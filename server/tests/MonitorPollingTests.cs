using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Application.Opening;
using Astra.Server.Domain.Opening;
using Xunit;

public sealed class MonitorPollingTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T18:00:00Z");
    static readonly MarketSession OpenSession = new(true, "open", null, Now.AddHours(-1), Now.AddHours(3));

    static readonly DateTimeOffset OpeningOpen = DateTimeOffset.Parse("2026-09-09T13:30:00Z");

    static OpeningScanService OpeningScan(TimeProvider clock, out MemoryObservationStore obs)
    {
        obs = new MemoryObservationStore();
        return new OpeningScanService(OpeningScanPolicy.Default, new OpeningVolumeProfileSource(new RecordingBarStore()),
            new OpeningScanObservationWriter(obs), clock, new FakeDiagnostics());
    }

    [Fact]
    public async Task WarmupWatchedSymbolIsObservedByOpeningScan()
    {
        var session = new MarketSession(true, "open", null, OpeningOpen, OpeningOpen.AddHours(6.5));
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        var market = new FakeMarket(session, [new Candle(OpeningOpen, 100, 101, 99, 100, 1000)], (100, OpeningOpen.AddMinutes(1).AddSeconds(30)));
        var clock = new FixedTimeProvider(OpeningOpen.AddMinutes(2));
        var svc = OpeningScan(clock, out var obs);
        var runtime = new MonitorRuntimeState();
        var poller = new MonitorPollingService(store, market, new FakeStream(), runtime, clock, new FakeDiagnostics(), openingScan: svc);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.NotEmpty(obs.AllLines);
        Assert.Equal("scanning", svc.Query().Phase);
    }

    [Fact]
    public async Task UnwatchedSymbolIsNotObservedByOpeningScan()
    {
        var session = new MarketSession(true, "open", null, OpeningOpen, OpeningOpen.AddHours(6.5));
        var store = new FakeStore();
        store.Trades.Add(OpenTrade("NVDA", OpeningOpen.AddMinutes(-5), session.End!.Value));
        var market = new FakeMarket(session, [new Candle(OpeningOpen, 100, 101, 99, 100, 1000)], (100, OpeningOpen.AddMinutes(1).AddSeconds(30)));
        var clock = new FixedTimeProvider(OpeningOpen.AddMinutes(2));
        var svc = OpeningScan(clock, out var obs);
        var runtime = new MonitorRuntimeState();
        var poller = new MonitorPollingService(store, market, new FakeStream(), runtime, clock, new FakeDiagnostics(), openingScan: svc);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.Empty(obs.AllLines);
    }

    [Fact]
    public async Task SessionEndBranchFinalizesOpeningScan()
    {
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        var openMarket = new FakeMarket(new MarketSession(true, "open", null, OpeningOpen, OpeningOpen.AddHours(6.5)),
            [new Candle(OpeningOpen, 100, 101, 99, 100, 1000)], (100, OpeningOpen.AddMinutes(1).AddSeconds(30)));
        var openClock = new FixedTimeProvider(OpeningOpen.AddMinutes(2));
        var svc = OpeningScan(openClock, out var obs);
        var runtime = new MonitorRuntimeState();
        var opener = new MonitorPollingService(store, openMarket, new FakeStream(), runtime, openClock, new FakeDiagnostics(), openingScan: svc);
        runtime.CommitStart();
        await opener.PollAsync(default);

        var closedMarket = new FakeMarket(new MarketSession(false, "closed", null, OpeningOpen, OpeningOpen.AddHours(6.5)),
            [], (100, OpeningOpen.AddHours(7)));
        var closeClock = new FixedTimeProvider(OpeningOpen.AddHours(7));
        var closer = new MonitorPollingService(store, closedMarket, new FakeStream(), runtime, closeClock, new FakeDiagnostics(), openingScan: svc);
        await closer.PollAsync(default);

        Assert.Contains(obs.AllLines, x => x.Contains("\"kind\":\"close\""));
    }

    [Fact]
    public async Task RemovedWatchSymbolWithOpenTradeStillExits()
    {
        var store = new FakeStore();
        store.Trades.Add(OpenTrade("NVDA", Now.AddMinutes(-10), Now.AddHours(3)));
        var market = new FakeMarket(OpenSession, Bars(10, stopAt: 5), (100, Now));
        var poller = Create(store, market, out var runtime);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.Equal("STOP", Assert.Single(store.Trades).Status);
    }

    [Fact]
    public async Task WarmupBarsStillStopExistingOpenTrade()
    {
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        store.Trades.Add(OpenTrade("NVDA", Now.AddMinutes(-10), Now.AddHours(3)));
        var poller = Create(store, new FakeMarket(OpenSession, Bars(12, stopAt: 7), (100, Now)), out var runtime);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.Equal("STOP", Assert.Single(store.Trades).Status);
        Assert.Empty(poller.Signals);
        Assert.Equal("connected", poller.ConnectionStatus);
    }

    [Fact]
    public async Task StopWhileDailyFetchPendingPreventsPersistenceAndSignalCommit()
    {
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        var dailyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dailyRelease = new TaskCompletionSource<IReadOnlyList<Candle>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var market = new FakeMarket(OpenSession, Bars(30), (100, Now), async ct =>
        {
            dailyStarted.SetResult();
            return await dailyRelease.Task.WaitAsync(ct);
        });
        var poller = Create(store, market, out var runtime);
        runtime.CommitStart();

        var polling = poller.PollAsync(default);
        await dailyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.CommitStop();
        dailyRelease.SetResult([]);
        await polling;

        Assert.Empty(store.Trades);
        Assert.Equal(0, store.PositionUpdates);
        Assert.Empty(poller.Signals);
    }

    [Fact]
    public async Task EodReplaysFinalStopBarBeforeEstimatedFallback()
    {
        var end = Now.AddMinutes(-1);
        var store = new FakeStore();
        store.Trades.Add(OpenTrade("NVDA", Now.AddHours(-2), end));
        var finalBar = new Candle(end.AddMinutes(-1), 100, 101, 94, 96, 1000);
        var closed = new MarketSession(false, "closed", null, Now.AddHours(-7), end);
        var poller = Create(store, new FakeMarket(closed, [finalBar], (96, end)), out var runtime);
        runtime.CommitStart();

        await poller.PollAsync(default);

        var trade = Assert.Single(store.Trades);
        Assert.Equal("STOP", trade.Status);
        Assert.Equal(95, trade.ExitPrice);
        Assert.False(trade.ExitEstimated);
    }

    static MonitorPollingService Create(FakeStore store, FakeMarket market, out MonitorRuntimeState runtime)
    {
        runtime = new MonitorRuntimeState();
        return new(store, market, new FakeStream(), runtime, new FixedTimeProvider(Now), new FakeDiagnostics());
    }

    [Fact]
    public async Task CompletedBarsAreHandedToTheBarStoreForWatchedSymbols()
    {
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        var market = new FakeMarket(OpenSession, Bars(30), (100, Now));
        var recorder = new RecordingBarStore();
        var barStore = new BarStoreService(recorder, new FixedTimeProvider(Now));
        var runtime = new MonitorRuntimeState();
        var poller = new MonitorPollingService(store, market, new FakeStream(), runtime, new FixedTimeProvider(Now),
            new FakeDiagnostics(), barStore: barStore);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.NotEmpty(recorder.Lines("2026-09-09", "NVDA"));
    }

    [Fact]
    public async Task BenchmarkPollingNeverAppearsInSignalsOrWatchlist()
    {
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        var market = new FakeMarket(OpenSession, Bars(30), (100, Now));
        var recorder = new RecordingBarStore();
        var barStore = new BarStoreService(recorder, new FixedTimeProvider(Now));
        var options = new ConfluenceOptions { Enabled = true, BenchmarkSymbol = "QQQ" };
        var benchmark = new BenchmarkPollingService(market, barStore, options, new FakeDiagnostics());
        var runtime = new MonitorRuntimeState();
        var poller = new MonitorPollingService(store, market, new FakeStream(), runtime, new FixedTimeProvider(Now),
            new FakeDiagnostics(), barStore: barStore, benchmark: benchmark);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.NotEmpty(recorder.Lines("2026-09-09", "QQQ"));
        Assert.DoesNotContain("QQQ", poller.Signals.Keys);
    }

    [Fact]
    public async Task BenchmarkPollFailureIsIsolatedAsADiagnosticAndDoesNotBreakWatchlistPolling()
    {
        var store = new FakeStore();
        store.Watch.Add(new("NVDA", "NVIDIA"));
        var market = new FakeMarket(OpenSession, Bars(30), (100, Now));
        var options = new ConfluenceOptions { Enabled = true, BenchmarkSymbol = "QQQ" };
        var diagnostics = new FakeDiagnostics();
        var benchmark = new BenchmarkPollingService(new ThrowingBenchmarkGateway(market), new BarStoreService(new RecordingBarStore(), new FixedTimeProvider(Now)), options, diagnostics);
        var runtime = new MonitorRuntimeState();
        var poller = new MonitorPollingService(store, market, new FakeStream(), runtime, new FixedTimeProvider(Now),
            diagnostics, benchmark: benchmark);
        runtime.CommitStart();

        await poller.PollAsync(default);

        Assert.NotEmpty(poller.Signals);
    }

    sealed class ThrowingBenchmarkGateway(FakeMarket inner) : IMarketDataGateway
    {
        public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => inner.Session(now, ct);
        public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => throw new HttpRequestException("qqq down");
        public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => inner.Price(symbol, ct);
        public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => inner.DailyCandles(symbol, ct);
        public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => inner.Stocks(symbols, ct);
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => inner.GetAccessTokenAsync(ct);
        public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => inner.Trades(symbol, count, ct);
        public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => inner.StockInfos(symbols, ct);
        public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => inner.Accounts(ct);
        public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => inner.Commissions(accountSeq, ct);
        public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => inner.ClosedOrders(accountSeq, from, to, cursor, limit, ct);
        public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => inner.Holdings(accountSeq, ct);
    }

    sealed class RecordingBarStore : IBarStore
    {
        readonly Dictionary<(string, string), List<string>> _files = new();
        public IReadOnlyList<string> Lines(string day, string symbol) =>
            _files.TryGetValue((day, symbol.ToUpperInvariant()), out var l) ? l : [];
        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct)
        {
            var l = Lines(day, symbol);
            return Task.FromResult(l.Count == 0 ? null : l[^1]);
        }
        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct)
        {
            var key = (day, symbol.ToUpperInvariant());
            if (!_files.TryGetValue(key, out var l)) _files[key] = l = [];
            l.Add(line);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Select(x => x.Item1).Distinct().ToArray());
        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Where(x => x.Item1 == day).Select(x => x.Item2).ToArray());
        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(Lines(day, symbol).Count);
        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(Lines(day, symbol));
        public Task DeleteDayAsync(string day, CancellationToken ct)
        {
            foreach (var key in _files.Keys.Where(x => x.Item1 == day).ToArray()) _files.Remove(key);
            return Task.CompletedTask;
        }
    }

    static SimTrade OpenTrade(string symbol, DateTimeOffset entered, DateTimeOffset end)
        => new("id", symbol, "SETUP", entered, 100, 110, 95, null, null, "OPEN", null, null, null, 100,
            LastEvaluatedBarAt: null, SessionEnd: end, LastPriceAt: entered);

    static Candle[] Bars(int count, int? stopAt = null)
        => Enumerable.Range(1, count).Select(i => new Candle(Now.AddMinutes(-count - 1 + i), 100, 101,
            i == stopAt ? 94 : 99, 100, 1000)).ToArray();

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    sealed class FakeStore : ILocalStore
    {
        readonly SemaphoreSlim gate = new(1, 1);
        public List<WatchItem> Watch { get; } = [];
        public List<SimTrade> Trades { get; private set; } = [];
        public Dictionary<string, Position> Positions { get; private set; } = [];
        public int PositionUpdates { get; private set; }

        public async Task<T> Read<T>(string file, T fallback)
        {
            await gate.WaitAsync();
            try { return Clone<T>(file, fallback); }
            finally { gate.Release(); }
        }
        public Task Write<T>(string file, T data) => Task.CompletedTask;
        public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
        {
            await gate.WaitAsync();
            try
            {
                var changed = change(Clone<T>(file, fallback));
                if (file == "simtrades.json") Trades = (changed.Data as List<SimTrade>)!;
                if (file == "positions.json") { Positions = (changed.Data as Dictionary<string, Position>)!; PositionUpdates++; }
                return changed.Result;
            }
            finally { gate.Release(); }
        }
        T Clone<T>(string file, T fallback) => file switch
        {
            "watchlist.json" => (T)(object)Watch.ToList(),
            "simtrades.json" => (T)(object)Trades.ToList(),
            "positions.json" => (T)(object)new Dictionary<string, Position>(Positions),
            _ => fallback
        };
    }

    sealed class FakeMarket(MarketSession session, IReadOnlyList<Candle> bars, (double Price, DateTimeOffset At) quote,
        Func<CancellationToken, Task<IReadOnlyList<Candle>>>? daily = null) : IMarketDataGateway
    {
        public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => Task.FromResult(session);
        public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult(bars);
        public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => Task.FromResult(quote);
        public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => daily?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<Candle>>([]);
        public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
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
        public string Status => "connected"; public string Message => ""; public DateTimeOffset? LastTickAt => null;
        public bool TryGetLatest(string symbol, out TossTrade trade) { trade = default!; return false; }
        public (decimal Buy, decimal Sell)? Flow(string symbol, DateTimeOffset from, DateTimeOffset to) => null;
        public Task StartAsync(IEnumerable<string> symbols, Func<CancellationToken, Task<string>> getAccessToken, bool allowedIpConfirmed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    sealed class FakeDiagnostics : IMonitorDiagnostics
    {
        public void PollFailed(string scope, Exception exception) { }
        public void MarketDataFailed(string symbol, string operation, Exception exception) { }
    }
}
