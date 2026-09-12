using System.Collections.Concurrent;

namespace Astra.Server.Application;

/// <summary>
/// #132 종목 메타데이터(`/stocks`) 세션 캐시. 관심종목이 늘어날 때 신규 심볼만 배치 1회 조회한다
/// (STOCK 그룹 5/s, 스펙 권고 "짧은 주기 폴링 금지"). 조회 실패는 결측으로 두고 다음 poll에 다시 시도한다.
/// </summary>
public sealed class SymbolMetadataService(IMarketDataGateway marketData, IMonitorDiagnostics diagnostics)
{
    readonly ConcurrentDictionary<string, StockInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>응답에 없던 심볼까지 포함한 "조회를 끝낸" 집합. 실패한 심볼은 들어가지 않는다.</summary>
    readonly ConcurrentDictionary<string, byte> _resolved = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _gate = new(1, 1);

    public StockInfo? Get(string symbol) => _cache.TryGetValue(symbol, out var info) ? info : null;

    public async Task EnsureAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (Pending(symbols).Length == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            var pending = Pending(symbols);
            if (pending.Length == 0) return;
            foreach (var info in await marketData.StockInfos(string.Join(',', pending), ct)) _cache[info.Symbol] = info;
            foreach (var symbol in pending) _resolved[symbol] = 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { diagnostics.MarketDataFailed(string.Join(',', Pending(symbols)), "symbol-meta", ex); }
        finally { _gate.Release(); }
    }

    string[] Pending(IEnumerable<string> symbols) => symbols
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim().ToUpperInvariant())
        .Where(x => !_resolved.ContainsKey(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    public void Remove(string symbol) { _cache.TryRemove(symbol, out _); _resolved.TryRemove(symbol, out _); }

    public void Clear() { _cache.Clear(); _resolved.Clear(); }
}
