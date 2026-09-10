namespace Astra.Server.Application;

public sealed class CatalogQueryService(IMarketDataGateway market)
{
    public async Task<IReadOnlyList<WatchItem>> SearchAsync(string query, CancellationToken ct)
    {
        var symbols = query.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(x => System.Text.RegularExpressions.Regex.IsMatch(x.ToUpperInvariant(), "^[A-Z0-9.-]{1,12}$") ? [x.ToUpperInvariant()] : SymbolAliases.Resolve(x))
            .Distinct(StringComparer.Ordinal).Take(20).ToArray();
        return symbols.Length == 0 ? [] : await market.Stocks(string.Join(',', symbols), ct);
    }
}

public enum PositionChangeStatus { Ok, Invalid, NotFound }
public sealed record PositionChangeResult(PositionChangeStatus Status, Position? Position = null);

public sealed class PositionService(ILocalStore store, MonitorRuntimeState runtime)
{
    public async Task<PositionChangeResult> PutAsync(string inputSymbol, PositionInput input, CancellationToken ct)
    {
        var symbol = inputSymbol.Trim().ToUpperInvariant();
        if (!double.IsFinite(input.EntryPrice) || !double.IsFinite(input.Quantity) || input.EntryPrice <= .01 || input.Quantity <= 0) return new(PositionChangeStatus.Invalid);
        using (await runtime.EnterControlAsync(ct))
        {
            var watch = await store.Read("watchlist.json", new List<WatchItem>());
            if (watch.All(x => !x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return new(PositionChangeStatus.NotFound);
            var position = MarketRules.Enter(input.EntryPrice, input.Quantity, null);
            await store.Update("positions.json", new Dictionary<string, Position>(), values => { values[symbol] = position; return (values, true); });
            return new(PositionChangeStatus.Ok, position);
        }
    }

    public async Task RemoveAsync(string inputSymbol, CancellationToken ct)
    {
        using (await runtime.EnterControlAsync(ct))
            await store.Update("positions.json", new Dictionary<string, Position>(), values => { values.Remove(inputSymbol.Trim().ToUpperInvariant()); return (values, true); });
    }
}
