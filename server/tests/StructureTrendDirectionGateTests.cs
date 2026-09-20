using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureTrendDirectionGateTests
{
    static readonly StructurePolicy P = D2.WideNetR with
    {
        AllowTransitionPullback = true,
        AllowTransitionBreakout = true
    };
    const int TriggerMinute = 30;

    const double NbisFrozenBreakoutSignedTrend = -65.3416;
    const double NowFrozenPullbackSignedTrend = -31.0703;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    static ImmutableArray<StructureBar> PullbackBarsWhoseEpisodeLowAlsoFormsARebound()
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

    static ImmutableArray<StructureBar> BearishTriggerBars()
    {
        var bars = PullbackBarsWhoseEpisodeLowAlsoFormsARebound().ToBuilder();
        bars[^1] = Bar(TriggerMinute, 99.58m, 99.85m, 99.85m, 99.80m, 2000);
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
            Fx.At(TriggerMinute + 1), Fx.At(TriggerMinute + 1), PullbackBarsWhoseEpisodeLowAlsoFormsARebound(),
            PullbackZones(), PullbackEpisodes(), trend, .20, 100.00m, Fx.At(TriggerMinute + 1),
            D2.Quote(99.99m, 100.01m, TriggerMinute + 1)), P);

    static SetupDetectionResult DetectBreakout(TrendAssessment trend) =>
        SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            Fx.At(TriggerMinute + 1), Fx.At(TriggerMinute + 1), BreakoutBars(), BreakoutZones(),
            ImmutableArray<TouchEpisode>.Empty, trend, .20, 100.30m, Fx.At(TriggerMinute + 1),
            D2.Quote(100.29m, 100.31m, TriggerMinute + 1)), P);

    [Fact]
    public void NbisFrozenBreakoutIsRejectedForDirectionAlthoughItsQualityAndPlanAreStillValid()
    {
        var result = DetectBreakout(D2.Trend(TrendState.Down, NbisFrozenBreakoutSignedTrend, structureDirection: -.5));
        var candidate = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Breakout));

        Assert.True(candidate.Quality.ReadyAllowed);
        Assert.True(candidate.EntryQuality > 0);
        Assert.True(candidate.Planning.Viable);
        Assert.Equal(0.173292, candidate.Quality.Components
            .Single(x => x.Name == EntryQualityEvaluator.AlignmentQuality).Value!.Value, 12);

        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], candidate.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Null(candidate.Plan);
        Assert.Null(result.PreferredCandidateId);
        Assert.False(candidate.RetestConfirmed);
    }

    [Fact]
    public void NowFrozenPullbackInTransitionPassesTheTrendStateGateAndIsRejectedBySignAlone()
    {
        var result = DetectPullback(D2.Trend(TrendState.Transition, NowFrozenPullbackSignedTrend,
            structureDirection: -.2));
        var candidate = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Pullback));

        Assert.Equal(SetupKind.Pullback, candidate.Kind);
        Assert.True(candidate.EntryQuality > 0);
        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], candidate.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Null(candidate.Plan);

        Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Rebound);
        Assert.Null(result.PreferredCandidateId);
    }

    [Fact]
    public void RangeDownAndUnknownStillProduceNoPullbackCandidateAtAll()
    {
        foreach (var state in new[] { TrendState.Range, TrendState.Down, TrendState.Unknown })
        {
            var result = DetectPullback(D2.Trend(state, state == TrendState.Unknown ? null : -20));
            Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Pullback);
        }
    }

    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void ExactlyZeroSignedTrendStillReachesReady(string kind)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Transition, 0, structureDirection: 0));

        Assert.Equal(0.5, candidate.Quality.Components
            .Single(x => x.Name == EntryQualityEvaluator.AlignmentQuality).Value!.Value, 12);
        Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, candidate.RejectionCodes);
        Assert.Empty(candidate.RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.NotNull(candidate.Plan);
    }

    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void TheSmallestNegativeSignedTrendIsRejectedSoNoToleranceBandExists(string kind)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Transition, -0.0001, structureDirection: 0));

        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], candidate.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(-24)]
    [InlineData(-25)]
    public void ReboundStaysReadyDownToTheTrendFloorInclusive(double signedTrend)
    {
        var result = DetectPullback(D2.Trend(TrendState.Range, signedTrend, structureDirection: -.5));
        var rebound = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Rebound));

        Assert.True(rebound.CounterTrend);
        Assert.Empty(rebound.RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
        Assert.NotNull(rebound.Plan);
        Assert.DoesNotContain(EntryQualityEvaluator.AlignmentQuality, rebound.Quality.UsedComponents);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-31.0703)]
    [InlineData(NbisFrozenBreakoutSignedTrend)]
    [InlineData(-99.9)]
    public void ReboundBelowTheTrendFloorIsRejectedButStaysObservable(double signedTrend)
    {
        var result = DetectPullback(D2.Trend(TrendState.Range, signedTrend, structureDirection: -.5));
        var rebound = Assert.Single(result.Candidates.Where(x => x.Kind == SetupKind.Rebound));

        Assert.True(rebound.CounterTrend);
        Assert.Equal([SetupDetector.CodeTrendDeeplyOpposesRebound], rebound.RejectionCodes.ToArray());
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Null(rebound.Plan);
        Assert.Null(result.PreferredCandidateId);
        Assert.Equal("TREND_DEEPLY_OPPOSES_REBOUND", SetupDetector.CodeTrendDeeplyOpposesRebound);
    }

    [Fact]
    public void TheReboundFloorNeverTouchesTheOtherKinds()
    {
        var breakout = Only("BREAKOUT", D2.Trend(TrendState.Up, 40));
        Assert.DoesNotContain(SetupDetector.CodeTrendDeeplyOpposesRebound, breakout.RejectionCodes);

        var pullback = Only("PULLBACK", D2.Trend(TrendState.Transition, -30, structureDirection: -.5));
        Assert.Equal([SetupDetector.CodeTrendDirectionOpposesLong], pullback.RejectionCodes.ToArray());
    }

    [Fact]
    public void ABearishTriggerBarProducesNoReboundCandidate()
    {
        var result = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            Fx.At(TriggerMinute + 1), Fx.At(TriggerMinute + 1), BearishTriggerBars(), PullbackZones(),
            PullbackEpisodes(), D2.Trend(TrendState.Range, -10, structureDirection: -.5), .20, 99.80m,
            Fx.At(TriggerMinute + 1), D2.Quote(99.79m, 99.81m, TriggerMinute + 1)), P);

        Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Rebound);
    }

    [Fact]
    public void APullbackOnTheSameBarZoneAndAnchorSuppressesTheRebound()
    {
        var withPullback = DetectPullback(D2.Trend(TrendState.Transition, -0.0001, structureDirection: 0));
        Assert.Single(withPullback.Candidates.Where(x => x.Kind == SetupKind.Pullback));
        Assert.DoesNotContain(withPullback.Candidates, x => x.Kind == SetupKind.Rebound);

        var withoutPullback = DetectPullback(D2.Trend(TrendState.Range, -0.0001, structureDirection: 0));
        Assert.DoesNotContain(withoutPullback.Candidates, x => x.Kind == SetupKind.Pullback);
        var rebound = Assert.Single(withoutPullback.Candidates.Where(x => x.Kind == SetupKind.Rebound));
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
    }

    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void NullSignedTrendKeepsTheExistingReasonsAndAddsNoDirectionCode(string kind)
    {
        var candidate = Only(kind, D2.Trend(TrendState.Transition, null, structureDirection: null,
            readyBlockers: TrendEvaluator.BlockerTrendUnavailable));

        Assert.Equal([EntryQualityEvaluator.ReasonMissingRequiredComponent,
            EntryQualityEvaluator.ReasonTrendUnavailable], candidate.RejectionCodes.ToArray());
        Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, candidate.RejectionCodes);
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Null(candidate.EntryQuality);
    }

    /// <summary>방향 게이트는 signedTrend 부호만 본다. 히스테리시스가 바꾸는 state label은 무관하다(§7, #148).</summary>
    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void TheDirectionGateReadsTheSignedTrendSignAndIgnoresTheStateLabel(string kind)
    {
        foreach (var state in States(kind))
        {
            Assert.Contains(SetupDetector.CodeTrendDirectionOpposesLong,
                Only(kind, D2.Trend(state, -30, structureDirection: -.5)).RejectionCodes);
            Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong,
                Only(kind, D2.Trend(state, 30, structureDirection: .5)).RejectionCodes);
        }
    }

    /// <summary>TREND_UNAVAILABLE은 추세 계산 가능 여부만 본다. state label과는 독립이다(§16B, #148).</summary>
    [Theory]
    [InlineData("PULLBACK")]
    [InlineData("BREAKOUT")]
    public void TheTrendUnavailableBlockerIsIndependentOfTheStateLabel(string kind)
    {
        foreach (var state in States(kind))
            Assert.Contains(EntryQualityEvaluator.ReasonTrendUnavailable,
                Only(kind, D2.Trend(state, null, structureDirection: null,
                    readyBlockers: TrendEvaluator.BlockerTrendUnavailable)).RejectionCodes);

        foreach (var state in States(kind))
            Assert.DoesNotContain(EntryQualityEvaluator.ReasonTrendUnavailable,
                Only(kind, D2.Trend(state, 30, structureDirection: .5)).RejectionCodes);
    }

    /// <summary>PULLBACK은 state 게이트를 따로 갖고 있으므로 그 게이트를 통과하는 label만 비교 대상이다(§8).</summary>
    static TrendState[] States(string kind) => kind == "PULLBACK"
        ? [TrendState.Up, TrendState.Transition]
        : [TrendState.Up, TrendState.Down, TrendState.Range, TrendState.Transition, TrendState.Unknown];

    [Fact]
    public void TheDirectionRejectionCodeIsDeterministicAndRidesTheCandidateFingerprint()
    {
        var candidate = Only("BREAKOUT",
            D2.Trend(TrendState.Down, NbisFrozenBreakoutSignedTrend, structureDirection: -.5));

        Assert.Contains(SetupDetector.CodeTrendDirectionOpposesLong, candidate.Fingerprint());
        Assert.Equal("TREND_DIRECTION_OPPOSES_LONG", SetupDetector.CodeTrendDirectionOpposesLong);
        Assert.Equal(candidate.Fingerprint(), Only("BREAKOUT",
            D2.Trend(TrendState.Down, NbisFrozenBreakoutSignedTrend, structureDirection: -.5)).Fingerprint());
    }

    [Fact]
    public void ADirectionRejectedCandidateProducesNoPlanAndIsNeverPreferred()
    {
        var result = DetectBreakout(D2.Trend(TrendState.Down, NbisFrozenBreakoutSignedTrend, structureDirection: -.5));

        Assert.All(result.Candidates.Where(x => x.Kind == SetupKind.Breakout), x =>
        {
            Assert.Null(x.Plan);
            Assert.NotEqual(CandidateDisposition.Ready, x.Disposition);
        });
        Assert.Null(result.PreferredCandidateId);
        Assert.Null(CandidateSelection.SelectPreferred(result.Candidates));
    }

    [Fact]
    public async Task ShadowModeRejectsItsDeeplyNegativeTrendReboundWithTheReboundFloorCode()
    {
        var harness = Build(StructureEngineMode.Shadow);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.NotNull(view.Trend);
        Assert.True(view.Trend!.SignedTrend < -P.TrendStateThreshold,
            $"fixture 추세가 하한 아래가 아니다: {view.Trend.SignedTrend}");
        var rebound = view.Candidates.Single(x => x.Kind == "REBOUND");
        Assert.Equal("REJECTED", rebound.State);
        Assert.Contains(SetupDetector.CodeTrendDeeplyOpposesRebound, rebound.RejectionCodes);
        Assert.All(view.Candidates, x =>
            Assert.DoesNotContain(SetupDetector.CodeTrendDirectionOpposesLong, x.RejectionCodes));
        Assert.Empty(harness.Store.Trades);
        Assert.Equal("v4", view.EntryOwner);
    }

    [Fact]
    public async Task OffModeComputesNothingAndStoresNothing()
    {
        var harness = Build(StructureEngineMode.Off);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.False(harness.Structure.TryGetPublished(Fx.Symbol, out _));
        Assert.Equal(0, harness.Observations.Interactions);
        Assert.Empty(harness.Store.Trades);
    }

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
