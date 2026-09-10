using System.Text.RegularExpressions;

namespace Astra.Server.Application;

public enum WatchlistChangeStatus { Ok, Invalid, NotFound, LimitReached }
public sealed record WatchlistChangeResult(WatchlistChangeStatus Status, string? Message = null);

/// <summary>Serialized application use cases for watchlist and monitoring lifecycle changes.</summary>
public sealed class MonitorControlService(
    ILocalStore store,
    IMarketDataGateway market,
    IRealtimeMarketStream stream,
    IMonitorSignals signals,
    MonitorRuntimeState runtime)
{
    static readonly Regex SymbolPattern = new("^[A-Z0-9.-]{1,12}$", RegexOptions.CultureInvariant);

    public async Task<WatchlistChangeResult> AddAsync(WatchItem? input, CancellationToken requestCt)
    {
        var symbol = input?.Symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(symbol) || !SymbolPattern.IsMatch(symbol)) return new(WatchlistChangeStatus.Invalid);

        var verified = (await market.Stocks(symbol, requestCt)).FirstOrDefault(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (verified is null) return new(WatchlistChangeStatus.NotFound);

        using (await runtime.EnterControlAsync(requestCt))
        {
            var result = await store.Update("watchlist.json", new List<WatchItem>(), items =>
            {
                if (items.Any(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return (items, (Ok: true, Changed: false));
                if (items.Count >= 30) return (items, (Ok: false, Changed: false));
                items.Add(verified);
                return (items, (Ok: true, Changed: true));
            });
            if (!result.Ok) return new(WatchlistChangeStatus.LimitReached, "관심종목은 최대 30개입니다.");
            if (result.Changed && runtime.Snapshot().Running) { runtime.CommitWatchlistChange(); await ApplyLatestSubscriptionsAsync(); }
        }
        return new(WatchlistChangeStatus.Ok);
    }

    public async Task RemoveAsync(string symbol, CancellationToken requestCt)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        using (await runtime.EnterControlAsync(requestCt))
        {
            var removed = await store.Update("watchlist.json", new List<WatchItem>(), items =>
                (items, items.RemoveAll(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)) > 0));
            if (!removed) return;
            signals.Remove(symbol);
            if (runtime.Snapshot().Running) { runtime.CommitWatchlistChange(); await ApplyLatestSubscriptionsAsync(); }
        }
    }

    public async Task StartAsync(CancellationToken requestCt)
    {
        using (await runtime.EnterControlAsync(requestCt))
        {
            runtime.CommitStart();
            await ApplyLatestSubscriptionsAsync();
        }
    }

    public async Task StopAsync(CancellationToken requestCt)
    {
        using (await runtime.EnterControlAsync(requestCt))
        {
            runtime.CommitStop();
            signals.Clear();
            // State is committed. Finish transport reconciliation even if the HTTP client disconnects.
            await stream.StopAsync(CancellationToken.None);
        }
    }

    async Task ApplyLatestSubscriptionsAsync()
    {
        var latest = await store.Read("watchlist.json", new List<WatchItem>());
        var symbols = latest.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (symbols.Length == 0) await stream.StopAsync(CancellationToken.None);
        else await stream.StartAsync(symbols, market.GetAccessTokenAsync, true, CancellationToken.None);
    }
}
