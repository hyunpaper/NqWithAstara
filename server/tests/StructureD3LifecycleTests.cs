using System.Collections.Immutable;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §16B "시각과 재시작" + §10 상태 전이의 지속 규칙.
/// watermark seeding, 놓친 중간 봉, 90초 신선도, tombstone 되살리기 금지, 재시작 복원을 고정한다.
/// </summary>
public sealed class StructureD3LifecycleTests
{
    static readonly StructurePolicy P = D2.AllLongKinds;
    const int TriggerMinute = 30;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    /// <summary>D2의 예시 A 눌림 경로와 같은 형태. 여기서는 지속 상태만 검증한다.</summary>
    static ImmutableArray<StructureBar> PullbackBars()
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 25; m++) bars.Add(Bar(m, 99.90m, 100.10m, 100m, 100m));
        bars.Add(Bar(25, 99.30m, 99.90m, 99.85m, 99.35m));
        bars.Add(Bar(26, 99.15m, 99.45m, 99.35m, 99.25m));
        bars.Add(Bar(27, 99.35m, 99.55m, 99.30m, 99.50m));
        bars.Add(Bar(28, 99.50m, 99.70m, 99.50m, 99.60m));
        bars.Add(Bar(29, 99.55m, 99.65m, 99.60m, 99.60m));
        bars.Add(Bar(TriggerMinute, 99.58m, 99.85m, 99.60m, 99.80m, 2000));
        return bars.ToImmutable();
    }

    static SetupDetectionResult Detect(int? nowMinute = null)
    {
        var analysisAsOf = Fx.At(TriggerMinute + 1);
        var now = nowMinute is null ? analysisAsOf : Fx.At(nowMinute.Value);
        return SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, now, PullbackBars(), [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)],
            [D2.Episode("support-zone", 25, 28)], D2.Trend(), .20, 100.00m, now,
            D2.Quote(99.99m, 100.01m, TriggerMinute + 1)), P);
    }

    static StructuralLatch Fresh() => StructuralLatch.Empty(Fx.Symbol, Fx.SessionStart, P.PolicyHash);

    static TriggerGate Gate(StructuralLatch latch, int barStartMinute, int nowMinute) =>
        StructuralLifecycle.Gate(latch, Fx.At(barStartMinute), Fx.At(barStartMinute + 1), TimeSpan.FromMinutes(1),
            Fx.At(nowMinute), P);

    // ── watermark ──

    /// <summary>첫 시작/재시작에서는 최신 완료 봉까지 watermark만 설정하고 신규 트리거를 만들지 않는다(§16B).</summary>
    [Fact]
    public void FirstObservationSeedsWatermarkWithoutCreatingATrigger()
    {
        var gate = Gate(Fresh(), TriggerMinute, TriggerMinute + 1);
        Assert.False(gate.AllowNewTrigger);
        Assert.Contains(StructuralLifecycle.NoteWatermarkSeeded, gate.Notes);
        Assert.Equal(0, gate.MissedBars);

        var ready = Detect().Candidates;
        Assert.Contains(ready, x => x.Disposition == CandidateDisposition.Ready);
        var suppressed = StructuralLifecycle.ApplyLatch(Fresh(), ready, gate.AllowNewTrigger, P);
        Assert.All(suppressed, x => Assert.Equal(CandidateDisposition.Wait, x.Disposition));
        Assert.All(suppressed, x => Assert.Null(x.Plan));
        Assert.All(suppressed, x => Assert.Contains(StructuralLifecycle.CodeNewTriggerSuppressed, x.Notes));
        Assert.Null(CandidateSelection.SelectPreferred(suppressed));
    }

    [Fact]
    public void CommitAdvancesTheWatermarkAndTheNextBarBecomesEvaluable()
    {
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), ImmutableArray<EntryCandidate>.Empty,
            [], "sig", "obs");
        Assert.True(latch.Seeded);
        Assert.Equal(Fx.At(TriggerMinute), latch.WatermarkBarStart);

        Assert.True(Gate(latch, TriggerMinute + 1, TriggerMinute + 2).AllowNewTrigger);
        var repeat = Gate(latch, TriggerMinute, TriggerMinute + 1);
        Assert.False(repeat.AllowNewTrigger);
        Assert.True(repeat.AlreadyEvaluated);
        Assert.Contains(StructuralLifecycle.NoteBarAlreadyEvaluated, repeat.Notes);
    }

    /// <summary>놓친 중간 봉은 구조 복원에만 쓰고 신규 트리거를 만들지 않는다. 최신 봉 하나만 평가한다(§16B).</summary>
    [Fact]
    public void MissedIntermediateBarsAreReplayOnlyAndOnlyTheLatestBarIsEvaluated()
    {
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(20), ImmutableArray<EntryCandidate>.Empty, [], null, null);
        var gate = Gate(latch, 25, 26);
        Assert.True(gate.AllowNewTrigger);
        Assert.Equal(4, gate.MissedBars);
        Assert.Contains(StructuralLifecycle.NoteMissedBars, gate.Notes);
    }

    /// <summary>최신 완료 봉의 종료가 90초보다 오래됐으면 신규 후보 생성 금지다(§16B).</summary>
    [Fact]
    public void StaleLatestBarBlocksNewCandidatesEvenAfterTheWatermarkAdvanced()
    {
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute - 1),
            ImmutableArray<EntryCandidate>.Empty, [], null, null);
        var fresh = StructuralLifecycle.Gate(latch, Fx.At(TriggerMinute), Fx.At(TriggerMinute + 1),
            TimeSpan.FromMinutes(1), Fx.At(TriggerMinute + 1).AddSeconds(89), P);
        Assert.True(fresh.AllowNewTrigger);
        Assert.False(fresh.Stale);

        var stale = StructuralLifecycle.Gate(latch, Fx.At(TriggerMinute), Fx.At(TriggerMinute + 1),
            TimeSpan.FromMinutes(1), Fx.At(TriggerMinute + 1).AddSeconds(91), P);
        Assert.False(stale.AllowNewTrigger);
        Assert.True(stale.Stale);
        Assert.Contains(SetupDetector.BlockerStaleLatestBar, stale.Blockers);
    }

    [Fact]
    public void MissingCompletedBarProducesNoTriggerAtAll()
    {
        var gate = StructuralLifecycle.Gate(Fresh(), null, null, TimeSpan.FromMinutes(1), Fx.At(10), P);
        Assert.False(gate.AllowNewTrigger);
        Assert.Contains(SetupDetector.WarningNoTriggerBar, gate.Blockers);
    }

    // ── tombstone / 되살리기 금지 ──

    [Theory]
    [InlineData(CandidateDisposition.Rejected)]
    [InlineData(CandidateDisposition.Invalidated)]
    [InlineData(CandidateDisposition.Expired)]
    [InlineData(CandidateDisposition.Entered)]
    public void TombstonedEventIsNeverRevivedEvenWhenItRecomputesAsReady(CandidateDisposition terminal)
    {
        var computed = Detect().Candidates;
        var ready = computed.First(x => x.Disposition == CandidateDisposition.Ready);
        var latch = Fresh() with { Seeded = true, Tombstones = Fresh().Tombstones.SetItem(ready.EventId, terminal) };

        var applied = StructuralLifecycle.ApplyLatch(latch, computed, allowNewTrigger: true, P);
        var same = applied.First(x => x.EventId == ready.EventId);
        Assert.Equal(terminal, same.Disposition);
        Assert.Null(same.Plan);
        Assert.Contains(StructuralLifecycle.NoteTombstoned, same.Notes);
    }

    [Theory]
    [InlineData(CandidateDisposition.Rejected)]
    [InlineData(CandidateDisposition.Invalidated)]
    [InlineData(CandidateDisposition.Expired)]
    [InlineData(CandidateDisposition.Entered)]
    public void SuppressedNewTriggerKeepsAComputedTerminalDispositionInsteadOfWait(CandidateDisposition terminal)
    {
        var computed = Detect().Candidates;
        var target = computed.First(x => x.Disposition == CandidateDisposition.Ready);
        var withTerminal = computed
            .Select(x => x.EventId == target.EventId ? x with { Disposition = terminal } : x)
            .ToImmutableArray();

        var applied = StructuralLifecycle.ApplyLatch(Fresh(), withTerminal, allowNewTrigger: false, P);
        var same = applied.First(x => x.EventId == target.EventId);

        Assert.Equal(terminal, same.Disposition);
        Assert.Null(same.Plan);
        Assert.DoesNotContain(StructuralLifecycle.CodeNewTriggerSuppressed, same.Notes);
        Assert.All(applied.Where(x => x.EventId != target.EventId),
            x => Assert.Equal(CandidateDisposition.Wait, x.Disposition));
    }

    [Fact]
    public void SuppressedTerminalCandidateStillBecomesATombstoneOnCommit()
    {
        var computed = Detect().Candidates;
        var target = computed.First(x => x.Disposition == CandidateDisposition.Ready);
        var withTerminal = computed
            .Select(x => x.EventId == target.EventId
                ? x with { Disposition = CandidateDisposition.Invalidated }
                : x)
            .ToImmutableArray();

        var applied = StructuralLifecycle.ApplyLatch(Fresh(), withTerminal, allowNewTrigger: false, P);
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), applied, [], null, null);

        Assert.Equal(CandidateDisposition.Invalidated, latch.Tombstones[target.EventId]);
        Assert.DoesNotContain(target.DuplicateGuardKey, latch.ConsumedGuardKeys);
    }

    /// <summary>같은 중복 방지 키에서 이미 이벤트를 소비했다면 새 READY를 만들지 않는다(§8).</summary>
    [Fact]
    public void ConsumedDuplicateGuardKeyRejectsAnotherReadyForTheSameTrigger()
    {
        var computed = Detect().Candidates;
        var ready = computed.First(x => x.Disposition == CandidateDisposition.Ready);
        var latch = Fresh() with { Seeded = true, ConsumedGuardKeys = Fresh().ConsumedGuardKeys.Add(ready.DuplicateGuardKey) };

        var applied = StructuralLifecycle.ApplyLatch(latch, computed, allowNewTrigger: true, P);
        var guarded = applied.Where(x => x.DuplicateGuardKey == ready.DuplicateGuardKey).ToArray();
        Assert.NotEmpty(guarded);
        Assert.All(guarded, x => Assert.NotEqual(CandidateDisposition.Ready, x.Disposition));
        Assert.All(guarded, x => Assert.Contains(StructuralLifecycle.CodeDuplicateGuard, x.RejectionCodes));
    }

    [Fact]
    public void EnteredPullbackEpisodeIsConsumedAcrossRestartButADetectedNewEpisodeCanRearm()
    {
        var ready = Detect().Candidates.First(x => x.Kind == SetupKind.Pullback && x.Disposition == CandidateDisposition.Ready);
        var entered = ready with { Disposition = CandidateDisposition.Entered };
        var persisted = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), [entered], [], null, null);

        var replayCandidate = ready with { EventId = "same-episode-next-trigger", DuplicateGuardKey = "same-episode-next-trigger" };
        var replay = StructuralLifecycle.ApplyLatch(persisted, [replayCandidate], allowNewTrigger: true, P);
        var rejected = Assert.Single(replay);
        Assert.Equal(CandidateDisposition.Rejected, rejected.Disposition);
        Assert.Contains(StructuralLifecycle.CodeEpisodeConsumed, rejected.RejectionCodes);

        var bars = PullbackBars().ToBuilder();
        bars[28] = Bar(28, 99.30m, 99.40m, 99.35m, 99.35m);
        bars[30] = Bar(30, 99.55m, 99.65m, 99.60m, 99.60m);
        bars.Add(Bar(31, 99.58m, 99.90m, 99.60m, 99.80m, 2000));
        var analysisAsOf = Fx.At(32);
        var nextEpisode = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, bars.ToImmutable(),
            [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)],
            [D2.Episode("support-zone", 28, 30)], D2.Trend(), .20, 100.00m, analysisAsOf,
            D2.Quote(99.99m, 100.01m, 32)), P).Candidates.Single(x => x.Kind == SetupKind.Pullback);
        Assert.NotEqual(ready.EpisodeStartAt, nextEpisode.EpisodeStartAt);
        Assert.NotEqual(ready.EventId, nextEpisode.EventId);

        var rearmed = Assert.Single(StructuralLifecycle.ApplyLatch(persisted, [nextEpisode], allowNewTrigger: true, P));
        Assert.Equal(CandidateDisposition.Ready, rearmed.Disposition);
    }

    [Fact]
    public void CommitRecordsTombstonesAndGuardKeysOnlyForTheRelevantStates()
    {
        var computed = Detect().Candidates;
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), computed, ["zone-x"], "sig", "obs");
        foreach (var candidate in computed)
        {
            if (CandidateSelection.IsTerminal(candidate.Disposition))
                Assert.Equal(candidate.Disposition, latch.Tombstones[candidate.EventId]);
            else
                Assert.False(latch.Tombstones.ContainsKey(candidate.EventId));
        }
        Assert.Contains(computed.First(x => x.Disposition == CandidateDisposition.Ready).DuplicateGuardKey,
            latch.ConsumedGuardKeys);
        Assert.Contains("zone-x", latch.RetiredZoneIds);
    }

    [Fact]
    public void AnEntryBlockedReadyKeepsItsGuardKeyWhileTheOthersAreStillConsumed()
    {
        var computed = Detect().Candidates;
        var blocked = computed.First(x => x.Disposition == CandidateDisposition.Ready);
        var other = blocked with
        {
            EventId = blocked.EventId + "|other",
            DuplicateGuardKey = blocked.DuplicateGuardKey + "|other"
        };

        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), [blocked, other], [], "sig", "obs",
            [blocked.EventId]);

        Assert.DoesNotContain(blocked.DuplicateGuardKey, latch.ConsumedGuardKeys);
        Assert.Contains(other.DuplicateGuardKey, latch.ConsumedGuardKeys);

        var retried = StructuralLifecycle.ApplyLatch(latch with { Seeded = true }, [blocked],
            allowNewTrigger: true, P);
        Assert.Equal(CandidateDisposition.Ready, Assert.Single(retried).Disposition);
    }

    [Fact]
    public void AnEnteredCandidateIsStillConsumedAndRejectsAnotherReadyForTheSameTrigger()
    {
        var ready = Detect().Candidates.First(x => x.Kind == SetupKind.Pullback &&
                                                   x.Disposition == CandidateDisposition.Ready);
        var entered = ready with { Disposition = CandidateDisposition.Entered };
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), [entered], [], "sig", "obs", []);

        Assert.Contains(entered.DuplicateGuardKey, latch.ConsumedGuardKeys);
        var replay = Assert.Single(StructuralLifecycle.ApplyLatch(latch with { Seeded = true },
            [ready with { EventId = ready.EventId + "|same-trigger" }], allowNewTrigger: true, P));
        Assert.Equal(CandidateDisposition.Rejected, replay.Disposition);
        Assert.Contains(StructuralLifecycle.CodeDuplicateGuard, replay.RejectionCodes);
    }

    // ── 실시간 유지 조건 · 만료 ──

    /// <summary>표시 유지 중에도 live quote가 구조 손절을 깨면 즉시 INVALIDATED다. 재상승으로 되살리지 않는다(§10).</summary>
    [Fact]
    public void LivePriceBreakingTheStructuralStopInvalidatesImmediatelyAndDoesNotRecover()
    {
        var computed = Detect().Candidates;
        var stop = computed.First(x => x.Plan is not null).Plan!.Stop;

        var broken = StructuralLifecycle.ApplyLive(computed, stop, Fx.At(TriggerMinute + 1));
        Assert.All(broken.Where(x => x.Kind != SetupKind.Breakout), x =>
            Assert.Equal(CandidateDisposition.Invalidated, x.Disposition));
        Assert.All(broken.Where(x => x.Kind != SetupKind.Breakout), x => Assert.Null(x.Plan));
        Assert.All(broken.Where(x => x.Kind != SetupKind.Breakout), x =>
            Assert.Contains(StructuralLifecycle.NoteLiveInvalidated, x.Notes));

        var recovered = StructuralLifecycle.ApplyLive(broken, 100.50m, Fx.At(TriggerMinute + 1));
        Assert.All(recovered.Where(x => x.Kind != SetupKind.Breakout), x =>
            Assert.Equal(CandidateDisposition.Invalidated, x.Disposition));
    }

    /// <summary>EXPIRED는 현재 시각이 TriggerConfirmedAt+TTL 이상일 때다(§16B).</summary>
    [Fact]
    public void CandidateExpiresExactlyAtTheTtlBoundary()
    {
        var computed = Detect().Candidates;
        var expiresAt = computed[0].ExpiresAt;
        Assert.Equal(Fx.At(TriggerMinute + 1) + P.CandidateTtl(), expiresAt);

        Assert.All(StructuralLifecycle.ApplyLive(computed, 100.00m, expiresAt.AddTicks(-1)), x =>
            Assert.NotEqual(CandidateDisposition.Expired, x.Disposition));
        Assert.All(StructuralLifecycle.ApplyLive(computed, 100.00m, expiresAt), x =>
            Assert.Equal(CandidateDisposition.Expired, x.Disposition));
    }

    [Fact]
    public void LiveChecksNeverReviveATerminalCandidate()
    {
        var computed = Detect().Candidates;
        var terminal = computed.Select(x => x with { Disposition = CandidateDisposition.Invalidated, Plan = null })
            .ToImmutableArray();
        var applied = StructuralLifecycle.ApplyLive(terminal, 999m, Fx.At(TriggerMinute + 1));
        Assert.All(applied, x => Assert.Equal(CandidateDisposition.Invalidated, x.Disposition));
    }

    // ── 재시작 복원 ──

    /// <summary>watermark·tombstone·guard key·retired ID가 저장/복원을 왕복해도 같은 결론을 낸다(§16B).</summary>
    [Fact]
    public void LatchRoundTripsThroughStorageAndKeepsSuppressingTheSameEvent()
    {
        var computed = Detect().Candidates;
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), computed, ["retired-zone"],
            StructuralLifecycle.EventSignature(computed, null), "obs-1");

        var restored = Assert.Single(StructureLatchStorage.Parse(StructureLatchStorage.Serialize([latch])));
        Assert.Equal(latch.Symbol, restored.Symbol);
        Assert.Equal(latch.SessionStart, restored.SessionStart);
        Assert.Equal(latch.PolicyHash, restored.PolicyHash);
        Assert.Equal(latch.WatermarkBarStart, restored.WatermarkBarStart);
        Assert.True(restored.Seeded);
        Assert.Equal(latch.Tombstones.OrderBy(x => x.Key, StringComparer.Ordinal),
            restored.Tombstones.OrderBy(x => x.Key, StringComparer.Ordinal));
        Assert.Equal(latch.ConsumedGuardKeys.Order(StringComparer.Ordinal),
            restored.ConsumedGuardKeys.Order(StringComparer.Ordinal));
        Assert.Contains("retired-zone", restored.RetiredZoneIds);
        Assert.Equal(latch.LastEventSignature, restored.LastEventSignature);

        // 복원된 래치로는 같은 봉이 다시 신규 트리거가 되지 않는다.
        Assert.False(Gate(restored, TriggerMinute, TriggerMinute + 1).AllowNewTrigger);
    }

    [Fact]
    public void LatchIsNotInheritedAcrossSessionsOrPolicyChanges()
    {
        var latch = StructuralLifecycle.Commit(Fresh(), Fx.At(TriggerMinute), ImmutableArray<EntryCandidate>.Empty,
            [], null, null);
        Assert.True(latch.Matches(Fx.SessionStart, P.PolicyHash));
        Assert.False(latch.Matches(Fx.SessionStart.AddDays(1), P.PolicyHash));
        Assert.False(latch.Matches(Fx.SessionStart, (P with { MinimumNetR = 1.5 }).PolicyHash));
    }

    [Fact]
    public void ObservationIdIsStableAcrossProcessesAndSeparatesBarsAndPolicies()
    {
        var a = StructuralLifecycle.ObservationId(Fx.Symbol, Fx.At(30), P.PolicyHash);
        Assert.Equal(a, StructuralLifecycle.ObservationId(Fx.Symbol, Fx.At(30), P.PolicyHash));
        Assert.NotEqual(a, StructuralLifecycle.ObservationId(Fx.Symbol, Fx.At(31), P.PolicyHash));
        Assert.NotEqual(a, StructuralLifecycle.ObservationId("OTHER", Fx.At(30), P.PolicyHash));
        Assert.NotEqual(a, StructuralLifecycle.ObservationId(Fx.Symbol, Fx.At(30),
            (P with { MinimumNetR = 1.5 }).PolicyHash));
    }

    [Fact]
    public void EventSignatureChangesWithDispositionSoObservationDetailCanFollowIt()
    {
        var computed = Detect().Candidates;
        var before = StructuralLifecycle.EventSignature(computed, computed[0].EventId);
        var after = StructuralLifecycle.EventSignature(
            StructuralLifecycle.ApplyLive(computed, 1m, Fx.At(TriggerMinute + 1)), computed[0].EventId);
        Assert.NotEqual(before, after);
        Assert.Equal(before, StructuralLifecycle.EventSignature(computed.Reverse().ToImmutableArray(),
            computed[0].EventId));
    }
}
