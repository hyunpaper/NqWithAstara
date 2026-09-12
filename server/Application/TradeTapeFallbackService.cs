namespace Astra.Server.Application;

/// <summary>
/// 웹소켓 틱이 끊기면 REST 체결 내역(<c>/trades</c>)으로 체결강도를 보정한다.
/// 표시·보정 전용이며 v5 진입 판정에는 쓰지 않는다(설계 §16B).
/// </summary>
public sealed class TradeTapeFallbackService(IMarketDataGateway toss, TickFlowTape tape, IMonitorDiagnostics diagnostics)
{
    public const int TradeFetchCount = 50;

    /// <summary>ws 틱이 신선하면 아무 것도 하지 않는다. 끊겼을 때만 체결 내역을 1회 조회해 병합한다.</summary>
    public async Task RefreshAsync(string symbol, CancellationToken ct)
    {
        if (!tape.IsWebSocketStale(symbol)) return;
        try { tape.MergeRestTrades(symbol, await toss.Trades(symbol, TradeFetchCount, ct)); }
        catch (Exception ex) { diagnostics.MarketDataFailed(symbol, "trade-tape", ex); }
    }

    public string FlowSource(string symbol) => tape.FlowSource(symbol);

    public int? BlockTradeCount(string symbol) => tape.BlockTradeCount(symbol);
}
