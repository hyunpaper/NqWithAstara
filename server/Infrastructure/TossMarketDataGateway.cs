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
}
