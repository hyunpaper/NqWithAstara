using Astra.Server;
using Astra.Server.Application;
using Xunit;

public sealed class TradeTapeFallbackTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T18:00:00Z");

    [Fact]
    public async Task WebSocketTicksKeepRestGatewayUntouched()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        var gateway = new TradeGateway([Trade("NVDA", 100m, 10m, Now.AddSeconds(-30)), Trade("NVDA", 101m, 10m, Now.AddSeconds(-20))]);
        var fallback = new TradeTapeFallbackService(gateway, tape, new SilentDiagnostics());
        tape.RecordTick(Trade("NVDA", 200m, 5m, Now.AddSeconds(-5)));

        await fallback.RefreshAsync("NVDA", default);

        Assert.Equal(0, gateway.Calls);
        Assert.Equal("ws", tape.FlowSource("NVDA"));
    }

    [Fact]
    public async Task StaleWebSocketFallsBackToTradeTapeOnce()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        tape.RecordTick(Trade("NVDA", 200m, 5m, Now));
        var gateway = new TradeGateway(
        [
            Trade("NVDA", 201m, 7m, Now.AddSeconds(65)),
            Trade("NVDA", 202m, 9m, Now.AddSeconds(66)),
        ]);
        var fallback = new TradeTapeFallbackService(gateway, tape, new SilentDiagnostics());
        clock.Now = Now.AddSeconds(70);

        await fallback.RefreshAsync("NVDA", default);

        Assert.Equal(1, gateway.Calls);
        Assert.Equal(50, gateway.LastCount);
        Assert.Equal("rest", tape.FlowSource("NVDA"));
        var flow = tape.Flow("NVDA", Now.AddSeconds(-1), Now.AddSeconds(120));
        Assert.NotNull(flow);
        Assert.Equal(16m, flow!.Value.Buy);
        Assert.Equal(0m, flow.Value.Sell);
    }

    [Fact]
    public async Task ReturningWebSocketTickRestoresWebSocketSource()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        tape.RecordTick(Trade("NVDA", 200m, 5m, Now));
        var gateway = new TradeGateway([Trade("NVDA", 201m, 7m, Now.AddSeconds(65))]);
        var fallback = new TradeTapeFallbackService(gateway, tape, new SilentDiagnostics());
        clock.Now = Now.AddSeconds(70);
        await fallback.RefreshAsync("NVDA", default);
        Assert.Equal("rest", tape.FlowSource("NVDA"));

        clock.Now = Now.AddSeconds(80);
        tape.RecordTick(Trade("NVDA", 203m, 4m, Now.AddSeconds(79)));
        await fallback.RefreshAsync("NVDA", default);

        Assert.Equal("ws", tape.FlowSource("NVDA"));
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public void AlreadySeenTradesAreNotCountedTwice()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        TossTrade[] trades =
        [
            Trade("NVDA", 100m, 3m, Now.AddSeconds(-30)),
            Trade("NVDA", 101m, 4m, Now.AddSeconds(-20)),
            Trade("NVDA", 102m, 5m, Now.AddSeconds(-10)),
        ];

        Assert.Equal(3, tape.MergeRestTrades("NVDA", trades));
        Assert.Equal(0, tape.MergeRestTrades("NVDA", trades));

        var flow = tape.Flow("NVDA", Now.AddMinutes(-5), Now.AddMinutes(5));
        Assert.NotNull(flow);
        Assert.Equal(9m, flow!.Value.Buy);
    }

    [Fact]
    public void SameTimestampWithDifferentVolumeIsStillCounted()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        var at = Now.AddSeconds(-10);

        tape.MergeRestTrades("NVDA", [Trade("NVDA", 100m, 3m, at)]);
        var merged = tape.MergeRestTrades("NVDA", [Trade("NVDA", 100m, 3m, at), Trade("NVDA", 100m, 4m, at)]);

        Assert.Equal(1, merged);
    }

    [Fact]
    public void SameSecondPriceVolumeDuplicatesWithinOneBatchAreBothCounted()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        var at = Now.AddSeconds(-10);

        var merged = tape.MergeRestTrades("NVDA", [Trade("NVDA", 218.25m, 1m, at), Trade("NVDA", 218.25m, 1m, at)]);

        Assert.Equal(2, merged);
    }

    [Fact]
    public void RefetchingSameBatchAddsNothing()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        var at = Now.AddSeconds(-10);
        TossTrade[] trades = [Trade("NVDA", 218.25m, 1m, at), Trade("NVDA", 218.25m, 1m, at)];

        tape.MergeRestTrades("NVDA", trades);
        var merged = tape.MergeRestTrades("NVDA", trades);

        Assert.Equal(0, merged);
    }

    [Fact]
    public void RefetchWithAnAdditionalThirdDuplicateAddsOnlyOne()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        var at = Now.AddSeconds(-10);

        tape.MergeRestTrades("NVDA", [Trade("NVDA", 218.25m, 1m, at), Trade("NVDA", 218.25m, 1m, at)]);
        var merged = tape.MergeRestTrades(
            "NVDA",
            [Trade("NVDA", 218.25m, 1m, at), Trade("NVDA", 218.25m, 1m, at), Trade("NVDA", 218.25m, 1m, at)]);

        Assert.Equal(1, merged);
    }

    [Fact]
    public void BlockTradeCountUsesMedianTimesFactorAsExclusiveThreshold()
    {
        var clock = new FixedClock(Now);
        var tape = new TickFlowTape(clock);
        tape.MergeRestTrades("NVDA", Uniform(9, 1m).Append(Trade("NVDA", 150m, 10m, Now.AddSeconds(-1))).ToArray());

        Assert.Equal(0, tape.BlockTradeCount("NVDA"));

        tape.MergeRestTrades("NVDA", [Trade("NVDA", 151m, 11m, Now)]);

        Assert.Equal(1, tape.BlockTradeCount("NVDA"));
    }

    [Fact]
    public void BlockTradeCountIsNullWithoutSample()
    {
        var tape = new TickFlowTape(new FixedClock(Now));

        Assert.Null(tape.BlockTradeCount("NVDA"));
        Assert.Equal("none", tape.FlowSource("NVDA"));
    }

    static IEnumerable<TossTrade> Uniform(int count, decimal volume)
        => Enumerable.Range(0, count).Select(i => Trade("NVDA", 100m + i, volume, Now.AddSeconds(-60 + i)));

    static TossTrade Trade(string symbol, decimal price, decimal volume, DateTimeOffset at)
        => new(symbol, price, volume, at, "USD");

    sealed class TradeGateway(IReadOnlyList<TossTrade> trades) : IMarketDataGateway
    {
        public int Calls { get; private set; }
        public int LastCount { get; private set; }
        public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct)
        {
            Calls++; LastCount = count; return Task.FromResult(trades);
        }
        public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => Task.FromResult(new MarketSession(false, "closed", null, null, null));
        public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
        public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
        public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => Task.FromResult((0d, DateTimeOffset.MinValue));
        public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token");
        public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>([]);
        public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => Task.FromResult<IReadOnlyList<TossAccount>>([]);
        public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossCommission>>([]);
        public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => Task.FromResult(new TossOrderPage([], null, false));
        public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossHolding>>([]);
    }

    sealed class SilentDiagnostics : IMonitorDiagnostics
    {
        public void PollFailed(string scope, Exception exception) { }
        public void MarketDataFailed(string symbol, string operation, Exception exception) { }
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
