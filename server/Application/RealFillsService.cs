using System.Text.Json;
using Astra.Server.Domain;

namespace Astra.Server.Application;

// 이슈 #131 — 실계좌 체결 수집(읽기 전용).
// 주문 생성·정정·취소 API는 호출하지 않는다. `/orders?status=CLOSED` 조회만 한다.
// 저장 필드는 대조에 필요한 것만이다 — 계좌번호·orderId·금액 합계·잔고는 저장하지 않는다.

/// <summary>저장되는 실체결 한 건. 이 레코드에 없는 필드는 수집 단계에서 버려진다(#131).</summary>
public sealed record RealFillRecord(string Symbol, string Side, DateTimeOffset FilledAt,
    decimal AverageFilledPrice, decimal FilledQuantity, decimal Commission, string OrderType);

/// <summary>실체결 하루치 파일(`App_Data/real-fills/YYYY-MM-DD.json`)의 전체 모양(#131).</summary>
public sealed record RealFillDay(string RecordVersion, DateOnly TradingDate, DateTimeOffset CollectedAt,
    IReadOnlyList<RealFillRecord> Fills);

/// <summary>수집 결과. 실패해도 진입·청산을 막지 않으므로 예외 대신 상태로 돌려준다(#131).</summary>
public sealed record RealFillRefreshResult(bool Collected, DateOnly TradingDate, int Fills, int Pages,
    string? Note);

/// <summary>실체결 파일 저장소. 관측·시뮬 거래와 분리된 디렉터리를 쓴다(#131).</summary>
public interface IRealFillStore
{
    Task<string?> ReadAsync(string file, CancellationToken ct);
    Task WriteAsync(string file, string content, CancellationToken ct);
}

/// <summary>
/// 세션 종료 후 1회(또는 수동 요청) 실계좌 체결을 수집해 하루치 파일로 남긴다(#131).
/// </summary>
public sealed class RealFillsService(IMarketDataGateway gateway, IRealFillStore store, TimeProvider clock,
    IMonitorDiagnostics diagnostics)
{
    public const string RecordVersion = "real-fills.1";

    const string BrokerageAccountType = "BROKERAGE";
    const string FilledStatus = "FILLED";
    const int PageLimit = 100;
    const int MaxPages = 50;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly object _gate = new();
    DateOnly? _collectedDate;

    /// <summary>ORDER_HISTORY 5/s 여유를 두기 위한 페이지 간 간격. 테스트에서만 0으로 둔다.</summary>
    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public static string FileName(DateOnly tradingDate) => $"{tradingDate:yyyy-MM-dd}.json";

    /// <summary>세션이 끝난 거래일에 대해 하루 한 번만 수집한다. 이미 수집했으면 아무 것도 하지 않는다.</summary>
    public async Task<RealFillRefreshResult?> CollectOnSessionEndAsync(DateOnly tradingDate, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_collectedDate == tradingDate) return null;
            _collectedDate = tradingDate;
        }
        return await Collect(tradingDate, ct);
    }

    /// <summary>수동 수집. 같은 날을 다시 요청하면 파일을 새로 쓴다.</summary>
    public Task<RealFillRefreshResult> RefreshAsync(DateOnly? date, CancellationToken ct)
    {
        var tradingDate = date ?? MarketRules.TradingDate(clock.GetUtcNow());
        lock (_gate) _collectedDate = tradingDate;
        return Collect(tradingDate, ct);
    }

    public async Task<RealFillDay?> ReadAsync(DateOnly tradingDate, CancellationToken ct)
    {
        var text = await store.ReadAsync(FileName(tradingDate), ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<RealFillDay>(text, Json); }
        catch (JsonException) { return null; }
    }

    async Task<RealFillRefreshResult> Collect(DateOnly tradingDate, CancellationToken ct)
    {
        try
        {
            var accounts = await gateway.Accounts(ct);
            var account = accounts.FirstOrDefault(x => x.AccountType == BrokerageAccountType);
            if (account is null)
            {
                diagnostics.MarketDataFailed("ACCOUNT", "real-fills",
                    new InvalidOperationException("REAL_FILLS_UNAVAILABLE: no brokerage account"));
                return new RealFillRefreshResult(false, tradingDate, 0, 0, "REAL_FILLS_NO_ACCOUNT");
            }

            var fills = new List<RealFillRecord>();
            string? cursor = null;
            var pages = 0;
            while (pages < MaxPages)
            {
                ct.ThrowIfCancellationRequested();
                var page = await gateway.ClosedOrders(account.AccountSeq, tradingDate, tradingDate, cursor,
                    PageLimit, ct);
                pages++;
                fills.AddRange(page.Orders.Where(Filled).Select(Record));
                if (!page.HasNext || string.IsNullOrEmpty(page.NextCursor)) break;
                cursor = page.NextCursor;
                if (PageDelay > TimeSpan.Zero) await Task.Delay(PageDelay, ct);
            }

            var day = new RealFillDay(RecordVersion, tradingDate, clock.GetUtcNow(),
                fills.OrderBy(x => x.FilledAt).ThenBy(x => x.Symbol, StringComparer.Ordinal).ToArray());
            await store.WriteAsync(FileName(tradingDate), JsonSerializer.Serialize(day, Json), ct);
            return new RealFillRefreshResult(true, tradingDate, day.Fills.Count, pages, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.MarketDataFailed("ORDER_HISTORY", "real-fills", ex);
            return new RealFillRefreshResult(false, tradingDate, 0, 0, "REAL_FILLS_FAILED");
        }
    }

    static bool Filled(TossOrder order) =>
        order.Execution is not null && string.Equals(order.Status, FilledStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>게이트웨이가 계좌번호·orderId·체결 금액을 채워 와도 이 변환에서 전부 떨어진다.</summary>
    static RealFillRecord Record(TossOrder order) =>
        new(order.Symbol, order.Side, order.Execution!.FilledAt, order.Execution.AverageFilledPrice,
            order.Execution.FilledQuantity, order.Execution.Commission, order.OrderType);
}
