using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureSessionCleanupTests
{
    static readonly StructurePolicy P = D2.WideNetR;
    static readonly StructureAlertDraft Draft =
        new(StructureAlertPublisher.TypeReady, "TEST|event-A", "PULLBACK", 50.0, 1.2m);

    static DateTimeOffset NextSession => Fx.SessionStart.AddDays(1);

    static Task Publish(StructureAlertPublisher alerts, DateTimeOffset session) =>
        alerts.PublishAsync(Fx.Symbol, session, P.PolicyHash, [Draft], 100m, session.AddMinutes(60), default);

    [Fact]
    public async Task ANewSessionNeverExposesThePreviousSessionsEvents()
    {
        var alerts = new StructureAlertPublisher(new MemoryObservationStore());
        await Publish(alerts, Fx.SessionStart);

        Assert.Single(await alerts.GetRecentAsync(Fx.SessionStart, default));
        Assert.Empty(await alerts.GetRecentAsync(NextSession, default));
    }

    [Fact]
    public async Task ReadingUnderANewSessionDropsTheOldEventsFromMemoryWithoutRewritingTheFile()
    {
        var store = new MemoryObservationStore();
        var alerts = new StructureAlertPublisher(store);
        await Publish(alerts, Fx.SessionStart);
        var persisted = store.Texts[StructureAlertPublisher.AlertsFile];
        var writes = store.TextWrites;

        Assert.Empty(await alerts.GetRecentAsync(NextSession, default));

        Assert.Empty(await alerts.GetRecentAsync(Fx.SessionStart, default));
        Assert.Equal(persisted, store.Texts[StructureAlertPublisher.AlertsFile]);
        Assert.Equal(writes, store.TextWrites);
    }

    [Fact]
    public async Task ARestartInsideTheSameSessionStillSuppressesTheDuplicate()
    {
        var store = new MemoryObservationStore();
        var first = new StructureAlertPublisher(store);
        await Publish(first, Fx.SessionStart);

        var restarted = new StructureAlertPublisher(store);
        var restored = await restarted.GetRecentAsync(Fx.SessionStart, default);
        Assert.Equal(1, Assert.Single(restored).Seq);

        await Publish(restarted, Fx.SessionStart);
        var again = Assert.Single(await restarted.GetRecentAsync(Fx.SessionStart, default));
        Assert.Equal(1, again.Seq);
        Assert.Equal(1, store.TextWrites);
    }

    [Fact]
    public async Task ClearEmptiesMemoryButKeepsThePersistedDedupKeysAndSeq()
    {
        var store = new MemoryObservationStore();
        var alerts = new StructureAlertPublisher(store);
        await Publish(alerts, Fx.SessionStart);

        alerts.Clear();

        await Publish(alerts, Fx.SessionStart);
        var restored = Assert.Single(await alerts.GetRecentAsync(Fx.SessionStart, default));
        Assert.Equal(1, restored.Seq);
        Assert.Equal(1, store.TextWrites);

        await alerts.PublishAsync(Fx.Symbol, Fx.SessionStart, P.PolicyHash,
            [Draft with { EventId = "TEST|event-B" }], 100m, Fx.At(61), default);
        var events = await alerts.GetRecentAsync(Fx.SessionStart, default);
        Assert.Equal([1L, 2L], events.Select(x => x.Seq));
    }

    [Fact]
    public async Task StateStopsServingTheAlertsOnceTheMarketSessionAdvances()
    {
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var alerts = new StructureAlertPublisher(observations);
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P), runtime,
            clock, new SilentDiagnostics(), new StructureEngineOptions(StructureEngineMode.Active), P, null, alerts);
        var generation = D3.StartedRuntime(runtime);
        await Publish(alerts, Fx.SessionStart);
        var state = new StateQueryService(store, new QuietSignals(), new QuietStream(), runtime, clock, structure, alerts);

        Assert.Equal(1, await EventCount(state));

        runtime.TryCommit(generation, s => s with
        {
            Market = new MarketSession(true, "정규장", null, NextSession, NextSession.AddHours(6.5))
        });
        clock.Now = NextSession.AddMinutes(60);

        Assert.Equal(0, await EventCount(state));
    }

    [Fact]
    public async Task StateServesNoAlertsWhileTheSessionIsUnknown()
    {
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var alerts = new StructureAlertPublisher(observations);
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P), runtime,
            clock, new SilentDiagnostics(), new StructureEngineOptions(StructureEngineMode.Active), P, null, alerts);
        runtime.CommitStart();
        await Publish(alerts, Fx.SessionStart);
        var state = new StateQueryService(store, new QuietSignals(), new QuietStream(), runtime, clock, structure, alerts);

        Assert.Equal(0, await EventCount(state));
    }

    [Fact]
    public async Task RemovingASymbolDropsItsCachedDailyCandles()
    {
        var harness = Poller();
        await PollAt(harness, 64);
        Assert.Equal(1, harness.Gateway.DailyCalls);

        await PollAt(harness, 65);
        Assert.Equal(1, harness.Gateway.DailyCalls);

        harness.Poller.Remove(Fx.Symbol);
        await PollAt(harness, 66);
        Assert.Equal(2, harness.Gateway.DailyCalls);
    }

    [Fact]
    public async Task TheSessionEndClearsTheCachedDailyCandles()
    {
        var harness = Poller();
        await PollAt(harness, 64);
        await PollAt(harness, 65);
        Assert.Equal(1, harness.Gateway.DailyCalls);

        harness.Clock.Now = Fx.SessionEnd.AddMinutes(5);
        await harness.Poller.PollAsync(default);

        await PollAt(harness, 66);
        Assert.Equal(2, harness.Gateway.DailyCalls);
    }

    [Fact]
    public async Task ClearDropsTheCachedDailyCandles()
    {
        var harness = Poller();
        await PollAt(harness, 64);
        harness.Poller.Clear();

        await PollAt(harness, 65);
        Assert.Equal(2, harness.Gateway.DailyCalls);
    }

    [Fact]
    public async Task TheWatchlistReadsPerPollDoNotGrowWithTheNumberOfSymbols()
    {
        var single = Poller(Fx.Symbol);
        await PollAt(single, 64);

        var many = Poller(Fx.Symbol, "OTHER", "THIRD");
        await PollAt(many, 64);

        Assert.Equal(3, many.Books.Calls);
        Assert.Equal(2, single.Store.Reads.Count(x => x == "watchlist.json"));
        Assert.Equal(single.Store.Reads.Count(x => x == "watchlist.json"),
            many.Store.Reads.Count(x => x == "watchlist.json"));
    }

    [Fact]
    public async Task RepeatedLiquidityQueriesInsideTheCacheWindowReadTheWatchlistFileOnce()
    {
        var (store, service, _, _) = Liquidity(Fx.Symbol);

        for (var i = 0; i < 30; i++)
        {
            var (status, _) = await service.GetAsync(Fx.Symbol, default);
            Assert.Equal(200, status);
        }

        Assert.Equal(1, store.Reads.Count(x => x == "watchlist.json"));
    }

    [Fact]
    public async Task ASymbolMissingFromTheCachedWatchlistIsAlwaysRecheckedAgainstTheFile()
    {
        var (store, service, _, _) = Liquidity(Fx.Symbol);
        await service.GetAsync(Fx.Symbol, default);

        var (missing, _) = await service.GetAsync("OTHER", default);
        Assert.Equal(404, missing);

        store.Watch.Add(new WatchItem("OTHER", "테스트"));
        var (added, response) = await service.GetAsync("OTHER", default);
        Assert.Equal(200, added);
        Assert.Equal(StructureLiquidityFeed.StatusReady, response!.Status);
    }

    [Fact]
    public async Task ARemovedSymbolFallsBackToNotFoundOnceTheCacheWindowPasses()
    {
        var (store, service, clock, _) = Liquidity(Fx.Symbol);
        Assert.Equal(200, (await service.GetAsync(Fx.Symbol, default)).HttpStatus);

        store.Watch.Clear();
        clock.Now = clock.Now.Add(LiquidityQueryService.WatchlistCacheTtl);

        Assert.Equal(404, (await service.GetAsync(Fx.Symbol, default)).HttpStatus);
    }

    [Fact]
    public async Task TheWatchlistCacheNeverInventsAnOrderBookWhenTheFeedIsUnavailable()
    {
        var (_, service, _, books) = Liquidity(Fx.Symbol);
        books.Failure = new HttpRequestException("Toss 5xx");
        var feed = new StructureLiquidityFeed(service, new SilentDiagnostics());

        Assert.Null(await feed.TryGetAsync(Fx.Symbol, default));
        Assert.Null(await feed.TryGetAsync(Fx.Symbol, default));
    }

    static async Task<int> EventCount(StateQueryService state)
    {
        var json = JsonSerializer.Serialize(await state.GetAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("structureEvents").GetArrayLength();
    }

    static (RecordingStore Store, LiquidityQueryService Service, MovableClock Clock, CountingOrderBookGateway Books)
        Liquidity(params string[] symbols)
    {
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        foreach (var symbol in symbols) store.Watch.Add(new WatchItem(symbol, "테스트"));
        var runtime = new MonitorRuntimeState();
        D3.StartedRuntime(runtime);
        var books = new CountingOrderBookGateway(clock);
        return (store, new LiquidityQueryService(store, books, runtime, clock), clock, books);
    }

    sealed record PollHarness(MonitorPollingService Poller, RecordingStore Store, ScriptedGateway Gateway,
        CountingOrderBookGateway Books, MovableClock Clock);

    static PollHarness Poller(params string[] symbols)
    {
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        foreach (var symbol in symbols.Length == 0 ? [Fx.Symbol] : symbols)
            store.Watch.Add(new WatchItem(symbol, "테스트"));
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var alerts = new StructureAlertPublisher(new MemoryObservationStore());
        var structure = new StructureAnalysisService(store,
            new StructureObservationWriter(new MemoryObservationStore(), P), runtime, clock, diagnostics,
            new StructureEngineOptions(StructureEngineMode.Active), P, new StructuralTradeEntryService(store), alerts);
        var books = new CountingOrderBookGateway(clock);
        var liquidity = new LiquidityQueryService(store, books, runtime, clock);
        var gateway = new ScriptedGateway(D6.Session, () => D6.CompletedBars(clock.Now),
            () => (D6.QuotePrice(clock.Now), clock.Now), () => D6.Daily());
        var poller = new MonitorPollingService(store, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure, new StructureLiquidityFeed(liquidity, diagnostics), alerts);
        runtime.CommitStart();
        return new PollHarness(poller, store, gateway, books, clock);
    }

    static async Task PollAt(PollHarness harness, int minute, int seconds = 0)
    {
        harness.Clock.Now = Fx.At(minute).AddSeconds(seconds);
        await harness.Poller.PollAsync(default);
    }
}

sealed class QuietSignals : IMonitorSignals
{
    public bool TryGet(string symbol, out SignalView signal) { signal = null!; return false; }
    public void Remove(string symbol) { }
    public void Clear() { }
}
