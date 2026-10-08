using System.Collections.Immutable;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #47 / 설계 §10 "돌파 동일 ZoneId 재발동 쿨다운은 30분".
/// 계약: 같은 zone lineage의 BREAKOUT이 직전 발동(READY 성립)으로부터 정책 쿨다운 이내에 다시 트리거되면
/// READY로 승격하지 않고 <see cref="StructuralLifecycle.CodeBreakoutCooldown"/>으로 거절한다.
/// 비교 기준은 확정된 과거 시각(TriggerConfirmedAt)이며 현재 시각이 아니다(미래 누출 금지).
/// PULLBACK/REBOUND·다른 zone·다른 종목·새 세션은 대상이 아니다.
/// </summary>
public sealed class StructureBreakoutCooldownTests
{
    static readonly StructurePolicy P = StructurePolicy.Default with { ReboundMaxTrendAlignment = null, WindowBlockStartMinutesFromOpen = null, WindowBlockEndMinutesFromOpen = null };
    const string BreakoutZoneId = "breakout-zone";

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    /// <summary>resistance [99.90,100.10]을 triggerMinute 봉이 양봉으로 돌파하는 경로.</summary>
    static ImmutableArray<StructureBar> BreakoutBars(int triggerMinute)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < triggerMinute; m++) bars.Add(Bar(m, 99.70m, 100.05m, 99.80m, 100.00m));
        bars.Add(Bar(triggerMinute, 100.00m, 100.35m, 100.05m, 100.30m, 2000));
        return bars.ToImmutable();
    }

    static ImmutableArray<PriceZone> Zones(string breakoutZoneId = BreakoutZoneId, params string[] aliases)
    {
        var breakout = D2.Resistance(99.90m, 100.10m, id: breakoutZoneId);
        if (aliases.Length > 0) breakout = breakout with { Aliases = aliases.ToImmutableArray() };
        return [breakout, D2.Resistance(101.80m, 102.10m, id: "target-zone")];
    }

    /// <summary>지정한 완료 봉을 트리거로 BREAKOUT 후보를 계산한다. now=트리거 봉 종료라 신선도 차단이 없다.</summary>
    static ImmutableArray<EntryCandidate> Detect(int triggerMinute, ImmutableArray<PriceZone>? zones = null,
        string symbol = Fx.Symbol)
    {
        var analysisAsOf = Fx.At(triggerMinute + 1);
        return SetupDetector.Detect(SetupDetectionRequest.Create(symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, BreakoutBars(triggerMinute), zones ?? Zones(),
            ImmutableArray<TouchEpisode>.Empty, D2.Trend(), .20, 100.30m, analysisAsOf,
            D2.Quote(100.29m, 100.31m, triggerMinute + 1)), P).Candidates;
    }

    static EntryCandidate Breakout(ImmutableArray<EntryCandidate> candidates) =>
        candidates.Single(x => x.Kind == SetupKind.Breakout);

    static StructuralLatch Seeded(string symbol = Fx.Symbol) =>
        StructuralLatch.Empty(symbol, Fx.SessionStart, P.PolicyHash) with { Seeded = true };

    /// <summary>triggerMinute의 돌파 READY를 실제 commit 경로로 래치에 남긴다.</summary>
    static StructuralLatch CommitBreakout(StructuralLatch latch, int triggerMinute,
        ImmutableArray<PriceZone>? zones = null)
    {
        var candidates = Detect(triggerMinute, zones);
        Assert.Equal(CandidateDisposition.Ready, Breakout(candidates).Disposition);
        return StructuralLifecycle.Commit(latch, Fx.At(triggerMinute), candidates, [], "sig", "obs");
    }

    static string CooldownKey(EntryCandidate candidate) =>
        StructuralLifecycle.BreakoutCooldownKey(Fx.Symbol, Fx.SessionStart, candidate.ZoneId,
            candidate.TriggerConfirmedAt);

    // ── 기본 계약 ──

    [Fact]
    public void BaselineBreakoutWithoutAnyPriorActivationIsReady()
    {
        var applied = StructuralLifecycle.ApplyLatch(Seeded(), Detect(30), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(applied).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, Breakout(applied).RejectionCodes);
    }

    /// <summary>같은 ZoneId의 새 트리거라도 30분 이내면 READY로 승격하지 않는다(§10).</summary>
    [Fact]
    public void SameZoneBreakoutInsideTheCooldownIsRejectedWithTheCooldownCode()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var repeat = Detect(45);                                            // 15분 뒤 새 트리거 봉
        Assert.Equal(CandidateDisposition.Ready, Breakout(repeat).Disposition);

        var applied = StructuralLifecycle.ApplyLatch(latch, repeat, allowNewTrigger: true, P, Zones());
        var blocked = Breakout(applied);
        Assert.Equal(CandidateDisposition.Rejected, blocked.Disposition);
        Assert.Null(blocked.Plan);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, blocked.RejectionCodes);
        Assert.Null(CandidateSelection.SelectPreferred(applied));
    }

    /// <summary>경계 계약: 경과 &lt; 30분만 차단하고 정확히 30분이면 새 발동을 허용한다.</summary>
    [Fact]
    public void CooldownBoundaryBlocksBelowThirtyMinutesAndAllowsExactlyThirty()
    {
        var latch = CommitBreakout(Seeded(), 30);
        Assert.Equal(30, P.BreakoutCooldownMinutes);

        var justInside = StructuralLifecycle.ApplyLatch(latch, Detect(59), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Rejected, Breakout(justInside).Disposition);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, Breakout(justInside).RejectionCodes);

        // 트리거 봉 종료 기준이므로 60분 봉의 확정 시각은 직전 발동(31분)에서 정확히 30분 뒤다.
        var exactly = StructuralLifecycle.ApplyLatch(latch, Detect(60), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(exactly).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, Breakout(exactly).RejectionCodes);
    }

    [Fact]
    public void BreakoutIsReadyAgainOnceTheCooldownHasFullyElapsed()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var later = StructuralLifecycle.ApplyLatch(latch, Detect(120), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(later).Disposition);
        Assert.NotNull(Breakout(later).Plan);
    }

    /// <summary>쿨다운 값은 정책에서만 온다. 정책이 0이면 억제가 사라지고 길어지면 더 오래 막힌다(#47 "미사용 설정 금지").</summary>
    [Fact]
    public void CooldownLengthComesFromThePolicyValue()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var repeat = Detect(45);

        var none = StructuralLifecycle.ApplyLatch(latch, repeat, allowNewTrigger: true,
            P with { BreakoutCooldownMinutes = 0 }, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(none).Disposition);

        var longer = StructuralLifecycle.ApplyLatch(latch, Detect(120), allowNewTrigger: true,
            P with { BreakoutCooldownMinutes = 180 }, Zones());
        Assert.Equal(CandidateDisposition.Rejected, Breakout(longer).Disposition);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, Breakout(longer).RejectionCodes);
    }

    // ── 적용 범위 ──

    /// <summary>다른 ZoneId는 같은 종목·같은 시간대라도 영향받지 않는다.</summary>
    [Fact]
    public void ADifferentZoneIdIsNotBlockedByAnotherZonesCooldown()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var otherZones = Zones("other-breakout-zone");
        var applied = StructuralLifecycle.ApplyLatch(latch, Detect(45, otherZones), allowNewTrigger: true, P,
            otherZones);
        Assert.Equal(CandidateDisposition.Ready, Breakout(applied).Disposition);
    }

    /// <summary>PULLBACK/REBOUND는 §10이 돌파만 명시하므로 이 쿨다운의 대상이 아니다.</summary>
    [Theory]
    [InlineData(SetupKind.Pullback)]
    [InlineData(SetupKind.Rebound)]
    public void OtherSetupKindsAreNeverBlockedByTheBreakoutCooldown(SetupKind kind)
    {
        var breakout = Breakout(Detect(30));
        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [breakout], [], null, null);

        // 같은 zone·쿨다운 이내지만 종류만 다른 후보.
        var other = Breakout(Detect(45)) with { Kind = kind, KindName = SetupKinds.Name(kind) };
        var applied = StructuralLifecycle.ApplyLatch(latch, [other], allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, applied[0].Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, applied[0].RejectionCodes);
    }

    /// <summary>래치는 종목별이므로 다른 종목의 돌파는 막히지 않는다.</summary>
    [Fact]
    public void ADifferentSymbolKeepsItsOwnLatchAndIsNotBlocked()
    {
        CommitBreakout(Seeded(), 30);
        var applied = StructuralLifecycle.ApplyLatch(Seeded("OTHER"), Detect(45, symbol: "OTHER"),
            allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(applied).Disposition);
    }

    /// <summary>새 세션·정책 변경은 래치를 승계하지 않으므로 쿨다운도 이월되지 않는다(§16B).</summary>
    [Fact]
    public void CooldownDoesNotSurviveANewSessionOrAPolicyChange()
    {
        var latch = CommitBreakout(Seeded(), 30);
        Assert.True(latch.Matches(Fx.SessionStart, P.PolicyHash));
        Assert.False(latch.Matches(Fx.SessionStart.AddDays(1), P.PolicyHash));
        Assert.False(latch.Matches(Fx.SessionStart, (P with { BreakoutCooldownMinutes = 45 }).PolicyHash));

        var nextSession = StructuralLifecycle.ApplyLatch(
            StructuralLatch.Empty(Fx.Symbol, Fx.SessionStart.AddDays(1), P.PolicyHash) with { Seeded = true },
            Detect(45), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(nextSession).Disposition);
    }

    /// <summary>BreakoutCooldownMinutes는 정책 hash 입력이다. 값이 바뀌면 hash가 바뀌어 래치가 승계되지 않는다(§16A).</summary>
    [Fact]
    public void CooldownMinutesIsPartOfThePolicyHash()
    {
        Assert.NotEqual(P.PolicyHash, (P with { BreakoutCooldownMinutes = 45 }).PolicyHash);
        Assert.Equal(TimeSpan.FromMinutes(30), P.BreakoutCooldown());
    }

    // ── zone lineage ──

    /// <summary>
    /// 병합으로 대표 ID가 바뀌어도 흡수된 ID(Aliases)의 쿨다운을 이어 받는다. 이름만 바꿔 우회할 수 없다(§16B lineage).
    /// </summary>
    [Fact]
    public void MergedZoneLineageInheritsTheCooldownThroughAliases()
    {
        var latch = CommitBreakout(Seeded(), 30);                               // ZoneId=breakout-zone으로 기록

        var merged = Zones("merged-zone", BreakoutZoneId);                      // 대표 ID가 바뀌고 이전 ID는 alias
        var applied = StructuralLifecycle.ApplyLatch(latch, Detect(45, merged), allowNewTrigger: true, P, merged);
        Assert.Equal(CandidateDisposition.Rejected, Breakout(applied).Disposition);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, Breakout(applied).RejectionCodes);
    }

    /// <summary>lineage가 없는 무관한 새 ZoneId에는 이전 쿨다운을 전달하지 않는다(§16B).</summary>
    [Fact]
    public void AnUnrelatedNewZoneIdDoesNotInheritTheCooldown()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var unrelated = Zones("fresh-zone", "some-other-alias");
        var applied = StructuralLifecycle.ApplyLatch(latch, Detect(45, unrelated), allowNewTrigger: true, P, unrelated);
        Assert.Equal(CandidateDisposition.Ready, Breakout(applied).Disposition);
    }

    // ── commit 기록 · 재시작 ──

    /// <summary>쿨다운 표식은 돌파가 READY/ENTERED로 성립했을 때만 남는다. 거절·만료는 재발동을 막지 않는다.</summary>
    [Theory]
    [InlineData(CandidateDisposition.Ready, true)]
    [InlineData(CandidateDisposition.Entered, true)]
    [InlineData(CandidateDisposition.Rejected, false)]
    [InlineData(CandidateDisposition.Invalidated, false)]
    [InlineData(CandidateDisposition.Expired, false)]
    [InlineData(CandidateDisposition.Wait, false)]
    public void CommitRecordsTheCooldownOnlyForActivatedBreakouts(CandidateDisposition disposition, bool recorded)
    {
        var breakout = Breakout(Detect(30)) with { Disposition = disposition };
        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [breakout], [], null, null);
        Assert.Equal(recorded, latch.ConsumedGuardKeys.Contains(CooldownKey(breakout)));
    }

    /// <summary>
    /// 이슈 #71: 같은 trigger에서 여러 BREAKOUT 후보가 READY여도 대표로 선택된 zone만 발동으로 본다.
    /// 대표가 아닌 zone은 이후 독립 돌파 기회를 30분 cooldown으로 잃지 않아야 한다.
    /// </summary>
    [Fact]
    public void CommitRecordsTheCooldownOnlyForThePreferredReadyBreakout()
    {
        var selected = Breakout(Detect(30));
        var unselected = selected with
        {
            EventId = selected.EventId + "|unselected",
            ZoneId = "unselected-zone",
            EntryQuality = selected.EntryQuality!.Value - 1
        };

        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [unselected, selected], [], null, null);

        Assert.Equal(selected.EventId, CandidateSelection.SelectPreferred([unselected, selected])!.EventId);
        Assert.Contains(CooldownKey(selected), latch.ConsumedGuardKeys);
        Assert.DoesNotContain(CooldownKey(unselected), latch.ConsumedGuardKeys);

        var laterUnselectedZone = Zones("unselected-zone");
        var later = StructuralLifecycle.ApplyLatch(latch, Detect(45, laterUnselectedZone), allowNewTrigger: true, P,
            laterUnselectedZone);
        Assert.Equal(CandidateDisposition.Ready, Breakout(later).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, Breakout(later).RejectionCodes);
    }

    /// <summary>active 모드에서 대표 후보가 ENTERED로 커밋되어도 같은 zone만 cooldown을 소비한다.</summary>
    [Fact]
    public void CommitRecordsTheCooldownOnlyForThePreferredEnteredBreakout()
    {
        var selected = Breakout(Detect(30)) with { Disposition = CandidateDisposition.Entered };
        var unselected = selected with
        {
            EventId = selected.EventId + "|unselected",
            ZoneId = "unselected-zone",
            Disposition = CandidateDisposition.Ready,
            EntryQuality = selected.EntryQuality!.Value - 1
        };

        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [unselected, selected], [], null, null);

        Assert.Contains(CooldownKey(selected), latch.ConsumedGuardKeys);
        Assert.DoesNotContain(CooldownKey(unselected), latch.ConsumedGuardKeys);

        var laterUnselectedZone = Zones("unselected-zone");
        var later = StructuralLifecycle.ApplyLatch(latch, Detect(45, laterUnselectedZone), allowNewTrigger: true, P,
            laterUnselectedZone);
        Assert.Equal(CandidateDisposition.Ready, Breakout(later).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, Breakout(later).RejectionCodes);
    }

    [Fact]
    public void CommitRecordsTheBreakoutCooldownForItsOwnGuardKeyWhenAnotherKindIsAlsoReady()
    {
        var detected = Breakout(Detect(30));
        var breakout = detected with { EntryQuality = detected.EntryQuality!.Value - 1 };
        var pullback = detected with
        {
            EventId = detected.EventId + "|pullback",
            DuplicateGuardKey = SetupDetector.DuplicateGuardKey(Fx.Symbol, Fx.SessionStart, "PULLBACK",
                detected.TriggerBarStart),
            Kind = SetupKind.Pullback,
            KindName = "PULLBACK",
            ZoneId = "pullback-zone"
        };

        Assert.Equal(breakout.EventId, CandidateSelection.SelectPreferred([breakout, pullback])!.EventId);

        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [breakout, pullback], [], null, null);
        Assert.Contains(CooldownKey(breakout), latch.ConsumedGuardKeys);

        var repeat = StructuralLifecycle.ApplyLatch(latch, Detect(45), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Rejected, Breakout(repeat).Disposition);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, Breakout(repeat).RejectionCodes);
    }

    [Fact]
    public void AnEnteredBreakoutStaysTheActivationEvenWhenAReadyBreakoutScoresHigher()
    {
        var detected = Breakout(Detect(30));
        var entered = detected with
        {
            Disposition = CandidateDisposition.Entered,
            EntryQuality = detected.EntryQuality!.Value - 1
        };
        var readyHigher = detected with
        {
            EventId = detected.EventId + "|ready",
            ZoneId = "ready-zone",
            Disposition = CandidateDisposition.Ready
        };

        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [readyHigher, entered], [], null, null);

        Assert.Contains(CooldownKey(entered), latch.ConsumedGuardKeys);
        Assert.DoesNotContain(CooldownKey(readyHigher), latch.ConsumedGuardKeys);
    }

    [Fact]
    public void AnEntryBlockedBreakoutArmsNeitherTheCooldownNorTheDuplicateGuard()
    {
        var candidates = Detect(30);
        var breakout = Breakout(candidates);
        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), candidates, [], "sig", "obs",
            [breakout.EventId]);

        Assert.DoesNotContain(CooldownKey(breakout), latch.ConsumedGuardKeys);
        Assert.DoesNotContain(breakout.DuplicateGuardKey, latch.ConsumedGuardKeys);

        var repeat = StructuralLifecycle.ApplyLatch(latch, Detect(45), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(repeat).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, Breakout(repeat).RejectionCodes);

        var same = StructuralLifecycle.ApplyLatch(latch, candidates, allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(same).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeDuplicateGuard, Breakout(same).RejectionCodes);
    }

    /// <summary>PULLBACK READY는 중복 방지 키만 남기고 돌파 쿨다운 표식을 남기지 않는다.</summary>
    [Fact]
    public void CommitDoesNotRecordACooldownForNonBreakoutCandidates()
    {
        var pullback = Breakout(Detect(30)) with { Kind = SetupKind.Pullback, KindName = "PULLBACK" };
        var latch = StructuralLifecycle.Commit(Seeded(), Fx.At(30), [pullback], [], null, null);
        Assert.Contains(pullback.DuplicateGuardKey, latch.ConsumedGuardKeys);
        Assert.DoesNotContain(latch.ConsumedGuardKeys,
            x => x.StartsWith(StructuralLifecycle.BreakoutCooldownKeyPrefix, StringComparison.Ordinal));
    }

    /// <summary>쿨다운 표식은 §8 중복 방지 키와 형식이 겹치지 않아 기존 guard 판정을 오염시키지 않는다.</summary>
    [Fact]
    public void CooldownMarkerDoesNotCollideWithTheDuplicateGuardKey()
    {
        var breakout = Breakout(Detect(30));
        var key = StructuralLifecycle.BreakoutCooldownKey(Fx.Symbol, Fx.SessionStart, breakout.ZoneId,
            breakout.TriggerConfirmedAt);
        Assert.NotEqual(breakout.DuplicateGuardKey, key);
        Assert.NotEqual(breakout.EventId, key);

        // 쿨다운 표식만 있는 래치는 중복 방지 판정을 발동시키지 않는다.
        var latch = Seeded() with { ConsumedGuardKeys = Seeded().ConsumedGuardKeys.Add(key) };
        var applied = StructuralLifecycle.ApplyLatch(latch, Detect(120), allowNewTrigger: true, P, Zones());
        Assert.DoesNotContain(StructuralLifecycle.CodeDuplicateGuard, Breakout(applied).RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, Breakout(applied).Disposition);
    }

    /// <summary>
    /// 재시작 복원: 쿨다운은 기존 래치 저장 경로(ConsumedGuardKeys)로 왕복하므로 프로세스가 재시작해도 같은 결론이다.
    /// </summary>
    [Fact]
    public void CooldownSurvivesTheLatchStorageRoundTrip()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var restored = Assert.Single(StructureLatchStorage.Parse(StructureLatchStorage.Serialize([latch])));
        Assert.Equal(latch.ConsumedGuardKeys.Order(StringComparer.Ordinal),
            restored.ConsumedGuardKeys.Order(StringComparer.Ordinal));

        var applied = StructuralLifecycle.ApplyLatch(restored, Detect(45), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Rejected, Breakout(applied).Disposition);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, Breakout(applied).RejectionCodes);

        var afterCooldown = StructuralLifecycle.ApplyLatch(restored, Detect(120), allowNewTrigger: true, P, Zones());
        Assert.Equal(CandidateDisposition.Ready, Breakout(afterCooldown).Disposition);
    }

    /// <summary>같은 봉을 여러 번 poll해도(신규 트리거 금지) 쿨다운이 자기 자신을 막지 않는다.</summary>
    [Fact]
    public void RepeatedPollOfTheSameBarIsSuppressedByTheGateNotByTheCooldown()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var gate = StructuralLifecycle.Gate(latch, Fx.At(30), Fx.At(31), TimeSpan.FromMinutes(1), Fx.At(31), P);
        Assert.False(gate.AllowNewTrigger);
        Assert.True(gate.AlreadyEvaluated);

        var applied = StructuralLifecycle.ApplyLatch(latch, Detect(30), gate.AllowNewTrigger, P, Zones());
        Assert.Equal(CandidateDisposition.Wait, Breakout(applied).Disposition);
        Assert.DoesNotContain(StructuralLifecycle.CodeBreakoutCooldown, Breakout(applied).RejectionCodes);
    }

    /// <summary>zone 스냅샷을 넘기지 않아도 자기 ZoneId 기준 판정은 그대로 동작한다.</summary>
    [Fact]
    public void CooldownStillAppliesWhenNoZoneSnapshotIsSupplied()
    {
        var latch = CommitBreakout(Seeded(), 30);
        var applied = StructuralLifecycle.ApplyLatch(latch, Detect(45), allowNewTrigger: true, P);
        Assert.Equal(CandidateDisposition.Rejected, Breakout(applied).Disposition);
        Assert.Contains(StructuralLifecycle.CodeBreakoutCooldown, Breakout(applied).RejectionCodes);
    }
}
