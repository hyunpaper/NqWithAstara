using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Xunit;

public sealed class LiquidityTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T15:00:00Z");
    [Fact]
    public void PicksBestPricesByValueAndComputesDisplayedDepth()
    {
        var book = new OrderBookSnapshot(Now, "USD", [new(101.2m, 2), new(101m, 3)], [new(100.5m, 4), new(100.8m, 6)]);
        Assert.True(LiquidityCalculator.TrySummarize(book, Now, Now.AddHours(-1), Now.AddHours(1), out var value));
        Assert.Equal(101m, value.BestAsk); Assert.Equal(100.8m, value.BestBid); Assert.Equal(33.3m, value.DisplayedDepthImbalancePercent);
    }
    [Theory]
    [InlineData("KRW", 0, 100, 101)] [InlineData("USD", 31, 100, 101)] [InlineData("USD", 0, 101, 101)]
    public void RejectsNonUsdStaleAndCrossedBooks(string currency, int ageSeconds, decimal bid, decimal ask)
    {
        var book = new OrderBookSnapshot(Now.AddSeconds(-ageSeconds), currency, [new(ask, 1)], [new(bid, 1)]);
        Assert.False(LiquidityCalculator.TrySummarize(book, Now, Now.AddHours(-1), Now.AddHours(1), out _));
    }
    [Fact]
    public async Task QuerySingleFlightsAndCachesForFiveSeconds()
    {
        var store = new Store(); await store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") }); var state = new MonitorRuntimeState(); using (await state.EnterControlAsync()) { var generation = state.CommitStart(); state.TryCommit(generation, x => x with { Market = new(true, "open", null, Now.AddHours(-1), Now.AddHours(1)) }); }
        var gateway = new Gateway(new(Now, "USD", [new(101, 1)], [new(100, 1)])); var query = new LiquidityQueryService(store, gateway, state, new Clock());
        var results = await Task.WhenAll(query.GetAsync("AAPL", default), query.GetAsync("AAPL", default));
        Assert.All(results, x => Assert.Equal("ready", x.Response!.Status)); Assert.Equal(1, gateway.Calls);
        await query.GetAsync("AAPL", default); Assert.Equal(1, gateway.Calls);
    }
    [Fact]
    public async Task InvalidStaleBookUsesShortCacheInsteadOfRefetching()
    {
        var store = new Store(); await store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") }); var state = new MonitorRuntimeState(); using (await state.EnterControlAsync()) { var generation = state.CommitStart(); state.TryCommit(generation, x => x with { Market = new(true, "open", null, Now.AddHours(-1), Now.AddHours(1)) }); }
        var gateway = new Gateway(new(Now.AddMinutes(-1), "USD", [new(101, 1)], [new(100, 1)])); var query = new LiquidityQueryService(store, gateway, state, new Clock());
        Assert.Equal("invalid", (await query.GetAsync("AAPL", default)).Response!.Status); Assert.Equal("invalid", (await query.GetAsync("AAPL", default)).Response!.Status); Assert.Equal(1, gateway.Calls);
    }
    [Fact]
    public async Task CancelledWaiterDoesNotRemoveSharedInflightFetch()
    {
        var (store, state) = await Ready(); var gateway = new HoldingGateway(new(Now, "USD", [new(101, 1)], [new(100, 1)])); var query = new LiquidityQueryService(store, gateway, state, new Clock()); using var cts = new CancellationTokenSource();
        var first = query.GetAsync("AAPL", cts.Token); await gateway.Started.Task; cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        var second = query.GetAsync("AAPL", default); Assert.Equal(1, gateway.Calls); gateway.Release.SetResult(); Assert.Equal("ready", (await second).Response!.Status); Assert.Equal(1, gateway.Calls);
    }
    [Fact]
    public async Task SessionStopDuringFetchCannotPublishReadyBook()
    {
        var (store, state) = await Ready(); var gateway = new HoldingGateway(new(Now, "USD", [new(101, 1)], [new(100, 1)])); var query = new LiquidityQueryService(store, gateway, state, new Clock()); var request = query.GetAsync("AAPL", default); await gateway.Started.Task;
        using (await state.EnterControlAsync()) state.CommitStop(); gateway.Release.SetResult(); Assert.Equal("stopped", (await request).Response!.Status);
    }
    [Fact]
    public void RejectsFutureEmptyAndZeroVolumeBooks()
    {
        Assert.False(LiquidityCalculator.TrySummarize(new(Now.AddSeconds(6), "USD", [new(101, 1)], [new(100, 1)]), Now, Now.AddHours(-1), Now.AddHours(1), out _));
        Assert.False(LiquidityCalculator.TrySummarize(new(Now, "USD", [], [new(100, 1)]), Now, Now.AddHours(-1), Now.AddHours(1), out _));
        Assert.False(LiquidityCalculator.TrySummarize(new(Now, "USD", [new(101, 0)], [new(100, 1)]), Now, Now.AddHours(-1), Now.AddHours(1), out _));
    }
    static async Task<(Store Store, MonitorRuntimeState State)> Ready()
    {
        var store = new Store(); await store.Write("watchlist.json", new List<WatchItem> { new("AAPL", "Apple") }); var state = new MonitorRuntimeState(); using (await state.EnterControlAsync()) { var generation = state.CommitStart(); state.TryCommit(generation, x => x with { Market = new(true, "open", null, Now.AddHours(-1), Now.AddHours(1)) }); } return (store, state);
    }
    sealed class Gateway(OrderBookSnapshot value) : IOrderBookGateway { public int Calls; public async Task<OrderBookSnapshot> OrderBook(string symbol, CancellationToken ct) { Interlocked.Increment(ref Calls); await Task.Yield(); return value; } }
    sealed class HoldingGateway(OrderBookSnapshot value) : IOrderBookGateway { public int Calls; public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public async Task<OrderBookSnapshot> OrderBook(string symbol, CancellationToken ct) { Interlocked.Increment(ref Calls); Started.TrySetResult(); await Release.Task; return value; } }
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc; }
    sealed class Store : ILocalStore
    {
        readonly Dictionary<string, object> values = new(); public Task<T> Read<T>(string f, T fallback) => Task.FromResult(values.TryGetValue(f, out var x) ? (T)x : fallback); public Task Write<T>(string f, T value) { values[f] = value!; return Task.CompletedTask; }
        public Task<R> Update<T, R>(string f, T fallback, Func<T, (T Data, R Result)> change) { var result = change(values.TryGetValue(f, out var x) ? (T)x : fallback); values[f] = result.Data!; return Task.FromResult(result.Result); }
    }
}
