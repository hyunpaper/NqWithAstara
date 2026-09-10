namespace Astra.Server.Domain;

public sealed record OrderBookLevel(decimal Price, decimal Volume);
public sealed record OrderBookSnapshot(DateTimeOffset Timestamp, string Currency, IReadOnlyList<OrderBookLevel> Asks, IReadOnlyList<OrderBookLevel> Bids);
public sealed record LiquiditySummary(decimal BestBid, decimal BestAsk, decimal Spread, decimal SpreadBps, decimal DisplayedBidVolume, decimal DisplayedAskVolume, decimal? DisplayedDepthImbalancePercent);

public static class LiquidityCalculator
{
    public static bool TrySummarize(OrderBookSnapshot book, DateTimeOffset now, DateTimeOffset sessionStart, DateTimeOffset sessionEnd, out LiquiditySummary summary)
    {
        summary = null!;
        if (book.Currency != "USD" || book.Timestamp < sessionStart || book.Timestamp >= sessionEnd || book.Timestamp > now.AddSeconds(5) || now - book.Timestamp > TimeSpan.FromSeconds(30)) return false;
        var asks = book.Asks.Where(x => x.Price > 0 && x.Volume > 0).ToArray(); var bids = book.Bids.Where(x => x.Price > 0 && x.Volume > 0).ToArray();
        if (asks.Length == 0 || bids.Length == 0) return false;
        var bestAsk = asks.Min(x => x.Price); var bestBid = bids.Max(x => x.Price); if (bestBid >= bestAsk) return false;
        var spread = bestAsk - bestBid; var midpoint = (bestAsk + bestBid) / 2; var askVolume = asks.Sum(x => x.Volume); var bidVolume = bids.Sum(x => x.Volume); var total = askVolume + bidVolume;
        summary = new(bestBid, bestAsk, spread, midpoint > 0 ? Math.Round(spread / midpoint * 10_000, 2) : 0, bidVolume, askVolume, total > 0 ? Math.Round((bidVolume - askVolume) / total * 100, 1) : null); return true;
    }
}
