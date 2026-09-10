using System.Collections.Concurrent;
using Astra.Server.Domain;

namespace Astra.Server.Application;

public sealed record LiquidityResponse(string Symbol, string Status, LiquiditySummary? Liquidity, DateTimeOffset? ObservedAt, DateTimeOffset UpdatedAt, string Source, string DepthLabel, string? Message);

public sealed class LiquidityQueryService(ILocalStore store, IOrderBookGateway orderBooks, MonitorRuntimeState runtime, TimeProvider clock)
{
    sealed record CacheEntry(long Generation, DateTimeOffset SessionStart, DateTimeOffset SessionEnd, DateTimeOffset ExpiresAt, LiquidityResponse Response);
    readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Lazy<Task<LiquidityResponse>>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<(int HttpStatus, LiquidityResponse? Response)> GetAsync(string input, CancellationToken ct)
    {
        var symbol = input.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Z0-9.-]{1,12}$")) return (400, null);
        var watch = await store.Read("watchlist.json", new List<WatchItem>()); if (watch.All(x => !x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return (404, null);
        foreach (var stale in _cache.Keys.Where(x => watch.All(w => !w.Symbol.Equals(x, StringComparison.OrdinalIgnoreCase)))) _cache.TryRemove(stale, out _);
        var state = runtime.Snapshot(); var now = clock.GetLocalNow();
        if (!state.Running) return (200, Empty(symbol, "stopped", now, "모니터링이 중지되어 있습니다."));
        if (state.Market.Start is not { } start || state.Market.End is not { } end || !MarketRules.IsOpen(now, start, end)) return (200, Empty(symbol, "marketClosed", now, "미국 정규장 외에는 호가를 조회하지 않습니다."));
        if (_cache.TryGetValue(symbol, out var cached) && cached.Generation == state.Generation && cached.SessionStart == start && cached.SessionEnd == end && now < cached.ExpiresAt && (cached.Response.Status != "ready" || cached.Response.ObservedAt is null || now - cached.Response.ObservedAt <= TimeSpan.FromSeconds(30))) return (200, cached.Response);
        var lazy = _inflight.GetOrAdd(symbol, _ => new(() => FetchAndFinalizeAsync(symbol, state.Generation, start, end), LazyThreadSafetyMode.ExecutionAndPublication));
        return (200, await lazy.Value.WaitAsync(ct));
    }

    async Task<LiquidityResponse> FetchAndFinalizeAsync(string symbol, long generation, DateTimeOffset start, DateTimeOffset end)
    {
        try
        {
            var book = await orderBooks.OrderBook(symbol, CancellationToken.None); var now = clock.GetLocalNow(); var latest = runtime.Snapshot();
            if (!latest.Running || latest.Generation != generation || latest.Market.Start != start || latest.Market.End != end || !MarketRules.IsOpen(now, start, end)) return Empty(symbol, latest.Running ? "marketClosed" : "stopped", now, "조회 중 세션 상태가 변경되었습니다.");
            LiquidityResponse response = LiquidityCalculator.TrySummarize(book, now, start, end, out var summary)
                ? new(symbol, "ready", summary, book.Timestamp, now, "Toss Securities orderbook", "표시 호가 잔량 추정치 (실제 매수·매도 체결 흐름 아님)", null)
                : new(symbol, "invalid", null, book.Timestamp, now, "Toss Securities orderbook", "표시 호가 잔량 추정치", "비USD·지연·빈 호가 또는 교차 호가로 사용할 수 없습니다.");
            _cache[symbol] = new(generation, start, end, now.AddSeconds(5), response); return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            var now = clock.GetLocalNow(); var response = Empty(symbol, "unavailable", now, "Toss 호가를 일시적으로 조회할 수 없습니다."); _cache[symbol] = new(generation, start, end, now.AddSeconds(2), response); return response;
        }
        finally { _inflight.TryRemove(symbol, out _); }
    }
    static LiquidityResponse Empty(string symbol, string status, DateTimeOffset now, string message) => new(symbol, status, null, null, now, "Toss Securities orderbook", "표시 호가 잔량 추정치", message);
}
