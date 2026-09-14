using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureEntryRetryTests
{
    static readonly StructurePolicy P = D6.PolicyWithoutTheReboundTrendFloor;

    sealed record Harness(StructureAnalysisService Structure, RecordingStore Store, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, GateClock Clock, long Generation, MarketSession Session);

    static Harness Build(StructureEngineMode mode)
    {
        var market = D6.Session;
        var clock = new GateClock(Fx.At(60));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var observations = new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P), runtime,
            clock, new SilentDiagnostics(), new StructureEngineOptions(mode), P,
            new StructuralTradeEntryService(store));
        var generation = runtime.CommitStart();
        runtime.TryCommit(generation, s => s with { Market = market });
        return new Harness(structure, store, observations, runtime, clock, generation, market);
    }

    static async Task ObserveAt(Harness harness, int minute, TimeSpan? gateDelay = null)
    {
        harness.Clock.Reset(Fx.At(minute), gateDelay ?? TimeSpan.Zero);
        await harness.Structure.ObserveAsync(new StructureObservationRequest(Fx.Symbol, harness.Generation,
            harness.Session, D6.Bars(minute), D6.Daily(), D6.QuotePrice(Fx.At(minute)), Fx.At(minute)), default);
    }

    static StructureAnalysisView Published(Harness harness)
    {
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        return view;
    }

    static StructuralLatch Latch(Harness harness) =>
        StructureLatchStorage.Parse(harness.Observations.Texts[StructureAnalysisService.LatchFile]).Single();

    static string GuardKey(StructureCandidateDto candidate) =>
        SetupDetector.DuplicateGuardKey(Fx.Symbol, Fx.SessionStart, candidate.Kind, candidate.TriggerBarStart);

    static TimeSpan StaleGate => TimeSpan.FromSeconds(P.NewEntryQuoteMaxAgeSeconds + 5);

    [Fact]
    public async Task AGateBlockedEntryLeavesTheTriggerGuardKeyUnconsumed()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, StaleGate);

        var candidate = Assert.Single(Published(harness).Candidates, x => x.State == "READY");
        Assert.Empty(harness.Store.Trades);
        Assert.DoesNotContain(GuardKey(candidate), Latch(harness).ConsumedGuardKeys);
    }

    [Fact]
    public async Task AGateBlockedEntryKeepsTheCandidateReadyAndRecordsTheBlockAsARejectionCode()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, StaleGate);

        var expected = $"{StructureAnalysisService.NoteEntryGateRecheck}:{SetupDetector.BlockerStaleQuote}";
        var view = Published(harness);
        var candidate = Assert.Single(view.Candidates, x => x.State == "READY");
        Assert.Contains(expected, candidate.RejectionCodes);
        Assert.Contains(expected, view.Notes);
        Assert.NotNull(candidate.Plan);
    }

    [Fact]
    public async Task ABlockedEntryStillEntersOnTheNextTriggerOfTheSameEpisode()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, StaleGate);
        Assert.Empty(harness.Store.Trades);

        await ObserveAt(harness, 66);

        var view = Published(harness);
        Assert.Single(harness.Store.Trades);
        Assert.Contains(view.Candidates, x => x.State == "ENTERED");
        Assert.All(view.Candidates, x =>
        {
            Assert.DoesNotContain(StructuralLifecycle.CodeDuplicateGuard, x.RejectionCodes);
            Assert.DoesNotContain(StructuralLifecycle.CodeEpisodeConsumed, x.RejectionCodes);
        });
    }

    [Fact]
    public async Task AnEnteredCandidateStillConsumesItsTriggerGuardKeyAndNeverEntersTwice()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65);

        var entered = Assert.Single(Published(harness).Candidates, x => x.State == "ENTERED");
        Assert.Single(harness.Store.Trades);
        Assert.Contains(GuardKey(entered), Latch(harness).ConsumedGuardKeys);

        await ObserveAt(harness, 66);
        Assert.Single(harness.Store.Trades);
    }

    [Fact]
    public async Task ShadowModeConsumesTheTriggerGuardKeyWhenReadyIsEstablished()
    {
        var harness = Build(StructureEngineMode.Shadow);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, StaleGate);

        var candidate = Assert.Single(Published(harness).Candidates, x => x.State == "READY");
        Assert.Empty(harness.Store.Trades);
        Assert.Contains(GuardKey(candidate), Latch(harness).ConsumedGuardKeys);
    }
}
