using Astra.Server.Application;
using Astra.Server.Domain;

namespace Astra.Server.Infrastructure;

public sealed class TossMarketDataGateway(TossClient client) : IMarketDataGateway, IOrderBookGateway
{
    public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => client.Stocks(symbols, ct);
    public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => client.Candles(symbol, ct);
    public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => client.DailyCandles(symbol, ct);
    public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => client.Price(symbol, ct);
    public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => client.Session(now, ct);
    public Task<OrderBookSnapshot> OrderBook(string symbol, CancellationToken ct) => client.OrderBook(symbol, ct);
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => client.GetAccessTokenAsync(ct);
    public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => client.Trades(symbol, count, ct);
    public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => client.StockInfos(symbols, ct);
    public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => client.Accounts(ct);
    public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => client.Commissions(accountSeq, ct);
    public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => client.ClosedOrders(accountSeq, from, to, cursor, limit, ct);
    public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => client.Holdings(accountSeq, ct);
}
