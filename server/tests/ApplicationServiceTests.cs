using Astra.Server;
using Astra.Server.Application;
using Xunit;

public sealed class ApplicationServiceTests
{
    [Fact]
    public async Task RuntimeRejectsCommitFromInvalidatedPoll()
    {
        var state = new MonitorRuntimeState();
        using (await state.EnterControlAsync()) { var generation = state.CommitStart(); state.CommitWatchlistChange(); Assert.False(state.TryCommit(generation, _ => throw new Exception("must not run"))); }
    }

    [Fact]
    public async Task SimulationReportSeparatesLegacyFromCurrentLogic()
    {
        var store = new MemoryStore(); var now = DateTimeOffset.UtcNow;
        await store.Write("simtrades.json", new List<SimTrade> { Trade("a", now, null), Trade("b", now.AddMinutes(1), "v3") });
        var report = await new SimulationReportQueryService(store, TimeProvider.System).GetAsync();
        Assert.Equal(new[] { "v3", "legacy" }, report.ByVersion.Select(x => x.Version));
        Assert.All(report.ByVersion, x => Assert.Equal(1, x.Stats.Closed));
        Assert.Null(report.Analysis);
        Assert.All(report.ByVersion, x => Assert.Single(x.ByKind));
    }

    [Fact]
    public async Task ReportExcludesMissingPnlAndCountsEstimatedExits()
    {
        var store = new MemoryStore(); var now = DateTimeOffset.UtcNow;
        await store.Write("simtrades.json", new List<SimTrade> { Trade("a", now, "v4") with { ExitEstimated = true }, Trade("b", now, "v4") with { PnlPercent = null } });
        var stats = (await new SimulationReportQueryService(store, TimeProvider.System).GetAsync()).Summary;
        Assert.Equal(2, stats.Closed); Assert.Equal(1, stats.ValidClosed); Assert.Equal(1, stats.MissingPnl); Assert.Equal(1, stats.EstimatedExits); Assert.Equal(100, stats.WinRate);
    }

    [Fact]
    public async Task NullWatchInputIsRejectedWithoutMarketCall()
    {
        var fixture = new ControlFixture(); var result = await fixture.Control.AddAsync(null, default);
        Assert.Equal(WatchlistChangeStatus.Invalid, result.Status); Assert.Equal(0, fixture.Market.StockCalls);
    }

    [Fact]
    public async Task CancellationAfterWatchCommitStillReconcilesSubscriptions()
    {
        using var cts = new CancellationTokenSource(); var fixture = new ControlFixture(); await fixture.Control.StartAsync(default); fixture.Store.AfterUpdate = cts.Cancel;
        var result = await fixture.Control.AddAsync(new("AAPL", "ignored"), cts.Token);
        Assert.Equal(WatchlistChangeStatus.Ok, result.Status);
        Assert.Equal(new[] { "AAPL" }, fixture.Stream.Symbols); Assert.False(fixture.Stream.LastStartToken.CanBeCanceled);
    }

    [Fact]
    public async Task ReorderStoresGivenOrderWhenSymbolSetMatches()
    {
        var fixture = new ControlFixture();
        await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple"), new("MSFT", "Microsoft"), new("NVDA", "Nvidia") });
        var result = await fixture.Control.ReorderAsync(["nvda", "AAPL", "MSFT"], default);
        var durable = await fixture.Store.Read("watchlist.json", new List<WatchItem>());
        Assert.Equal(WatchlistChangeStatus.Ok, result.Status);
        Assert.Equal(["NVDA", "AAPL", "MSFT"], durable.Select(x => x.Symbol));
        Assert.Equal(["Nvidia", "Apple", "Microsoft"], durable.Select(x => x.Name));
    }

    [Fact]
    public async Task ReorderRejectsAnySetMismatchWithoutTouchingStoredOrder()
    {
        string[][] cases = [["AAPL"], ["AAPL", "AAPL"], ["AAPL", "TSLA"], ["AAPL", "MSFT", "TSLA"], ["AAPL", "MSFT", ""]];
        foreach (var symbols in cases)
        {
            var fixture = new ControlFixture();
            await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple"), new("MSFT", "Microsoft") });
            var result = await fixture.Control.ReorderAsync(symbols, default);
            var durable = await fixture.Store.Read("watchlist.json", new List<WatchItem>());
            Assert.Equal(WatchlistChangeStatus.Invalid, result.Status);
            Assert.Equal(["AAPL", "MSFT"], durable.Select(x => x.Symbol));
        }
    }

    [Fact]
    public async Task ReorderRejectsEmptyRequestOnEmptyWatchlist()
    {
        var fixture = new ControlFixture();
        Assert.Equal(WatchlistChangeStatus.Invalid, (await fixture.Control.ReorderAsync([], default)).Status);
        Assert.Equal(WatchlistChangeStatus.Invalid, (await fixture.Control.ReorderAsync(null, default)).Status);
    }

    [Fact]
    public async Task ReorderKeepsSubscriptionsUntouchedWhileRunning()
    {
        var fixture = new ControlFixture();
        await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple"), new("MSFT", "Microsoft") });
        await fixture.Control.StartAsync(default);
        var before = fixture.Stream.StartCalls;
        var result = await fixture.Control.ReorderAsync(["MSFT", "AAPL"], default);
        Assert.Equal(WatchlistChangeStatus.Ok, result.Status);
        Assert.Equal(before, fixture.Stream.StartCalls);
        Assert.Equal(["AAPL", "MSFT"], fixture.Stream.Symbols);
    }

    [Fact]
    public async Task PositionRejectsEntryAtCentPrecisionBoundary()
    {
        var store = new MemoryStore(); await store.Write("watchlist.json", new List<WatchItem> { new("PENNY", "Penny") });
        var result = await new PositionService(store, new MonitorRuntimeState()).PutAsync("PENNY", new(.01, 1), default);
        Assert.Equal(PositionChangeStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task MetricsDiscardFetchWhenMonitorStopsWhileAwaitingDailyData()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") });
        var now = DateTimeOffset.Parse("2026-09-09T10:00:00+00:00"); var clock = new FixedClock(now); var pending = new TaskCompletionSource<IReadOnlyList<Candle>>(TaskCreationOptions.RunContinuationsAsynchronously); fixture.Market.Daily = _ => pending.Task;
        using (await fixture.Runtime.EnterControlAsync()) { var gen = fixture.Runtime.CommitStart(); fixture.Runtime.TryCommit(gen, s => s with { Market = new(true, "open", null, now.AddHours(-1), now.AddHours(5)) }); }
        var task = new MetricsQueryService(fixture.Store, fixture.Market, fixture.Stream, fixture.Runtime, clock).GetAsync("AAPL", default);
        using (await fixture.Runtime.EnterControlAsync()) fixture.Runtime.CommitStop(); pending.SetResult(Days(now));
        var result = await task; Assert.False(result.Data!.Running); Assert.False(result.Data.MarketOpen); Assert.Null(result.Data.Daily);
    }

    [Fact]
    public async Task MetricsRejectFutureOrNonUsdTickAsDailyPrice()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") }); var now = DateTimeOffset.Parse("2026-09-09T10:00:00+00:00"); fixture.Market.Daily = _ => Task.FromResult<IReadOnlyList<Candle>>(Days(now)); fixture.Stream.Trade = new("AAPL", 999, 1, now.AddMinutes(1), "KRW");
        using (await fixture.Runtime.EnterControlAsync()) { var gen = fixture.Runtime.CommitStart(); fixture.Runtime.TryCommit(gen, s => s with { Market = new(true, "open", null, now.AddHours(-1), now.AddHours(5)) }); }
        var result = await new MetricsQueryService(fixture.Store, fixture.Market, fixture.Stream, fixture.Runtime, new FixedClock(now)).GetAsync("AAPL", default);
        Assert.NotNull(result.Data!.Daily); Assert.NotEqual(899, result.Data.Daily!.ChangeFromPrevClose);
    }

    [Fact]
    public async Task MetricsDropsResultWhenClockCrossesSessionEndDuringFetch()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") }); var now = DateTimeOffset.Parse("2026-09-09T10:00:00+00:00"); var clock = new FixedClock(now); fixture.Market.Daily = _ => { clock.Now = now.AddHours(1); return Task.FromResult<IReadOnlyList<Candle>>(Days(now)); };
        using (await fixture.Runtime.EnterControlAsync()) { var gen = fixture.Runtime.CommitStart(); fixture.Runtime.TryCommit(gen, s => s with { Market = new(true, "open", null, now.AddHours(-1), now.AddMinutes(30)) }); }
        var result = await new MetricsQueryService(fixture.Store, fixture.Market, fixture.Stream, fixture.Runtime, clock).GetAsync("AAPL", default);
        Assert.False(result.Data!.MarketOpen); Assert.Null(result.Data.Daily);
    }

    static IReadOnlyList<Candle> Days(DateTimeOffset now) => [new(now.AddDays(-3), 100, 101, 99, 100, 1000), new(now.AddDays(-2), 100, 101, 99, 100, 1000), new(now.AddDays(-1), 100, 101, 99, 100, 1000), new(now, 100, 102, 99, 101, 500)];

    [Fact]
    public async Task ConcurrentControlCommandsEndInAConsistentTransportState()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") }); await fixture.Control.StartAsync(default);
        await Task.WhenAll(fixture.Control.AddAsync(new("MSFT", "ignored"), default), fixture.Control.RemoveAsync("AAPL", default), fixture.Control.StopAsync(default));
        var durable = await fixture.Store.Read("watchlist.json", new List<WatchItem>()); var running = fixture.Runtime.Snapshot().Running;
        Assert.Equal(running ? durable.Select(x => x.Symbol).Order() : [], fixture.Stream.Symbols.Order());
    }

    [Fact]
    public async Task AutoStartEnabledStartsMonitoringOnceAsAuto()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") });
        var service = new MonitorAutoStartService(new MonitorOptions { AutoStart = true }, fixture.Control, new FakeCredentials(true));
        var result = await service.RunOnceAsync(default);
        Assert.Equal(MonitorAutoStartOutcome.Started, result.Outcome);
        var snapshot = fixture.Runtime.Snapshot();
        Assert.True(snapshot.Running);
        Assert.Equal("auto", snapshot.StartedBy);
        Assert.NotNull(snapshot.StartedAt);
        Assert.Equal(1, fixture.Stream.StartCalls);
        Assert.Empty(service.Warnings);
    }

    [Fact]
    public async Task AutoStartDisabledLeavesMonitoringIdle()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") });
        var service = new MonitorAutoStartService(new MonitorOptions(), fixture.Control, new FakeCredentials(true));
        var result = await service.RunOnceAsync(default);
        Assert.Equal(MonitorAutoStartOutcome.Disabled, result.Outcome);
        var snapshot = fixture.Runtime.Snapshot();
        Assert.False(snapshot.Running);
        Assert.Null(snapshot.StartedBy);
        Assert.Null(snapshot.StartedAt);
        Assert.Equal(0, fixture.Stream.StartCalls);
    }

    [Fact]
    public async Task AutoStartWithoutCredentialsWarnsAndDoesNotStart()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") });
        var service = new MonitorAutoStartService(new MonitorOptions { AutoStart = true }, fixture.Control, new FakeCredentials(false));
        var result = await service.RunOnceAsync(default);
        Assert.Equal(MonitorAutoStartOutcome.CredentialsMissing, result.Outcome);
        Assert.False(fixture.Runtime.Snapshot().Running);
        Assert.Equal(0, fixture.Stream.StartCalls);
        Assert.Single(service.Warnings);
        Assert.Contains("자격 증명", service.Warnings[0]);
    }

    [Fact]
    public async Task ManualStartIsRecordedAsManualAndStopClearsIt()
    {
        var fixture = new ControlFixture(); await fixture.Store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") });
        await fixture.Control.StartAsync(default);
        Assert.Equal("manual", fixture.Runtime.Snapshot().StartedBy);
        await fixture.Control.StopAsync(default);
        var snapshot = fixture.Runtime.Snapshot();
        Assert.Null(snapshot.StartedBy);
        Assert.Null(snapshot.StartedAt);
    }

    sealed class FakeCredentials(bool configured) : IMarketCredentialProbe { public Task<bool> HasCredentialsAsync(CancellationToken ct) => Task.FromResult(configured); }

    static SimTrade Trade(string id, DateTimeOffset at, string? logic) => new(id, "AAPL", "SETUP", at, 100, 102, 99, null, null, "TARGET", 102, at.AddMinutes(5), 1.8, 102, Logic: logic);

    sealed class MemoryStore : ILocalStore
    {
        readonly Dictionary<string, object> _values = new(); readonly SemaphoreSlim _gate = new(1, 1);
        public Action? AfterUpdate { get; set; }
        public async Task<T> Read<T>(string file, T fallback) { await _gate.WaitAsync(); try { return _values.TryGetValue(file, out var value) ? (T)value : fallback; } finally { _gate.Release(); } }
        public async Task Write<T>(string file, T data) { await _gate.WaitAsync(); try { _values[file] = data!; } finally { _gate.Release(); } }
        public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change) { await _gate.WaitAsync(); try { var next = change(_values.TryGetValue(file, out var value) ? (T)value : fallback); _values[file] = next.Data!; AfterUpdate?.Invoke(); AfterUpdate = null; return next.Result; } finally { _gate.Release(); } }
    }

    sealed class ControlFixture
    {
        public MemoryStore Store { get; } = new(); public FakeMarket Market { get; } = new(); public FakeStream Stream { get; } = new(); public MonitorRuntimeState Runtime { get; } = new();
        public MonitorControlService Control { get; }
        public ControlFixture() => Control = new(Store, Market, Stream, new FakeSignals(), Runtime);
    }
    sealed class FakeSignals : IMonitorSignals { public bool TryGet(string symbol, out SignalView signal) { signal = null!; return false; } public void Remove(string symbol) { } public void Clear() { } }
    sealed class FakeMarket : IMarketDataGateway
    {
        public Func<string, Task<IReadOnlyList<Candle>>> Daily { get; set; } = _ => Task.FromResult<IReadOnlyList<Candle>>([]);
        public int StockCalls; public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) { StockCalls++; return Task.FromResult<IReadOnlyList<WatchItem>>([new(symbols, symbols)]); }
        public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
        public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => Daily(symbol);
        public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => Task.FromResult((1d, DateTimeOffset.UtcNow));
        public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => Task.FromResult(new MarketSession(false, "closed", null, null, null));
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token");
        public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossTrade>>([]);
        public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>([]);
        public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => Task.FromResult<IReadOnlyList<TossAccount>>([]);
        public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossCommission>>([]);
        public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => Task.FromResult(new TossOrderPage([], null, false));
        public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossHolding>>([]);
    }
    sealed class FakeStream : IRealtimeMarketStream
    {
        public string[] Symbols { get; private set; } = []; public CancellationToken LastStartToken { get; private set; } public int StartCalls { get; private set; }
        public TossTrade? Trade { get; set; }
        public string Status => Symbols.Length > 0 ? "connected" : "idle"; public string Message => "test"; public DateTimeOffset? LastTickAt => null;
        public bool TryGetLatest(string symbol, out TossTrade trade) { trade = Trade!; return Trade is not null && Trade.Symbol == symbol; }
        public (decimal Buy, decimal Sell)? Flow(string symbol, DateTimeOffset from, DateTimeOffset to) => null;
        public Task StartAsync(IEnumerable<string> symbols, Func<CancellationToken, Task<string>> token, bool allowed, CancellationToken ct = default) { StartCalls++; LastStartToken = ct; Symbols = symbols.Order().ToArray(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { Symbols = []; return Task.CompletedTask; }
    }
    sealed class FixedClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime(); public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }
}
