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
    MonitorRuntimeState runtime,
    FeeRateCheckService? feeCheck = null)
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

    /// <summary>관심종목 표시 순서만 교체한다. 집합이 정확히 일치해야 저장한다 (#224).</summary>
    public async Task<WatchlistChangeResult> ReorderAsync(IReadOnlyList<string>? symbols, CancellationToken requestCt)
    {
        if (symbols is null || symbols.Count == 0) return new(WatchlistChangeStatus.Invalid, "관심종목 순서 목록이 비었습니다.");
        var ordered = symbols.Select(x => x?.Trim().ToUpperInvariant() ?? string.Empty).ToList();
        if (ordered.Any(x => !SymbolPattern.IsMatch(x))) return new(WatchlistChangeStatus.Invalid, "심볼 형식이 올바르지 않습니다.");
        if (ordered.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Count) return new(WatchlistChangeStatus.Invalid, "중복된 심볼이 있습니다.");

        using (await runtime.EnterControlAsync(requestCt))
        {
            var applied = await store.Update("watchlist.json", new List<WatchItem>(), items =>
            {
                if (items.Count != ordered.Count) return (items, false);
                var bySymbol = new Dictionary<string, WatchItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items) bySymbol[item.Symbol] = item;
                if (bySymbol.Count != ordered.Count || !ordered.All(bySymbol.ContainsKey)) return (items, false);
                return (ordered.Select(x => bySymbol[x]).ToList(), true);
            });
            if (!applied) return new(WatchlistChangeStatus.Invalid, "관심종목 목록과 순서가 일치하지 않습니다.");
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
        // 이슈 #130: 기동 시 1회 실계좌 US 왕복 수수료와 정책 값 정합을 확인한다. 실패해도 진입을 막지 않는다.
        if (feeCheck is not null) await feeCheck.CheckOnMonitorStartAsync(requestCt);
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
