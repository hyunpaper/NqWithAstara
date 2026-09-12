using Astra.Server.Domain;
namespace Astra.Server.Application;

public interface ILocalStore
{
    Task<T> Read<T>(string file, T fallback);
    Task Write<T>(string file, T data);
    Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change);
}

public interface IMarketDataGateway
{
    Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct);
    Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct);
    Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct);
    Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct);
    Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct);
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct);
    Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct);
    Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct);
    Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct);
    Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct);
    Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct);
}

public interface IOrderBookGateway { Task<OrderBookSnapshot> OrderBook(string symbol, CancellationToken ct); }

/// <summary>
/// v5 구조 엔진 전용 관측·래치 저장소(설계 §16). 실제 거래 기록(<see cref="ILocalStore"/>)과 분리해
/// shadow 모드가 simtrades/positions 경로에 어떤 쓰기도 하지 않도록 보장한다.
/// 삭제·보존 기간 정책은 사용자가 확인 가능한 파일 단위로 남기고 기존 기록을 조용히 삭제하지 않는다.
/// </summary>
public interface IStructureObservationStore
{
    /// <summary>현재 파일 크기(바이트). 없으면 0이다. 일자 상한 검사에 쓴다.</summary>
    Task<long> SizeAsync(string file, CancellationToken ct);
    /// <summary>append 중복 방지를 위한 기존 줄 읽기. 없으면 빈 목록이다.</summary>
    Task<IReadOnlyList<string>> ReadLinesAsync(string file, CancellationToken ct);
    Task AppendAsync(string file, string line, CancellationToken ct);
    Task<string?> ReadTextAsync(string file, CancellationToken ct);
    Task WriteTextAsync(string file, string content, CancellationToken ct);
}

/// <summary>완료 1분봉 파일 저장소(#165 C5). `App_Data/bars/&lt;날짜&gt;/&lt;심볼&gt;.jsonl` 아래에만 쓴다.</summary>
public interface IBarStore
{
    Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct);
    Task AppendAsync(string day, string symbol, string line, CancellationToken ct);
    Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct);
    Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct);
    Task DeleteDayAsync(string day, CancellationToken ct);
}

/// <summary>RS 기법(#167 C3)이 읽는 당일 벤치마크 완료 봉. 새 외부 호출을 만들지 않고 기존 폴링 결과만 노출한다.</summary>
public interface IBenchmarkBarSource
{
    string Symbol { get; }
    IReadOnlyList<Candle> Bars { get; }
}

public interface IMonitorSignals
{
    bool TryGet(string symbol, out SignalView signal);
    void Remove(string symbol);
    void Clear();
}

public interface IMonitorDiagnostics
{
    void PollFailed(string scope, Exception exception);
    void MarketDataFailed(string symbol, string operation, Exception exception);
}

public interface IRealtimeMarketStream
{
    string Status { get; }
    string Message { get; }
    DateTimeOffset? LastTickAt { get; }
    bool TryGetLatest(string symbol, out TossTrade trade);
    (decimal Buy, decimal Sell)? Flow(string symbol, DateTimeOffset from, DateTimeOffset to);
    Task StartAsync(IEnumerable<string> symbols, Func<CancellationToken, Task<string>> getAccessToken, bool allowedIpConfirmed, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
