using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Validation;

static class Rf
{
    public static readonly DateOnly TradingDate = new(2026, 9, 11);
    public static readonly DateTimeOffset FilledAt = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(-4));

    public static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    public static TossOrder Order(string id, string side = "BUY", string status = "FILLED", bool executed = true,
        int minute = 0, DateTimeOffset? filledAt = null) =>
        new(id, "TEST", side, "MARKET", status, FilledAt.AddMinutes(minute - 1),
            executed
                ? new TossOrderExecution(10m, 100.25m, 1002.5m, 1.0025m,
                    filledAt ?? FilledAt.AddMinutes(minute))
                : null);

    public static RealFillsService Service(FakeOrderGateway gateway, MemoryRealFillStore store,
        FakeRealFillDiagnostics? diagnostics = null) =>
        new(gateway, store, new MovableClock(FilledAt), diagnostics ?? new FakeRealFillDiagnostics())
        { PageDelay = TimeSpan.Zero };

    public static string Observation()
    {
        var candidate = new StructureCandidateDto("E1", "PULLBACK", "zone-1", FilledAt.AddMinutes(-2),
            FilledAt.AddMinutes(-1), FilledAt.AddMinutes(-1), FilledAt.AddMinutes(5),
            RealFillCandidateStates.Ready, 62, 100m, 99.6m, 99.4m, 101.5m, 1.6m, null, [], [], [], false, true);
        var record = new StructureObservationRecord("obs-1", "v5-observation.1", "TEST", FilledAt.AddMinutes(-1),
            FilledAt.AddHours(-1), FilledAt.AddMinutes(-1), FilledAt.AddMinutes(-1), FilledAt.AddMinutes(-2),
            "hash-A", "v5-structure.1", "active", "v5", "full", "available", "READY", "E1",
            new StructureTrendDto("UP", 40, 30, 50, .6, .2, 100.1, 99.9, 100, .3, false, 45,
                FilledAt.AddMinutes(-1), [], [], []),
            null, null, [candidate], [], []);
        return JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}

sealed class FakeRealFillDiagnostics : IMonitorDiagnostics
{
    public int Notes;
    public void PollFailed(string scope, Exception exception) { }
    public void MarketDataFailed(string symbol, string operation, Exception exception) => Notes++;
}

sealed class MemoryRealFillStore : IRealFillStore
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public Task<string?> ReadAsync(string file, CancellationToken ct) =>
        Task.FromResult(Files.TryGetValue(file, out var content) ? content : null);

    public Task WriteAsync(string file, string content, CancellationToken ct)
    {
        Files[file] = content;
        return Task.CompletedTask;
    }
}

sealed class FakeOrderGateway(IReadOnlyList<TossOrderPage> pages) : IMarketDataGateway
{
    public List<string?> Cursors { get; } = [];
    public List<int> Limits { get; } = [];
    public List<(DateOnly? From, DateOnly? To)> Ranges { get; } = [];
    public int Calls { get; private set; }
    public Exception? Failure { get; init; }
    public IReadOnlyList<TossAccount> AccountList { get; init; } = [new(7, "BROKERAGE")];

    public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
    public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
    public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
    public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => Task.FromResult((1d, Rf.FilledAt));
    public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => Task.FromResult(new MarketSession(false, "closed", null, null, null));
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token");
    public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossTrade>>([]);
    public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>([]);
    public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => Task.FromResult(AccountList);
    public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossCommission>>([]);
    public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossHolding>>([]);

    public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor,
        int limit, CancellationToken ct)
    {
        if (Failure is { } failure) throw failure;
        Cursors.Add(cursor);
        Limits.Add(limit);
        Ranges.Add((from, to));
        return Task.FromResult(pages[Calls++]);
    }
}
