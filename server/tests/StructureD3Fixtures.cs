using System.Text;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;

/// <summary>
/// v5 구조 엔진 D3 테스트 fixture. 임의 값이며 실제 종목 추천이 아니다.
/// 운영 App_Data·실제 게이트웨이·서버 실행을 쓰지 않고 fake gateway/TimeProvider/메모리 저장소만 쓴다.
/// </summary>
static class D3
{
    public static readonly DateTimeOffset SessionStart = Fx.SessionStart;
    public static readonly DateTimeOffset SessionEnd = Fx.SessionEnd;
    public const string Symbol = "TEST";

    public static MarketSession Session => new(true, "정규장", null, SessionStart, SessionEnd);
    public static DateTimeOffset At(int minute) => SessionStart.AddMinutes(minute);

    /// <summary>결정적 합성 1분봉. 난수가 없으므로 같은 입력은 항상 같은 결과다(§3).</summary>
    public static Candle Candle(int i)
    {
        var mid = 100d + (i % 7 - 3) * .05 + (i % 11 - 5) * .03;
        return new Candle(At(i), mid, mid + .12, mid - .11, mid + (i % 3 - 1) * .04, 1000 + i % 13 * 100);
    }

    public static Candle[] Candles(int count) => Enumerable.Range(0, count).Select(Candle).ToArray();

    /// <summary>완료 일봉과 진행 중(당일) 일봉. 당일 봉은 snapshot factory가 제거해야 한다(§16B).</summary>
    public static Candle[] Daily(int completedSessions = 5, bool includeCurrentTradingDay = true)
    {
        var days = new List<Candle>();
        for (var i = completedSessions; i >= 1; i--)
            days.Add(new Candle(SessionStart.AddDays(-i), 99, 100.5 + i * .1, 98.5, 99.5, 1_000_000));
        if (includeCurrentTradingDay) days.Add(new Candle(SessionStart.AddMinutes(1), 100, 130, 99, 129, 500_000));
        return [.. days];
    }

    public static StructureAnalysisService Service(StructureEngineMode mode, MemoryObservationStore observations,
        MonitorRuntimeState runtime, TimeProvider clock, ILocalStore? store = null,
        ConfluenceService? confluence = null) =>
        new(store ?? new RecordingStore(), new StructureObservationWriter(observations, StructurePolicy.Default),
            runtime, clock, new SilentDiagnostics(), new StructureEngineOptions(mode), StructurePolicy.Default,
            confluence: confluence);

    public static long StartedRuntime(MonitorRuntimeState runtime, MarketSession? session = null)
    {
        var generation = runtime.CommitStart();
        runtime.TryCommit(generation, s => s with { Market = session ?? Session });
        return generation;
    }

    public static StructureObservationRequest Request(long generation, int barCount, MarketSession? session = null,
        DateTimeOffset? quoteAt = null, double? quotePrice = 100.0, Candle[]? daily = null) =>
        new(Symbol, generation, session ?? Session, Candles(barCount), daily ?? Daily(), quotePrice,
            quoteAt ?? At(barCount - 1), null);
}

/// <summary>시각을 임의로 옮길 수 있는 TimeProvider. 한 평가 안에서 시각이 흔들리지 않게 명시적으로만 바꾼다.</summary>
sealed class MovableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

sealed class SilentDiagnostics : IMonitorDiagnostics
{
    public List<(string Scope, Exception Exception)> Failures { get; } = [];
    public void PollFailed(string scope, Exception exception) => Failures.Add((scope, exception));
    public void MarketDataFailed(string symbol, string operation, Exception exception) => Failures.Add((operation, exception));
}

/// <summary>v5 전용 관측·래치 저장소의 메모리 구현. 상호작용 횟수까지 기록해 off 모드의 무동작을 증명한다.</summary>
sealed class MemoryObservationStore : IStructureObservationStore
{
    public Dictionary<string, List<string>> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);
    public int Appends { get; private set; }
    public int TextWrites { get; private set; }
    public int Interactions { get; private set; }
    public long? ForcedSize { get; set; }
    public Action? BeforeAppend { get; set; }
    public Exception? AppendFailure { get; set; }
    /// <summary>파일별 텍스트 쓰기 실패 주입(이슈 #26 알림 영속 원자성 테스트). null 반환은 성공이다.</summary>
    public Func<string, Exception?>? TextWriteFailure { get; set; }

    public List<string> Lines(string file) => Files.TryGetValue(file, out var lines) ? lines : [];
    public IEnumerable<string> AllLines => Files.Values.SelectMany(x => x);

    public Task<long> SizeAsync(string file, CancellationToken ct)
    {
        Interactions++;
        if (ForcedSize is { } forced) return Task.FromResult(forced);
        return Task.FromResult(Lines(file).Sum(x => (long)Encoding.UTF8.GetByteCount(x) + 1));
    }

    public Task<IReadOnlyList<string>> ReadLinesAsync(string file, CancellationToken ct)
    {
        Interactions++;
        return Task.FromResult<IReadOnlyList<string>>(Lines(file).ToArray());
    }

    public Task AppendAsync(string file, string line, CancellationToken ct)
    {
        Interactions++;
        BeforeAppend?.Invoke();
        if (AppendFailure is { } failure) throw failure;
        if (!Files.TryGetValue(file, out var lines)) Files[file] = lines = [];
        lines.Add(line);
        Appends++;
        return Task.CompletedTask;
    }

    public Task<string?> ReadTextAsync(string file, CancellationToken ct)
    {
        Interactions++;
        return Task.FromResult(Texts.TryGetValue(file, out var text) ? text : null);
    }

    public Task WriteTextAsync(string file, string content, CancellationToken ct)
    {
        Interactions++;
        if (TextWriteFailure?.Invoke(file) is { } failure) throw failure;
        Texts[file] = content;
        TextWrites++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// 실제 거래 저장소. 쓰기 호출을 파일별로 기록해 v4/v5 경로의 저장 결과를 바이트 단위로 비교할 수 있게 한다.
/// </summary>
sealed class RecordingStore : ILocalStore
{
    readonly SemaphoreSlim _gate = new(1, 1);
    public List<WatchItem> Watch { get; } = [];
    public List<SimTrade> Trades { get; private set; } = [];
    public Dictionary<string, Position> Positions { get; private set; } = [];
    public List<string> Writes { get; } = [];
    public bool RejectWrites { get; set; }

    /// <summary>읽기 호출을 파일별로 기록한다 — 전역 파일 세마포어를 잡는 횟수를 단언하는 데 쓴다(이슈 #67).</summary>
    public List<string> Reads { get; } = [];

    /// <summary>테스트 시작 상태를 심는다(예: active 전환 시점에 이미 열려 있던 v4 거래).</summary>
    public void Seed(params SimTrade[] trades) => Trades = trades.ToList();

    public async Task<T> Read<T>(string file, T fallback)
    {
        await _gate.WaitAsync();
        try { Reads.Add(file); return Clone(file, fallback); }
        finally { _gate.Release(); }
    }

    public async Task Write<T>(string file, T data)
    {
        await _gate.WaitAsync();
        try
        {
            if (RejectWrites) throw new InvalidOperationException($"v5 경로는 {file}에 쓰지 않아야 한다.");
            Writes.Add(file);
            Store(file, data!);
        }
        finally { _gate.Release(); }
    }

    public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
    {
        await _gate.WaitAsync();
        try
        {
            if (RejectWrites) throw new InvalidOperationException($"v5 경로는 {file}에 쓰지 않아야 한다.");
            var changed = change(Clone(file, fallback));
            Writes.Add(file);
            Store(file, changed.Data!);
            return changed.Result;
        }
        finally { _gate.Release(); }
    }

    void Store(string file, object data)
    {
        if (file == "simtrades.json" && data is List<SimTrade> trades) Trades = trades;
        if (file == "positions.json" && data is Dictionary<string, Position> positions) Positions = positions;
    }

    T Clone<T>(string file, T fallback) => file switch
    {
        "watchlist.json" => (T)(object)Watch.ToList(),
        "simtrades.json" => (T)(object)Trades.ToList(),
        "positions.json" => (T)(object)new Dictionary<string, Position>(Positions),
        _ => fallback
    };
}

sealed class ScriptedGateway(MarketSession session, Func<Candle[]> bars, Func<(double Price, DateTimeOffset At)> quote,
    Func<Candle[]>? daily = null) : IMarketDataGateway
{
    public int DailyCalls { get; private set; }
    public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => Task.FromResult(session);
    public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>(bars());
    public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => Task.FromResult(quote());
    public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct)
    { DailyCalls++; return Task.FromResult<IReadOnlyList<Candle>>(daily?.Invoke() ?? []); }
    public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token");
    public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossTrade>>([]);
    public Func<string, StockInfo[]>? StockInfoSource { get; set; }
    public List<string> StockInfoRequests { get; } = [];
    public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct)
    { StockInfoRequests.Add(symbols); return Task.FromResult<IReadOnlyList<StockInfo>>(StockInfoSource?.Invoke(symbols) ?? []); }
    public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => Task.FromResult<IReadOnlyList<TossAccount>>([]);
    public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossCommission>>([]);
    public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => Task.FromResult(new TossOrderPage([], null, false));
    public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossHolding>>([]);
}

sealed class QuietStream : IRealtimeMarketStream
{
    public string Status => "idle";
    public string Message => "test";
    public DateTimeOffset? LastTickAt => null;
    public bool TryGetLatest(string symbol, out TossTrade trade) { trade = default!; return false; }
    public (decimal Buy, decimal Sell)? Flow(string symbol, DateTimeOffset from, DateTimeOffset to) => null;
    public Task StartAsync(IEnumerable<string> symbols, Func<CancellationToken, Task<string>> getAccessToken, bool allowedIpConfirmed, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
