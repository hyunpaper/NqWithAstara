using System.Collections.Concurrent;

namespace Astra.Server.Application;

public sealed record MetricsResponse(string Symbol, bool MarketOpen, bool Running, DailyMetrics? Daily, DailyMetrics? Daily5m, DailyMetrics? Daily10m, object? Flow, object? Flow5m, object? Flow10m, DateTimeOffset UpdatedAt);

public sealed class MetricsQueryService(ILocalStore store, IMarketDataGateway marketData, IRealtimeMarketStream stream, MonitorRuntimeState runtime, TimeProvider clock)
{
    sealed record CacheKey(string Symbol, DateTimeOffset Start, DateTimeOffset End);
    readonly ConcurrentDictionary<CacheKey, (DateTimeOffset At, DailyMetrics? Data)> _cache = new();
    readonly ConcurrentDictionary<CacheKey, ConcurrentQueue<(DateTimeOffset At, DailyMetrics Data)>> _history = new();

    public async Task<(int Status, MetricsResponse? Data)> GetAsync(string input, CancellationToken ct)
    {
        var symbol = input.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Z0-9.-]{1,12}$")) return (400, null);
        var watch = await store.Read("watchlist.json", new List<WatchItem>());
        if (watch.All(x => !x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return (404, null);
        var now = clock.GetLocalNow(); var state = runtime.Snapshot(); var session = state.Market; var generation = state.Generation;
        var start = session.Start.GetValueOrDefault(); var end = session.End.GetValueOrDefault();
        var open = state.Running && session.Start.HasValue && session.End.HasValue && MarketRules.IsOpen(now, start, end);
        DailyMetrics? daily = null; CacheKey? key = null;
        if (open)
        {
            key = new(symbol, start, end);
            foreach (var stale in _cache.Keys.Where(x => x.Start != start || x.End != end || watch.All(w => !w.Symbol.Equals(x.Symbol, StringComparison.OrdinalIgnoreCase)))) _cache.TryRemove(stale, out _);
            foreach (var stale in _history.Keys.Where(x => x.Start != start || x.End != end || watch.All(w => !w.Symbol.Equals(x.Symbol, StringComparison.OrdinalIgnoreCase)))) _history.TryRemove(stale, out _);
            if (_cache.TryGetValue(key, out var hit) && now - hit.At < TimeSpan.FromSeconds(20)) daily = hit.Data;
            else
            {
                var days = await marketData.DailyCandles(symbol, ct);
                now = clock.GetLocalNow(); var latest = runtime.Snapshot();
                if (!latest.Running || latest.Generation != generation || latest.Market.Start != session.Start || latest.Market.End != session.End || !MarketRules.IsOpen(now, start, end))
                    return (200, new(symbol, false, latest.Running, null, null, null, null, null, null, clock.GetLocalNow()));
                double? live = stream.TryGetLatest(symbol, out var tick) && tick.Currency == "USD" && tick.Timestamp >= start && tick.Timestamp < end && tick.Timestamp <= now.AddSeconds(5) && now - tick.Timestamp <= TimeSpan.FromMinutes(3) ? (double)tick.Price : null;
                daily = DailyStats.Compute(days, session with { IsOpen = true }, now, live); _cache[key] = (now, daily);
                if (daily is not null) { var queue = _history.GetOrAdd(key, _ => new()); queue.Enqueue((now, daily)); while (queue.TryPeek(out var oldest) && now - oldest.At > TimeSpan.FromMinutes(25)) queue.TryDequeue(out _); }
            }
        }
        DailyMetrics? Near(TimeSpan ago) { if (key is null || !_history.TryGetValue(key, out var q)) return null; var target = now - ago; return q.Where(x => (x.At - target).Duration() <= TimeSpan.FromMinutes(2.5)).OrderBy(x => (x.At - target).Duration()).Select(x => x.Data).FirstOrDefault(); }
        object? Flow(DateTimeOffset from, DateTimeOffset to) { from = from < start ? start : from; to = to > now ? now : to; if (to > end) to = end; if (from >= to) return null; var flow = stream.Flow(symbol, from, to); return flow is null ? null : new { buyVolume = flow.Value.Buy, sellVolume = flow.Value.Sell, strength = flow.Value.Sell > 0 ? Math.Round((double)(flow.Value.Buy / flow.Value.Sell) * 100, 1) : (double?)null, buyShare = flow.Value.Buy + flow.Value.Sell > 0 ? Math.Round((double)(flow.Value.Buy / (flow.Value.Buy + flow.Value.Sell)) * 100, 1) : (double?)null, windowMinutes = 5 }; }
        return (200, new(symbol, open, state.Running, daily, open ? Near(TimeSpan.FromMinutes(5)) : null, open ? Near(TimeSpan.FromMinutes(10)) : null,
            open ? Flow(now.AddMinutes(-5), now) : null, open ? Flow(now.AddMinutes(-10), now.AddMinutes(-5)) : null, open ? Flow(now.AddMinutes(-15), now.AddMinutes(-10)) : null, now));
    }
}
