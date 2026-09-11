using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §8 + §10 + §16B 후보·상태·대표 선택. 트리거 봉은 구조 증거가 아니며(§16B),
/// 되살리기 금지와 예시 D의 INVALIDATED tombstone을 고정한다.
/// </summary>
public sealed class StructureD2CandidateTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;
    const int TriggerMinute = 30;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    /// <summary>
    /// 예시 A와 같은 형태의 눌림 경로: 100.00 부근 → support [99.20,99.40] 접촉(저점 99.15) → 회복 → 트리거.
    /// </summary>
    static ImmutableArray<StructureBar> PullbackBars(decimal episodeLow = 99.15m, decimal triggerLow = 99.58m,
        double triggerVolume = 2000, decimal? triggerClose = null)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 25; m++) bars.Add(Bar(m, 99.90m, 100.10m, 100m, 100m));
        bars.Add(Bar(25, 99.30m, 99.90m, 99.85m, 99.35m));                       // 지지 접촉 시작
        bars.Add(Bar(26, episodeLow, 99.45m, 99.35m, 99.25m));                   // episode 저점
        bars.Add(Bar(27, 99.35m, 99.55m, 99.30m, 99.50m));                       // 아직 구간 접촉
        bars.Add(Bar(28, 99.50m, 99.70m, 99.50m, 99.60m));                       // 구간 이탈 1
        bars.Add(Bar(29, 99.55m, 99.65m, 99.60m, 99.60m));                       // 구간 이탈 2
        bars.Add(Bar(TriggerMinute, triggerLow, 99.85m, 99.60m, triggerClose ?? 99.80m, triggerVolume));
        return bars.ToImmutable();
    }

    static ImmutableArray<StructureBar> BreakoutBars(decimal resistanceUpper, decimal triggerClose,
        double triggerVolume = 2000)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < TriggerMinute; m++)
            bars.Add(Bar(m, resistanceUpper - .40m, resistanceUpper - .05m, resistanceUpper - .30m, resistanceUpper - .10m));
        bars.Add(Bar(TriggerMinute, resistanceUpper - .10m, triggerClose + .05m, resistanceUpper - .05m,
            triggerClose, triggerVolume));
        return bars.ToImmutable();
    }

    static SetupDetectionRequest Request(ImmutableArray<StructureBar> bars, ImmutableArray<PriceZone> zones,
        ImmutableArray<TouchEpisode> episodes, TrendAssessment? trend = null, decimal? live = 100.00m,
        int? nowMinute = null, StructureLiquidity? liquidity = null, double? atr = .20,
        DateTimeOffset? quoteAt = null, DateTimeOffset? sessionEnd = null)
    {
        var analysisAsOf = Fx.At(TriggerMinute + 1);
        var now = nowMinute is null ? analysisAsOf : Fx.At(nowMinute.Value);
        return SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, sessionEnd ?? Fx.SessionEnd, analysisAsOf,
            now, bars, zones, episodes, trend ?? D2.Trend(), atr, live, quoteAt ?? now,
            liquidity ?? D2.Quote(99.99m, 100.01m, TriggerMinute + 1));
    }

    static ImmutableArray<PriceZone> PullbackZones() =>
        [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)];

    static ImmutableArray<TouchEpisode> PullbackEpisodes() =>
        [D2.Episode("support-zone", 25, 28)];

    // ── PULLBACK ──

    /// <summary>후보 경로 전체가 §14 A의 구조 계획(Stop 99.12 / Target 101.78 / netR≈1.418)을 재현한다.</summary>
    [Fact]
    public void PullbackReadyReproducesExampleAEndToEnd()
    {
        var result = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes()), P);
        var candidate = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Pullback));

        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.Equal(99.15m, candidate.InvalidationAnchor);
        Assert.NotNull(candidate.Plan);
        Assert.Equal(100.00m, candidate.Plan!.EntryReference);
        Assert.Equal(99.12m, candidate.Plan.Stop);
        Assert.Equal(101.78m, candidate.Plan.Target);
        Assert.Equal(1.56m, candidate.Plan.NetReward);
        Assert.Equal(1.10m, candidate.Plan.NetRisk);
        Assert.Equal(1.418m, Math.Round(candidate.Plan.NetR, 3));
        Assert.Empty(candidate.RejectionCodes);
        Assert.True(candidate.EntryQuality > 0);
        Assert.Equal(CandidateDisposition.Ready, result.Summary);
        // 예시 A의 눌림 저점(99.15)은 지지 하단(99.20) 아래이므로 실패한 이탈로도 읽힌다.
        // 두 종류 후보가 같은 구조 계획을 만들고 대표는 정렬 규칙이 하나만 고른다(§8).
        Assert.Equal(2, result.Candidates.Length);
        Assert.All(result.Candidates, x => Assert.Equal(99.12m, x.Plan!.Stop));
        Assert.All(result.Candidates, x => Assert.Equal(101.78m, x.Plan!.Target));
        Assert.NotNull(result.Preferred);
        Assert.Equal(result.Candidates.Max(x => x.EntryQuality), result.Preferred!.EntryQuality);
        Assert.Equal(Fx.At(TriggerMinute), candidate.StructureCutoff);
        Assert.Equal(Fx.At(TriggerMinute + 1), candidate.TriggerConfirmedAt);
        Assert.Equal(Fx.At(TriggerMinute + 1) + P.CandidateTtl(), candidate.ExpiresAt);
        // 트리거 거래량은 품질에만 쓰인다: RV=2000/1000=2 → 2/(1+2)
        Assert.Equal(2.0 / 3, candidate.Quality.Components.Single(x => x.Name == "triggerVolumeQuality").Value!.Value, 12);
    }

    /// <summary>§8/§16B: 트리거 자체의 저가로 anchor를 넓히지 않는다.</summary>
    [Fact]
    public void TriggerBarLowNeverWidensTheInvalidationAnchor()
    {
        var result = SetupDetector.Detect(
            Request(PullbackBars(triggerLow: 98.50m), PullbackZones(), PullbackEpisodes()), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(99.15m, candidate.InvalidationAnchor);          // 98.50이 아니다
        Assert.Contains(SetupDetector.NoteChaseTriggerBelowAnchor, candidate.Notes);
        Assert.Equal(99.12m, candidate.Planning.Stop);
    }

    /// <summary>뒤에서 생긴 더 낮은 저점을 끌어다 넣어 손절을 계속 내리지 않는다(§8).</summary>
    [Fact]
    public void OnlyTheOwnEpisodeLowFeedsTheAnchor()
    {
        // 같은 구간의 더 이른 별개 episode가 더 낮은 저점을 갖고 있어도 최신 눌림 episode만 쓴다.
        var bars = PullbackBars().ToBuilder();
        bars[5] = Bar(5, 98.60m, 99.40m, 99.30m, 99.35m);            // 이른 접촉(더 낮은 저점)
        var episodes = ImmutableArray.Create(D2.Episode("support-zone", 5, 8), D2.Episode("support-zone", 25, 28));
        var result = SetupDetector.Detect(Request(bars.ToImmutable(), PullbackZones(), episodes), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(Fx.At(25), candidate.EpisodeStartAt);
        Assert.Equal(99.15m, candidate.InvalidationAnchor);
    }

    [Fact]
    public void PullbackRequiresUpOrTransitionAndProducesNoCandidateInRange()
    {
        foreach (var state in new[] { TrendState.Range, TrendState.Down, TrendState.Unknown })
        {
            var result = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
                D2.Trend(state, state == TrendState.Unknown ? null : 10)), P);
            Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Pullback);
        }

        foreach (var state in new[] { TrendState.Up, TrendState.Transition })
        {
            var result = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
                D2.Trend(state, 10)), P);
            Assert.Contains(result.Candidates, x => x.Kind == SetupKind.Pullback);
        }
    }

    [Fact]
    public void PullbackNeedsAConfirmedTouchEpisodeOnTheSupport()
    {
        var result = SetupDetector.Detect(
            Request(PullbackBars(), PullbackZones(), ImmutableArray<TouchEpisode>.Empty), P);

        Assert.Empty(result.Candidates);
        Assert.Equal(CandidateDisposition.Wait, result.Summary);
        Assert.Null(result.PreferredCandidateId);
    }

    [Fact]
    public void IneligibleOrBrokenSupportCannotAnchorACandidate()
    {
        var ineligible = D2.Zone("support-zone", 99.20m, 99.40m, ZoneRole.Support, .2, eligible: false);
        var broken = D2.Zone("support-zone", 99.20m, 99.40m, ZoneRole.Broken, .8);
        var retired = D2.Zone("support-zone", 99.20m, 99.40m, ZoneRole.Support, .8, retired: true);

        foreach (var zone in new[] { ineligible, broken, retired })
        {
            var result = SetupDetector.Detect(
                Request(PullbackBars(), [zone, D2.Resistance(101.80m, 102.10m)], PullbackEpisodes()), P);
            Assert.Empty(result.Candidates);
        }
    }

    [Fact]
    public void TriggerMustCloseAboveThePreviousHighAndTheSupportUpper()
    {
        // 직전 봉 High(99.65) 아래에서 마감하면 트리거가 아니다.
        var weak = SetupDetector.Detect(
            Request(PullbackBars(triggerClose: 99.62m), PullbackZones(), PullbackEpisodes()), P);
        Assert.Empty(weak.Candidates);

        // support Upper(99.40) 아래에서 마감하면 회복이 아니다.
        var bars = PullbackBars().ToBuilder();
        bars[29] = Bar(29, 99.10m, 99.20m, 99.15m, 99.15m);
        bars[TriggerMinute] = Bar(TriggerMinute, 99.15m, 99.35m, 99.15m, 99.30m, 2000);
        var below = SetupDetector.Detect(Request(bars.ToImmutable(), PullbackZones(), PullbackEpisodes()), P);
        Assert.Empty(below.Candidates);
    }

    /// <summary>실시간 가격이 지지 하단 아래면 INVALIDATED이고 신규 진입은 없다(§8).</summary>
    [Fact]
    public void LivePriceBelowSupportLowerInvalidatesThePullback()
    {
        var result = SetupDetector.Detect(
            Request(PullbackBars(), PullbackZones(), PullbackEpisodes(), live: 99.10m), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(CandidateDisposition.Invalidated, candidate.Disposition);
        Assert.Null(candidate.Plan);
        Assert.Null(result.PreferredCandidateId);
        Assert.Contains(SetupDetector.NoteLiveBelowSupportLower, candidate.Notes);
    }

    // ── BREAKOUT ──

    [Fact]
    public void BreakoutNeedsABullishCloseAboveThePreTriggerResistanceSnapshot()
    {
        var resistance = D2.Resistance(99.90m, 100.10m, id: "breakout-zone");
        var zones = ImmutableArray.Create(resistance, D2.Resistance(101.80m, 102.10m, id: "target-zone"));
        var bars = BreakoutBars(100.10m, 100.30m);

        var ready = SetupDetector.Detect(Request(bars, zones, ImmutableArray<TouchEpisode>.Empty,
            live: 100.30m, liquidity: D2.Quote(100.29m, 100.31m, TriggerMinute + 1)), P);
        var candidate = ready.Candidates.Single(x => x.Kind == SetupKind.Breakout);

        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.Equal(99.90m, candidate.InvalidationAnchor);                 // resistance Lower
        Assert.Equal("breakout-zone", candidate.ZoneId);
        Assert.Equal("target-zone", candidate.Plan!.TargetZoneSnapshot.Id);
        Assert.Equal(101.78m, candidate.Plan.Target);
        Assert.False(candidate.RetestConfirmed);
        Assert.Contains(SetupDetector.NoteRetestPending, candidate.Notes);
        Assert.False(candidate.CounterTrend);

        // 음봉이면 후보가 아니다.
        var bearish = bars.SetItem(TriggerMinute,
            Bar(TriggerMinute, 100.00m, 100.40m, 100.35m, 100.30m, 2000));
        Assert.DoesNotContain(SetupDetector.Detect(Request(bearish, zones,
            ImmutableArray<TouchEpisode>.Empty, live: 100.30m), P).Candidates, x => x.Kind == SetupKind.Breakout);
    }

    [Fact]
    public void BreakoutIsSkippedWhenThePreviousBarAlreadyClosedAboveTheZone()
    {
        var resistance = D2.Resistance(99.90m, 100.10m, id: "breakout-zone");
        var bars = BreakoutBars(100.10m, 100.30m).ToBuilder();
        bars[TriggerMinute - 1] = Bar(TriggerMinute - 1, 100.10m, 100.25m, 100.12m, 100.20m);

        var result = SetupDetector.Detect(Request(bars.ToImmutable(),
            [resistance, D2.Resistance(101.80m, 102.10m, id: "target-zone")],
            ImmutableArray<TouchEpisode>.Empty, live: 100.30m), P);

        Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Breakout);
    }

    /// <summary>§14 D: 돌파 후 즉시 되밀림. 완료 봉만 보고 READY를 만들지 않고 결과는 INVALIDATED tombstone이다.</summary>
    [Fact]
    public void ExampleDBreakoutPushedBackIsInvalidatedNotWaitAndNotReady()
    {
        var resistance = D2.Resistance(100.80m, 101.00m, id: "breakout-zone");
        var zones = ImmutableArray.Create(resistance, D2.Resistance(102.80m, 103.10m, id: "target-zone"));
        var bars = BreakoutBars(101.00m, 101.10m);

        var result = SetupDetector.Detect(Request(bars, zones, ImmutableArray<TouchEpisode>.Empty,
            live: 100.95m, liquidity: D2.Quote(100.94m, 100.96m, TriggerMinute + 1)), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Breakout);

        Assert.Equal(101.10m, bars[TriggerMinute].Close);
        Assert.Equal(CandidateDisposition.Invalidated, candidate.Disposition);
        Assert.NotEqual(CandidateDisposition.Wait, candidate.Disposition);
        Assert.NotEqual(CandidateDisposition.Ready, candidate.Disposition);
        Assert.Null(candidate.Plan);
        Assert.Null(result.PreferredCandidateId);
        Assert.Contains(SetupDetector.NoteLiveBelowBreakoutLevel, candidate.Notes);
    }

    /// <summary>§10/§16B: 재상승했다고 같은 이벤트를 되살리지 않는다. 새 트리거만 새 이벤트다.</summary>
    [Fact]
    public void AnInvalidatedEventIsNeverRevivedByARecoveringQuote()
    {
        var resistance = D2.Resistance(100.80m, 101.00m, id: "breakout-zone");
        var zones = ImmutableArray.Create(resistance, D2.Resistance(102.80m, 103.10m, id: "target-zone"));
        var bars = BreakoutBars(101.00m, 101.10m);

        var invalidated = SetupDetector.Detect(Request(bars, zones, ImmutableArray<TouchEpisode>.Empty,
            live: 100.95m), P).Candidates.Single();
        var recovered = SetupDetector.Detect(Request(bars, zones, ImmutableArray<TouchEpisode>.Empty,
            live: 101.20m, liquidity: D2.Quote(101.19m, 101.21m, TriggerMinute + 1)), P).Candidates.Single();

        Assert.Equal(invalidated.EventId, recovered.EventId);
        Assert.Equal(CandidateDisposition.Ready, recovered.Disposition);       // 계산만 보면 유효해 보인다
        var reconciled = CandidateSelection.Reconcile(invalidated, recovered);
        Assert.Equal(CandidateDisposition.Invalidated, reconciled.Disposition);
        Assert.Null(reconciled.Plan);
        Assert.Null(CandidateSelection.SelectPreferred([reconciled]));
    }

    [Theory]
    [InlineData(CandidateDisposition.Rejected)]
    [InlineData(CandidateDisposition.Invalidated)]
    [InlineData(CandidateDisposition.Expired)]
    [InlineData(CandidateDisposition.Entered)]
    public void TerminalDispositionsAreNeverRevived(CandidateDisposition terminal)
    {
        Assert.True(CandidateSelection.IsTerminal(terminal));
        Assert.Equal(terminal, CandidateSelection.Reconcile(terminal, CandidateDisposition.Ready));
    }

    [Fact]
    public void WaitAndReadyStatesStillProgress()
    {
        Assert.False(CandidateSelection.IsTerminal(CandidateDisposition.Wait));
        Assert.Equal(CandidateDisposition.Ready,
            CandidateSelection.Reconcile(CandidateDisposition.Wait, CandidateDisposition.Ready));
        Assert.Equal(CandidateDisposition.Invalidated,
            CandidateSelection.Reconcile(CandidateDisposition.Ready, CandidateDisposition.Invalidated));
    }

    // ── REBOUND ──

    /// <summary>실패한 하향 이탈(꼬리로만 이탈) 후 회복. 추세 상태를 요구하지 않는다(§8).</summary>
    [Fact]
    public void ReboundFiresOnAFailedBreakdownWithoutRequiringAnUptrend()
    {
        // episode 저점이 지지 하단(99.20) 아래지만 완료 종가는 하단 위에서 마감했다.
        var bars = PullbackBars(episodeLow: 99.10m);
        var result = SetupDetector.Detect(Request(bars, PullbackZones(), PullbackEpisodes(),
            D2.Trend(TrendState.Range, -30)), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);

        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.True(candidate.CounterTrend);
        Assert.Contains(SetupDetector.NoteCounterTrend, candidate.Notes);
        Assert.Equal(99.10m, candidate.InvalidationAnchor);
        Assert.Equal(99.07m, candidate.Plan!.Stop);            // floorToCent(99.10-0.03)
        Assert.DoesNotContain(candidate.Quality.UsedComponents, x => x == "alignmentQuality");
        Assert.Contains("reclaimQuality", candidate.Quality.UsedComponents);
        // 추세 점수가 낮다는 이유만으로 거절되지 않는다.
        Assert.Empty(candidate.RejectionCodes);
    }

    [Fact]
    public void ReboundDoesNotFireWithoutAFailedBreakdownEpisode()
    {
        // episode 저점이 지지 구간 안(99.25)이면 이탈 시도가 없었다 → 눌림이지 반등이 아니다.
        var result = SetupDetector.Detect(Request(PullbackBars(episodeLow: 99.25m), PullbackZones(),
            PullbackEpisodes(), D2.Trend(TrendState.Range, -30)), P);

        Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Rebound);
    }

    [Fact]
    public void AClosedBreakdownIsNotAFailedBreakdown()
    {
        // 완료 종가가 하단 아래로 마감했다면 그 구간은 붕괴이며 REBOUND 근거가 아니다(§16B 부활 금지).
        var bars = PullbackBars(episodeLow: 99.10m).ToBuilder();
        bars[26] = Bar(26, 99.10m, 99.45m, 99.35m, 99.15m);      // Close < Lower
        var result = SetupDetector.Detect(Request(bars.ToImmutable(), PullbackZones(), PullbackEpisodes(),
            D2.Trend(TrendState.Range, -30)), P);

        Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Rebound);
    }

    // ── 상태·시간 규칙 ──

    /// <summary>
    /// §16B: EXPIRED는 현재 시각&gt;=TriggerConfirmedAt+5분이다.
    /// 기본 정책에서는 90초 최신 봉 규칙이 먼저 걸려 신규 후보 자체가 생기지 않으므로
    /// 여기서는 봉 신선도 제한만 완화해 만료 판정 자체를 고정한다.
    /// </summary>
    [Fact]
    public void ExpiryUsesTriggerConfirmationPlusTheCandidateTtl()
    {
        var policy = P with { LatestBarMaxAgeSeconds = 3600 };
        var expiredAt = TriggerMinute + 1 + policy.CandidateTtlMinutes;
        var result = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
            nowMinute: expiredAt, liquidity: D2.Quote(99.99m, 100.01m, expiredAt),
            quoteAt: Fx.At(expiredAt)), policy);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(CandidateDisposition.Expired, candidate.Disposition);
        Assert.Null(candidate.Plan);
        Assert.Null(result.PreferredCandidateId);

        // 만료 직전(4분 59초)에는 만료가 아니다.
        var justInTime = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
            liquidity: D2.Quote(99.99m, 100.01m, TriggerMinute + 1),
            quoteAt: Fx.At(expiredAt).AddSeconds(-1)) with { Now = Fx.At(expiredAt).AddSeconds(-1) }, policy);
        Assert.Equal(CandidateDisposition.Ready,
            justInTime.Candidates.Single(x => x.Kind == SetupKind.Pullback).Disposition);
    }

    /// <summary>§16B: 최신 완료 봉이 90초보다 오래됐으면 신규 후보를 만들지 않는다.</summary>
    [Fact]
    public void AStaleLatestBarStopsNewCandidateGeneration()
    {
        var request = Request(PullbackBars(), PullbackZones(), PullbackEpisodes()) with
        {
            Now = Fx.At(TriggerMinute + 1).AddSeconds(91)
        };
        var result = SetupDetector.Detect(request, P);

        Assert.Empty(result.Candidates);
        Assert.Contains(SetupDetector.BlockerStaleLatestBar, result.ReadyBlockers);
    }

    /// <summary>§9.3: 정규장 마감 40분 전 신규 진입 금지는 v4 운영 제한을 유지한다.</summary>
    [Fact]
    public void NewEntriesAreBlockedInsideTheClosingCutoff()
    {
        var result = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
            sessionEnd: Fx.At(TriggerMinute + 20)), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Contains(SetupDetector.BlockerAfterEntryCutoff, candidate.RejectionCodes);
        Assert.Null(candidate.Plan);
    }

    [Fact]
    public void StaleOrMissingQuotesBlockReadyAndAreReportedExplicitly()
    {
        var stale = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
            quoteAt: Fx.At(TriggerMinute + 1).AddSeconds(-16)), P).Candidates.First(x => x.Kind == SetupKind.Pullback);
        Assert.Equal(CandidateDisposition.Rejected, stale.Disposition);
        Assert.Contains(SetupDetector.BlockerStaleQuote, stale.RejectionCodes);

        var missing = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(),
            live: null), P).Candidates.First(x => x.Kind == SetupKind.Pullback);
        Assert.Equal(CandidateDisposition.Rejected, missing.Disposition);
        Assert.Contains(SetupDetector.BlockerMissingQuote, missing.RejectionCodes);
        Assert.Contains(SetupDetector.NoteEntryFromTriggerClose, missing.Notes);
        Assert.Equal(99.80m, missing.EntryReference);                     // 트리거 종가를 참고값으로만 쓴다
    }

    /// <summary>
    /// §16B: 5m 구조 결측은 READY 차단이며 구체적 사유를 남긴다.
    /// [#33(D7)에서 기대값 갱신] 차단은 alignmentQuality가 필수인 유형(PULLBACK/BREAKOUT)으로 한정됐다.
    /// 이 fixture의 눌림 저점(99.15)은 지지 하단 아래라 REBOUND도 함께 생기는데, REBOUND는 §8/§9.4에 따라
    /// 이 차단의 스코프 밖이므로 코호트 note를 달고 READY(대표 후보)가 된다.
    /// </summary>
    [Fact]
    public void MissingFiveMinuteStructureBlocksReadyWithAReason()
    {
        var trend = D2.Trend(TrendState.Up, 40, structureDirection: null,
            readyBlockers: TrendEvaluator.BlockerMissing5mStructure);
        var result = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes(), trend), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, candidate.RejectionCodes);

        var rebound = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
        Assert.Contains(SetupDetector.NoteReadyWithout5mStructure, rebound.Notes);
        Assert.Equal(rebound.EventId, result.PreferredCandidateId);
    }

    [Fact]
    public void NoCompletedTriggerBarMeansWaitRatherThanAFabricatedCandidate()
    {
        var request = Request(PullbackBars(), PullbackZones(), PullbackEpisodes()) with
        {
            AnalysisAsOf = Fx.At(TriggerMinute + 5)
        };
        var result = SetupDetector.Detect(request, P);

        Assert.Empty(result.Candidates);
        Assert.Contains(SetupDetector.WarningNoTriggerBar, result.Warnings);
        Assert.Equal(CandidateDisposition.Wait, result.Summary);
    }

    [Fact]
    public void MissingTargetStructureRejectsEvenWithAValidTrigger()
    {
        var result = SetupDetector.Detect(Request(PullbackBars(), [D2.Support(99.20m, 99.40m)],
            PullbackEpisodes()), P);
        var candidate = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);

        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Contains(StructuralPlanner.NoTargetStructure, candidate.RejectionCodes);
        Assert.Null(candidate.Plan);
        Assert.Null(candidate.Planning.Target);
        Assert.Null(result.PreferredCandidateId);
    }

    /// <summary>§16A: 트리거 상대 거래량의 분모가 0이면 NO_VOLUME_BASELINE이고 신규 진입은 보류한다.</summary>
    [Fact]
    public void AZeroVolumeBaselineBlocksReadyWithNoVolumeBaseline()
    {
        var bars = PullbackBars().Select(x => x.Start == Fx.At(TriggerMinute)
            ? x
            : x with { Volume = 0 }).ToImmutableArray();
        var result = SetupDetector.Detect(Request(bars, PullbackZones(), PullbackEpisodes()), P);
        var candidate = result.Candidates.First();

        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Contains(EntryQualityEvaluator.ReasonNoVolumeBaseline, candidate.RejectionCodes);
        Assert.Null(candidate.EntryQuality);
        Assert.Null(candidate.Plan);
        Assert.Null(result.PreferredCandidateId);
    }

    /// <summary>§15 prefix invariance: 미래 봉을 덧붙여도 cutoff 기준 결과는 그대로다.</summary>
    [Fact]
    public void AppendingFutureBarsDoesNotChangeTheCandidatesAtTheCutoff()
    {
        var bars = PullbackBars();
        var withFuture = bars
            .Add(Bar(TriggerMinute + 1, 99.70m, 120m, 99.80m, 119m, 999_999))
            .Add(Bar(TriggerMinute + 2, 90m, 99m, 95m, 91m, 999_999));

        var baseline = SetupDetector.Detect(Request(bars, PullbackZones(), PullbackEpisodes()), P);
        var future = SetupDetector.Detect(Request(withFuture, PullbackZones(), PullbackEpisodes()), P);

        Assert.Equal(baseline.Candidates.Select(x => x.Fingerprint()), future.Candidates.Select(x => x.Fingerprint()));
        Assert.Contains(SetupDetector.WarningFutureBars, future.Warnings);
    }

    // ── 식별자와 대표 선택 ──

    [Fact]
    public void EventIdAndGuardKeyAreStableAcrossRepeatedEvaluations()
    {
        var first = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes()), P);
        var second = SetupDetector.Detect(Request(PullbackBars(), PullbackZones(), PullbackEpisodes()), P);

        Assert.Equal(first.Candidates.Select(x => x.Fingerprint()), second.Candidates.Select(x => x.Fingerprint()));
        Assert.Equal(
            SetupDetector.EventId(Fx.Symbol, Fx.SessionStart, "PULLBACK", "support-zone", Fx.At(TriggerMinute)),
            first.Candidates.Single(x => x.Kind == SetupKind.Pullback).EventId);
        Assert.Equal(
            SetupDetector.DuplicateGuardKey(Fx.Symbol, Fx.SessionStart, "PULLBACK", Fx.At(TriggerMinute)),
            first.Candidates.Single(x => x.Kind == SetupKind.Pullback).DuplicateGuardKey);
    }

    [Fact]
    public void PreferredSelectionUsesQualityThenNetRThenZoneIdWithinAKind()
    {
        var high = Candidate("PULLBACK", "zone-a", 70, 2.0m);
        var low = Candidate("PULLBACK", "zone-b", 60, 9.0m);
        Assert.Equal(high.EventId, CandidateSelection.SelectPreferred([low, high])!.EventId);

        var tieQualityHighR = Candidate("PULLBACK", "zone-c", 70, 3.0m, "trigger-2");
        var tieQualityLowR = Candidate("PULLBACK", "zone-a", 70, 1.0m, "trigger-2");
        Assert.Equal(tieQualityHighR.EventId,
            CandidateSelection.SelectPreferred([tieQualityLowR, tieQualityHighR])!.EventId);
    }

    [Fact]
    public void PreferredSelectionAcrossKindsUsesQualityThenKindOrdinal()
    {
        var breakout = Candidate("BREAKOUT", "zone-b", 70, 1.5m, "trigger-b");
        var pullback = Candidate("PULLBACK", "zone-p", 70, 9.0m, "trigger-p");
        // 같은 품질이면 종류 문자열 ordinal: BREAKOUT < PULLBACK
        Assert.Equal(breakout.EventId, CandidateSelection.SelectPreferred([pullback, breakout])!.EventId);

        var betterPullback = Candidate("PULLBACK", "zone-p", 80, 1.1m, "trigger-p");
        Assert.Equal(betterPullback.EventId, CandidateSelection.SelectPreferred([betterPullback, breakout])!.EventId);
    }

    [Fact]
    public void OnlyOneCandidatePerDuplicateGuardKeySurvivesSelection()
    {
        // 같은 트리거가 여러 저항을 동시에 넘어도 이 키당 신규 거래 후보는 1개다(§8).
        var a = Candidate("BREAKOUT", "zone-a", 70, 2.0m);
        var b = Candidate("BREAKOUT", "zone-b", 65, 3.0m);
        var preferred = CandidateSelection.SelectPreferred([a, b]);

        Assert.Equal(a.DuplicateGuardKey, b.DuplicateGuardKey);
        Assert.Equal(a.EventId, preferred!.EventId);
    }

    [Fact]
    public void NonReadyCandidatesAreNeverPreferred()
    {
        var rejected = Candidate("PULLBACK", "zone-a", 95, 5m) with { Disposition = CandidateDisposition.Rejected };
        var invalidated = Candidate("PULLBACK", "zone-b", 90, 5m) with { Disposition = CandidateDisposition.Invalidated };

        Assert.Null(CandidateSelection.SelectPreferred([rejected, invalidated]));
        Assert.Equal(CandidateDisposition.Rejected, CandidateSelection.Summarize([rejected]));
        Assert.Equal(CandidateDisposition.Invalidated, CandidateSelection.Summarize([rejected, invalidated]));
    }

    static EntryCandidate Candidate(string kindName, string zoneId, double quality, decimal netR,
        string trigger = "trigger-1")
    {
        var kind = kindName switch
        {
            "BREAKOUT" => SetupKind.Breakout,
            "REBOUND" => SetupKind.Rebound,
            _ => SetupKind.Pullback
        };
        var planning = new PlanEvaluation(null, ImmutableArray<string>.Empty, null, null, null, null, null, null,
            null, netR, null, null, null, null, false, null, ImmutableArray<string>.Empty);
        var qualityResult = new EntryQualityResult(quality, ImmutableArray<QualityComponent>.Empty,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);
        return new EntryCandidate($"{kindName}|{zoneId}|{trigger}", $"{kindName}|{trigger}", kind, kindName, zoneId,
            Fx.At(TriggerMinute), Fx.At(TriggerMinute + 1), Fx.At(TriggerMinute), Fx.At(TriggerMinute + 1),
            Fx.At(TriggerMinute + 6), CandidateDisposition.Ready, 100m, 99.15m, quality, qualityResult, null,
            planning, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, false, false, null);
    }

    // ── D1 파이프라인과의 결합: 트리거 봉 격리와 예시 E ──

    /// <summary>§16B: structureCutoff=TriggerBarStart. 트리거 봉의 고가·거래량은 구조 근거가 되지 않는다.</summary>
    [Fact]
    public void TriggerBarNeverBecomesItsOwnStructureEvidenceInTheCandidatePath()
    {
        var bars = PullbackBars().ToBuilder();
        bars[TriggerMinute] = Bar(TriggerMinute, 99.58m, 120m, 99.60m, 99.80m, 999_999);
        var all = bars.ToImmutable();
        var structureCutoff = Fx.At(TriggerMinute);

        var build = ZoneBuilder.Build(ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            structureCutoff, all, BarAggregator.Aggregate(all, Fx.SessionStart, structureCutoff, P)), P);
        var evaluated = ZoneEvaluator.Evaluate(build.Zones,
            ZoneEvaluationRequest.Create(Fx.SessionStart, structureCutoff, all), P);

        Assert.All(evaluated.Zones, zone => Assert.True(zone.Upper < 101m, $"zone {zone.Lower}-{zone.Upper}"));
        Assert.Contains("FUTURE_BAR_INPUT_REJECTED", build.Warnings);

        // 같은 스냅샷으로 후보를 만들면 트리거 봉이 만든 저항은 목표가 될 수 없다.
        var result = SetupDetector.Detect(Request(all, evaluated.Zones, evaluated.Episodes), P);
        Assert.All(result.Candidates, candidate =>
            Assert.True(candidate.Planning.TargetZone is null || candidate.Planning.TargetZone.Upper < 101m));
        // 트리거 거래량은 상대 거래량(품질)에만 반영된다.
        Assert.Equal(999_999.0 / 1000, SessionIndicators.RelativeVolume(all, structureCutoff, 20)!.Value, 6);
    }

    /// <summary>
    /// §14 E: 10:00 봉 피벗은 10:01/10:02로 확인되어 10:03에 확정된다.
    /// 10:01 평가에는 그 피벗이 없으므로 목표 구조도 아직 없다.
    /// </summary>
    [Fact]
    public void ExampleEPivotConfirmationAtTenOhThreeGatesTheTargetStructure()
    {
        // 09:30 세션 시작 기준 10:00 = 30분, 확인은 10:03 = 33분이다.
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 40; m++)
            bars.Add(m == 30
                ? Bar(m, 99.90m, 100.50m, 100m, 100m)                 // 후보 피벗 고점
                : Bar(m, 99.90m, 100.10m, 100m, 100m));
        var all = bars.ToImmutable();
        var daily = ImmutableArray.Create(
            new StructureDailyBar(new DateOnly(2026, 9, 8), 99.50m, 100.50m, 99m, 99.50m, 10_000));

        Assert.Equal(Fx.At(33), Assert.Single(PivotDetector.Detect(Fx.Symbol, Fx.SessionStart,
            BarTimeframe.OneMinute, all, Fx.At(33), P).Where(x => x.Kind == PivotKind.High)).ConfirmedAt);
        Assert.Empty(PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.OneMinute, all, Fx.At(31), P)
            .Where(x => x.Kind == PivotKind.High));

        var before = Plan(all, daily, Fx.At(31));
        var after = Plan(all, daily, Fx.At(33));

        Assert.Contains(StructuralPlanner.NoTargetStructure, before.ReasonCodes);
        Assert.Null(before.Target);
        Assert.NotNull(after.Target);
        Assert.Equal(StructureMath.FloorToCent(after.TargetZone!.Lower - .01m), after.Target);
    }

    static PlanEvaluation Plan(ImmutableArray<StructureBar> bars, ImmutableArray<StructureDailyBar> daily,
        DateTimeOffset cutoff)
    {
        var build = ZoneBuilder.Build(ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, cutoff,
            bars, BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, P), daily), P);
        var evaluated = ZoneEvaluator.Evaluate(build.Zones,
            ZoneEvaluationRequest.Create(Fx.SessionStart, cutoff, bars), P);
        var support = D2.Support(99.20m, 99.40m);
        return StructuralPlanner.Evaluate(new PlanRequest(Fx.Symbol, "event-E", "PULLBACK", 100.00m, 99.15m,
            support, evaluated.Zones, build.Atr1mAtCutoff, null, cutoff, cutoff + P.CandidateTtl()), P);
    }
}
