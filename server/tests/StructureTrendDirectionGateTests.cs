using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #42 — 정렬 전제 셋업(PULLBACK/BREAKOUT)의 역방향 롱 진입 차단.
///
/// 감독자 설계안: `signedTrend &lt; 0`이면 READY로 승격하지 않고 <see cref="SetupDetector.CodeTrendDirectionOpposesLong"/>를
/// 남긴다. 0 기준 부호 검사일 뿐이며 새 파라미터·품질 임계값은 없다(§9.4가 BUY=70 같은 기준을 금지한다).
/// REBOUND는 §8/§9.4에 따라 면제이고, PULLBACK의 기존 TrendState 게이트(Up/Transition)는 그대로 유지된다.
///
/// 2026-09-10 세션의 실측 사례를 비식별 결정적 fixture로 재현한다 — 값은 signedTrend만 원본에서 가져왔고
/// 가격·구간은 D2 합성 fixture다. 실제 종목 추천이 아니다.
/// </summary>
public sealed class StructureTrendDirectionGateTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;
    const int TriggerMinute = 30;

    /// <summary>NBIS 96ba680b BREAKOUT의 동결 추세 값.</summary>
    const double NbisSignedTrend = -65.3416;

    /// <summary>NOW 7b2ec1ae PULLBACK의 동결 추세 값(TrendState=TRANSITION).</summary>
    const double NowSignedTrend = -31.0703;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    /// <summary>
    /// 눌림 경로: 100.00 부근 → support [99.20,99.40] 접촉(저점 99.15) → 회복 → 트리거.
    /// 눌림 저점이 지지 하단 아래이므로 같은 트리거에서 REBOUND 후보도 함께 생긴다 — 면제 검증에 쓴다.
    /// </summary>
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

    static ImmutableArray<StructureBar> BreakoutBars()
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < TriggerMinute; m++) bars.Add(Bar(m, 99.70m, 100.05m, 99.80m, 100.00m));
        bars.Add(Bar(TriggerMinute, 100.00m, 100.35m, 100.05m, 100.30m, 2000));
        return bars.ToImmutable();
    }

    static ImmutableArray<PriceZone> PullbackZones() =>
        [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)];

    static ImmutableArray<TouchEpisode> PullbackEpisodes() => [D2.Episode("support-zone", 25, 28)];

    static ImmutableArray<PriceZone> BreakoutZones() =>
    [
        D2.Resistance(99.90m, 100.10m, id: "breakout-zone"),
        D2.Resistance(101.80m, 102.10m, id: "target-zone")
    ];

    static SetupDetectionResult DetectPullback(TrendAssessment trend) =>
        SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            Fx.At(TriggerMinute + 1), Fx.At(TriggerMinute + 1), PullbackBars(), PullbackZones(), PullbackEpisodes(),
            trend, .20, 100.00m, Fx.At(TriggerMinute + 1),
            D2.Quote(99.99m, 100.01m, TriggerMinute + 1)), P);

    static SetupDetectionResult DetectBreakout(TrendAssessment trend) =>
        SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            Fx.At(TriggerMinute + 1), Fx.At(TriggerMinute + 1), BreakoutBars(), BreakoutZones(),
            ImmutableArray<TouchEpisode>.Empty, trend, .20, 100.30m, Fx.At(TriggerMinute + 1),
            D2.Quote(100.29m, 100.31m, TriggerMinute + 1)), P);

    // ── 실측 사례 재현 ───────────────────────────────────────────────────────

    /// <summary>
    /// NBIS 96ba680b 재현: BREAKOUT, signedTrend=-65.3416.
    /// 현재 설계에서는 alignmentQuality=0.173292가 0보다 커서 점수가 나오고(품질 46.32로 진입) 아무것도 막지 못했다.
    /// 새 규칙에서는 점수가 여전히 &gt;0이지만 유일한 거절 사유로 TREND_DIRECTION_OPPOSES_LONG이 남는다.
    /// </summary>
    [Fact]
    public void NbisBreakoutAgainstANegativeTrendIsRejectedByDirectionNotByScore()
    {
        var result = DetectBreakout(D2.Trend(TrendState.Down, NbisSignedTrend, structureDirection: -.5));
        var candidate = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Breakout));

        // 점수 문턱이 아니다: 품질은 계산되고 0보다 크며 계획도 성립한다.
        Assert.True(candidate.Quality.ReadyAllowed);
        Assert.True(candidate.EntryQuality > 0);
        Assert.True(candidate.Planning.Viable);
        Assert.Equal(0.173292, candidate.Quality.Components
            .Single(x => x.Name == EntryQualityEvaluator.AlignmentQuality).Value!.Value, 12);

        // 막는 것은 방향 부호다. 다른 게이트는 하나도 걸리지 않았다.
        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], candidate.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Null(candidate.Plan);
        Assert.Null(result.PreferredCandidateId);
        Assert.False(candidate.RetestConfirmed);
    }

    /// <summary>
    /// NOW 7b2ec1ae 재현: PULLBACK, TrendState=TRANSITION + signedTrend=-31.0703.
    /// 기존 TrendState 게이트(Up/Transition)는 그대로 통과해 후보 자체는 생기고, 새 부호 검사에서 걸린다.
    /// </summary>
    [Fact]
    public void NowPullbackInTransitionWithANegativeTrendPassesTheStateGateAndFailsTheSignGate()
    {
        var result = DetectPullback(D2.Trend(TrendState.Transition, NowSignedTrend, structureDirection: -.2));
        var candidate = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Pullback));

        // TrendState 게이트는 유지된다 — TRANSITION이라 후보 탐지 자체는 성립했다.
        Assert.Equal(SetupKind.Pullback, candidate.Kind);
        Assert.True(candidate.EntryQuality > 0);
        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], candidate.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Null(candidate.Plan);

        // 같은 트리거의 REBOUND는 면제이므로 여전히 READY다(§8 CounterTrend).
        var rebound = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Rebound));
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
        Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, rebound.RejectionCodes);
        Assert.Equal(rebound.EventId, result.PreferredCandidateId);
    }

    /// <summary>PULLBACK의 기존 TrendState 게이트는 부호 검사가 추가돼도 그대로다(RANGE/DOWN/UNKNOWN은 후보 없음).</summary>
    [Fact]
    public void ThePullbackTrendStateGateIsUnchanged()
    {
        foreach (var state in new[] { TrendState.Range, TrendState.Down, TrendState.Unknown })
        {
            var result = DetectPullback(D2.Trend(state, state == TrendState.Unknown ? null : -20));
            Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Pullback);
        }
    }

    // ── 경계 계약 ────────────────────────────────────────────────────────────

    /// <summary>signedTrend=0은 허용한다. 규칙은 `&lt;0` 거절이지 `&gt;0` 요구가 아니다 — 경계를 고정한다.</summary>
    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void ExactlyZeroSignedTrendIsAllowed(string kind)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Transition, 0, structureDirection: 0));

        Assert.Equal(0.5, candidate.Quality.Components
            .Single(x => x.Name == EntryQualityEvaluator.AlignmentQuality).Value!.Value, 12);
        Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, candidate.RejectionCodes);
        Assert.Empty(candidate.RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.NotNull(candidate.Plan);
    }

    /// <summary>아주 작은 음수도 거절한다 — 부호 검사에 완충 구간(새 임계값)을 두지 않았음을 고정한다.</summary>
    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void AnyNegativeSignedTrendIsRejectedWithoutATolerance(string kind)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Transition, -0.0001, structureDirection: 0));

        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], candidate.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
    }

    /// <summary>양수 추세는 종전대로 통과한다(회귀).</summary>
    [Theory]
    [InlineData("PULLBACK", 0.5)]
    [InlineData("PULLBACK", 65.3416)]
    [InlineData("BREAKOUT", 0.5)]
    [InlineData("BREAKOUT", 65.3416)]
    public void PositiveSignedTrendStillReachesReady(string kind, double signedTrend)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Up, signedTrend));

        Assert.Empty(candidate.RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.NotNull(candidate.Plan);
    }

    // ── REBOUND 면제 ─────────────────────────────────────────────────────────

    /// <summary>
    /// REBOUND는 음수 추세에서도 영향을 받지 않는다. §8이 높은 추세 점수를 요구하지 않고
    /// §9.4가 alignmentQuality를 제외하므로 여기에 방향 검사를 걸면 설계를 다시 깨는 것이다(#33 D7과 같은 원칙).
    /// </summary>
    [Theory]
    [InlineData(-0.0001)]
    [InlineData(-31.0703)]
    [InlineData(NbisSignedTrend)]
    [InlineData(-99.9)]
    public void ReboundIsExemptFromTheDirectionGate(double signedTrend)
    {
        var result = DetectPullback(D2.Trend(TrendState.Range, signedTrend, structureDirection: -.5));
        var rebound = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Rebound));

        Assert.True(rebound.CounterTrend);
        Assert.Empty(rebound.RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
        Assert.NotNull(rebound.Plan);
        Assert.DoesNotContain(EntryQualityEvaluator.AlignmentQuality, rebound.Quality.UsedComponents);
    }

    // ── null 추세: 중복 사유를 만들지 않는다 ─────────────────────────────────

    /// <summary>
    /// signedTrend가 null이면 기존 TREND_UNAVAILABLE 경로가 이미 READY를 막는다.
    /// 새 코드가 중복으로 붙지 않는다 — #27/#28이 두 사유를 섞어 집계하지 않도록 하는 계약이다.
    /// </summary>
    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void NullSignedTrendKeepsOnlyTrendUnavailable(string kind)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Transition, null, structureDirection: null,
            readyBlockers: TrendEvaluator.BlockerTrendUnavailable));

        // 결측 필수 구성요소(alignmentQuality) 사유는 기존 경로 그대로다. 여기에 새 코드가 더해지지 않는다.
        Assert.Equal([EntryQualityEvaluator.ReasonMissingRequiredComponent,
            EntryQualityEvaluator.ReasonTrendUnavailable], candidate.RejectionCodes.ToArray());
        Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, candidate.RejectionCodes);
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Null(candidate.EntryQuality);
    }

    // ── 관측 연결 (#27/#28) ──────────────────────────────────────────────────

    /// <summary>
    /// 거절 사유는 후보 fingerprint와 RejectionCodes에 남아 관측·동결 계획으로 흘러간다.
    /// #27/#28이 "이 규칙으로 걸린 후보"를 별도 코호트로 집계할 수 있어야 한다.
    /// </summary>
    [Fact]
    public void TheRejectionCodeIsCarriedOnTheCandidateForCohortAggregation()
    {
        var candidate = Only("BREAKOUT", D2.Trend(TrendState.Down, NbisSignedTrend, structureDirection: -.5));

        Assert.Contains(SetupDetector.CodeTrendDirectionOpposesLong, candidate.Fingerprint());
        Assert.Equal("TREND_DIRECTION_OPPOSES_LONG", SetupDetector.CodeTrendDirectionOpposesLong);
        // 사유 코드는 결정적이다 — 같은 입력을 다시 평가해도 fingerprint가 같다.
        Assert.Equal(candidate.Fingerprint(),
            Only("BREAKOUT", D2.Trend(TrendState.Down, NbisSignedTrend, structureDirection: -.5)).Fingerprint());
    }

    /// <summary>
    /// 거절된 후보는 계획을 만들지 않고 대표로도 선택되지 않는다.
    /// 진입은 READY 대표 후보의 동결 계획에서만 나오므로(§10/§18) 어떤 모드에서도 이 후보로는 진입할 수 없다.
    /// </summary>
    [Fact]
    public void ARejectedDirectionCandidateNeverProducesAFrozenPlan()
    {
        var result = DetectBreakout(D2.Trend(TrendState.Down, NbisSignedTrend, structureDirection: -.5));

        Assert.All(result.Candidates.Where(x => x.Kind == SetupKind.Breakout), x =>
        {
            Assert.Null(x.Plan);
            Assert.NotEqual(CandidateDisposition.Ready, x.Disposition);
        });
        Assert.Null(result.PreferredCandidateId);
        Assert.Null(CandidateSelection.SelectPreferred(result.Candidates));
    }

    // ── off/shadow 무영향 ────────────────────────────────────────────────────

    /// <summary>
    /// D6 배선 fixture(m64 REBOUND 트리거)는 추세가 음수인 국면이다. shadow는 이전과 동일하게 관측만 하고
    /// REBOUND는 면제이므로 READY 그대로이며 새 사유 코드는 어디에도 붙지 않는다. 거래도 만들지 않는다.
    /// </summary>
    [Fact]
    public async Task ShadowModeIsUnaffectedByTheDirectionGate()
    {
        var harness = Build(StructureEngineMode.Shadow);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.NotNull(view.Trend);
        Assert.True(view.Trend!.SignedTrend < 0, $"fixture 추세가 음수가 아니다: {view.Trend.SignedTrend}");
        Assert.Contains(view.Candidates, x => x.Kind == "REBOUND" && x.State == "READY");
        Assert.All(view.Candidates, x =>
            Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, x.RejectionCodes));
        Assert.Empty(harness.Store.Trades);
        Assert.Equal("v4", view.EntryOwner);
    }

    /// <summary>off는 계산 자체를 하지 않으므로 새 사유 코드가 생길 자리도 없다.</summary>
    [Fact]
    public async Task OffModeIsUnaffectedByTheDirectionGate()
    {
        var harness = Build(StructureEngineMode.Off);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.False(harness.Structure.TryGetPublished(Fx.Symbol, out _));
        Assert.Equal(0, harness.Observations.Interactions);
        Assert.Empty(harness.Store.Trades);
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────

    static EntryCandidate Only(string kind, TrendAssessment trend) => kind switch
    {
        "BREAKOUT" => Assert.Single(DetectBreakout(trend).Candidates.Where(x => x.Kind == SetupKind.Breakout)),
        _ => Assert.Single(DetectPullback(trend).Candidates.Where(x => x.Kind == SetupKind.Pullback))
    };

    sealed record Harness(MonitorPollingService Poller, RecordingStore Store, MemoryObservationStore Observations,
        StructureAnalysisService Structure, MovableClock Clock);

    static Harness Build(StructureEngineMode mode)
    {
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var observations = new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P), runtime,
            clock, diagnostics, new StructureEngineOptions(mode), P, new StructuralTradeEntryService(store));
        var gateway = new ScriptedGateway(D6.Session, () => D6.CompletedBars(clock.Now),
            () => (D6.QuotePrice(clock.Now), clock.Now), () => D6.Daily());
        var poller = new MonitorPollingService(store, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure);
        runtime.CommitStart();
        return new Harness(poller, store, observations, structure, clock);
    }

    static async Task PollAt(Harness harness, int minute)
    {
        harness.Clock.Now = Fx.At(minute);
        await harness.Poller.PollAsync(default);
    }
}
