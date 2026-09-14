using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureStopReentryCooldownTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static StructuralTradePlan PlanA()
    {
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), P);
        Assert.True(evaluation.Viable);
        return evaluation.Plan!;
    }

    static FrozenStructureContext Context(string eventId = "TEST|event-cooldown") =>
        StructuralSimulation.Freeze(PlanA(), eventId, "UP", 41.0, 55.5, Fx.At(40), Fx.At(40));

    static IReadOnlyList<DateTimeOffset> BarStarts(params int[] minutes) =>
        minutes.Select(Fx.At).ToArray();

    static IReadOnlyList<DateTimeOffset> BarStartsThrough(int lastMinute) =>
        BarStarts(Enumerable.Range(0, lastMinute + 1).ToArray());

    static StructuralEntryRequest Request(int triggerMinute, IReadOnlyList<DateTimeOffset>? bars,
        DateTimeOffset? sessionStart = null, string eventId = "TEST|event-cooldown") =>
        new(Fx.Symbol, Fx.At(triggerMinute), Fx.At(triggerMinute + 1), Fx.SessionEnd, Context(eventId), bars,
            sessionStart ?? Fx.SessionStart);

    static SimTrade Closed(string status, DateTimeOffset? exitAt, string symbol = Fx.Symbol, string id = "prior-1") =>
        new(id, symbol, "PULLBACK", Fx.At(20), 100.0, 101.0, 99.5, "저항", "구조", status, 99.5, exitAt, -0.5, 99.5,
            Logic: "v5-structure.1", SessionEnd: Fx.SessionEnd);

    [Fact]
    public void AStopExitBlocksTheSameSymbolUntilThreeCompletedBarsHaveClosed()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20));

        var twoBarsLater = StructuralSimulation.Enter([stopped], Request(32, BarStartsThrough(32)), P);
        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown, twoBarsLater.Outcome);
        Assert.Null(twoBarsLater.Trade);
        Assert.Single(twoBarsLater.Trades);

        var threeBarsLater = StructuralSimulation.Enter([stopped], Request(33, BarStartsThrough(33)), P);
        Assert.Equal(StructuralEntryOutcome.Entered, threeBarsLater.Outcome);
        Assert.Equal(2, threeBarsLater.Trades.Count);
    }

    [Fact]
    public void TheStopBarItselfCountsAsBarZero()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(59));

        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown,
            StructuralSimulation.Enter([stopped], Request(30, BarStartsThrough(30)), P).Outcome);
        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown,
            StructuralSimulation.Enter([stopped], Request(31, BarStartsThrough(31)), P).Outcome);
    }

    [Fact]
    public void OnlyCompletedBarsCountSoAGapKeepsTheCooldownActive()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20));
        var withGap = BarStarts(28, 29, 30, 33);

        var result = StructuralSimulation.Enter([stopped], Request(33, withGap), P);

        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown, result.Outcome);
    }

    [Fact]
    public void ReEvaluatingTheSameTradeListAfterARestartRepeatsTheSameOutcome()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20));
        var request = Request(32, BarStartsThrough(32));

        var first = StructuralSimulation.Enter([stopped], request, P);
        var second = StructuralSimulation.Enter(first.Trades, request, P);

        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown, first.Outcome);
        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown, second.Outcome);
        Assert.Single(second.Trades);
    }

    [Fact]
    public void AStopFromAPreviousSessionNeverBlocksTheNewSession()
    {
        var stopped = Closed("STOP", Fx.SessionStart.AddMinutes(-45));

        var result = StructuralSimulation.Enter([stopped], Request(1, BarStartsThrough(1)), P);

        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
    }

    [Theory]
    [InlineData("TARGET")]
    [InlineData("EOD")]
    [InlineData("CUT")]
    public void ExitsOtherThanStopDoNotStartACooldown(string status)
    {
        var closed = Closed(status, Fx.At(30).AddSeconds(20));

        var result = StructuralSimulation.Enter([closed], Request(31, BarStartsThrough(31)), P);

        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
    }

    [Fact]
    public void AStopOnAnotherSymbolDoesNotBlockThisSymbol()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20), symbol: "OTHER");

        var result = StructuralSimulation.Enter([stopped], Request(31, BarStartsThrough(31)), P);

        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
    }

    [Fact]
    public void TheLatestStopWinsWhenSeveralStopsExist()
    {
        var older = Closed("STOP", Fx.At(20).AddSeconds(10), id: "prior-old");
        var newer = Closed("STOP", Fx.At(30).AddSeconds(10), id: "prior-new");

        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown,
            StructuralSimulation.Enter([newer, older], Request(32, BarStartsThrough(32)), P).Outcome);
        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown,
            StructuralSimulation.Enter([older, newer], Request(32, BarStartsThrough(32)), P).Outcome);
    }

    [Fact]
    public void ALegacyStopWithoutAnExitTimestampIsNeverUsedAsEvidence()
    {
        var legacy = Closed("STOP", null);

        var result = StructuralSimulation.Enter([legacy], Request(31, BarStartsThrough(31)), P);

        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
    }

    [Fact]
    public void ARequestWithoutCompletedBarsIsNeverBlocked()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20));

        Assert.Equal(StructuralEntryOutcome.Entered,
            StructuralSimulation.Enter([stopped], Request(31, null), P).Outcome);
        Assert.Equal(StructuralEntryOutcome.Entered,
            StructuralSimulation.Enter([stopped], Request(31, []), P).Outcome);
    }

    [Fact]
    public void AnOpenTradeStillTakesPrecedenceOverTheCooldown()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20));
        var open = Closed("OPEN", null, id: "open-1") with { ExitPrice = null, PnlPercent = null };

        var result = StructuralSimulation.Enter([stopped, open], Request(31, BarStartsThrough(31)), P);

        Assert.Equal(StructuralEntryOutcome.BlockedByOpenTrade, result.Outcome);
    }

    [Fact]
    public void TheCooldownBarCountIsPartOfThePolicyHash()
    {
        Assert.Equal(3, P.StopReentryCooldownBars);
        Assert.NotEqual(P.PolicyHash, (P with { StopReentryCooldownBars = 5 }).PolicyHash);
    }

    [Fact]
    public void ZeroBarsDisablesTheCooldown()
    {
        var stopped = Closed("STOP", Fx.At(30).AddSeconds(20));

        var result = StructuralSimulation.Enter([stopped], Request(30, BarStartsThrough(30)),
            P with { StopReentryCooldownBars = 0 });

        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
    }
}

public sealed class StructureStopReentryCooldownWiringTests
{
    static readonly StructurePolicy P = D6.PolicyWithoutTheReboundTrendFloor;

    sealed record Harness(StructureAnalysisService Structure, RecordingStore Store, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, GateClock Clock, long Generation, MarketSession Session);

    static Harness Build(params SimTrade[] seed)
    {
        var market = D6.Session;
        var clock = new GateClock(Fx.At(60));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        store.Seed(seed);
        var observations = new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P), runtime,
            clock, new SilentDiagnostics(), new StructureEngineOptions(StructureEngineMode.Active), P,
            new StructuralTradeEntryService(store, P));
        var generation = runtime.CommitStart();
        runtime.TryCommit(generation, s => s with { Market = market });
        return new Harness(structure, store, observations, runtime, clock, generation, market);
    }

    static async Task ObserveAt(Harness harness, int minute)
    {
        harness.Clock.Reset(Fx.At(minute), TimeSpan.Zero);
        await harness.Structure.ObserveAsync(new StructureObservationRequest(Fx.Symbol, harness.Generation,
            harness.Session, D6.Bars(minute), D6.Daily(), D6.QuotePrice(Fx.At(minute)), Fx.At(minute)), default);
    }

    static StructureAnalysisView Published(Harness harness)
    {
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        return view;
    }

    static string GuardKey(StructureCandidateDto candidate) =>
        SetupDetector.DuplicateGuardKey(Fx.Symbol, Fx.SessionStart, candidate.Kind, candidate.TriggerBarStart);

    static StructuralLatch Latch(Harness harness) =>
        StructureLatchStorage.Parse(harness.Observations.Texts[StructureAnalysisService.LatchFile]).Single();

    static SimTrade Stopped(DateTimeOffset exitAt) =>
        new("stop-1", Fx.Symbol, "PULLBACK", Fx.At(50), 99.80, 100.40, 99.55, "저항", "구조", "STOP", 99.55, exitAt,
            -0.25, 99.55, Logic: "v5-structure.1", SessionEnd: Fx.SessionEnd);

    [Fact]
    public async Task AReboundOneBarAfterAStopStaysReadyAndRecordsTheCooldownBlock()
    {
        var harness = Build(Stopped(Fx.At(64).AddSeconds(13)));
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65);

        var view = Published(harness);
        var candidate = Assert.Single(view.Candidates, x => x.State == "READY");
        Assert.Contains(StructureAnalysisService.NoteEntryBlockedByStopCooldown, candidate.RejectionCodes);
        Assert.Contains(StructureAnalysisService.NoteEntryBlockedByStopCooldown, view.Notes);
        Assert.Single(harness.Store.Trades);
        Assert.DoesNotContain(GuardKey(candidate), Latch(harness).ConsumedGuardKeys);
    }

    [Fact]
    public async Task TheSameTriggerEntersOnceThreeCompletedBarsHavePassedSinceTheStop()
    {
        var harness = Build(Stopped(Fx.At(61).AddSeconds(13)));
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65);

        var view = Published(harness);
        Assert.Contains(view.Candidates, x => x.State == "ENTERED");
        Assert.Equal(2, harness.Store.Trades.Count);
    }

    [Fact]
    public async Task TheBarBeforeTheBoundaryIsStillBlocked()
    {
        var harness = Build(Stopped(Fx.At(62).AddSeconds(13)));
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65);

        Assert.Single(Published(harness).Candidates, x => x.State == "READY");
        Assert.Single(harness.Store.Trades);
    }
}
