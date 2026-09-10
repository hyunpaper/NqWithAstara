using Astra.Server;
using Astra.Server.Application;
using Xunit;

public sealed class MonitorPollingTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T18:00:00Z");
    static readonly MarketSession OpenSession = new(true, "open", null, Now.AddHours(-1), Now.AddHours(3));

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
