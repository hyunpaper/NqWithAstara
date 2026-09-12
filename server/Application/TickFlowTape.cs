using System.Collections.Concurrent;

namespace Astra.Server.Application;

/// <summary>
/// 틱 룰(직전 체결가 대비 상승=매수)로 체결강도를 추정하는 체결 테이프.
/// 웹소켓 틱과 REST 체결 내역을 같은 규칙으로 누적하며, 같은 (시각·가격·수량) 체결은 한 번만 센다.
/// Toss는 체결 방향을 주지 않으므로 모두 로컬 추정치다.
/// </summary>
public sealed class TickFlowTape(TimeProvider clock)
{
    /// <summary>웹소켓 틱이 이 시간 이상 없으면 REST 체결 내역으로 보정한다.</summary>
    public static readonly TimeSpan TradeTapeFallbackAfter = TimeSpan.FromSeconds(60);
    /// <summary>대량체결 판정 배수(§16A 미검증 상수) — 최근 표본 중앙값의 k배 초과 수량을 블록 체결로 본다.</summary>
    public const int BlockTradeVolumeFactor = 10;
    public const int BlockTradeSampleSize = 50;
    const int MaxTrades = 5000;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    readonly record struct Entry(DateTimeOffset At, decimal Price, decimal Volume, decimal Buy, decimal Sell);

    sealed class SymbolTape
    {
        public readonly object Gate = new();
        public readonly Queue<Entry> Trades = new();
        public readonly HashSet<(DateTimeOffset At, decimal Price, decimal Volume)> Seen = [];
        public decimal LastPrice;
        public int LastDirection;
        public DateTimeOffset LastAt;
        public DateTimeOffset? LastWsAt;
        public DateTimeOffset? LastRestAt;
    }

    readonly ConcurrentDictionary<string, SymbolTape> _flows = new(StringComparer.OrdinalIgnoreCase);

    public void RecordTick(TossTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        var flow = _flows.GetOrAdd(trade.Symbol, _ => new SymbolTape());
        lock (flow.Gate)
        {
            Append(flow, trade.Timestamp, trade.Price, trade.Volume);
            flow.LastWsAt = trade.Timestamp;
        }
    }

    /// <summary>REST 체결 내역을 같은 틱 룰로 병합한다. 반환값은 새로 반영된 체결 수다.</summary>
    public int MergeRestTrades(string symbol, IReadOnlyList<TossTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        var flow = _flows.GetOrAdd(symbol, _ => new SymbolTape());
        var merged = 0;
        lock (flow.Gate)
        {
            foreach (var trade in trades.OrderBy(x => x.Timestamp))
                if (Append(flow, trade.Timestamp, trade.Price, trade.Volume)) merged++;
            flow.LastRestAt = clock.GetUtcNow();
        }
        return merged;
    }

    public DateTimeOffset? LastWsTickAt(string symbol)
    {
        if (!_flows.TryGetValue(symbol, out var flow)) return null;
        lock (flow.Gate) return flow.LastWsAt;
    }

    /// <summary>ws 틱이 <see cref="TradeTapeFallbackAfter"/> 이상 끊겼는지. 틱이 한 번도 없으면 끊긴 것으로 본다.</summary>
    public bool IsWebSocketStale(string symbol)
    {
        var last = LastWsTickAt(symbol);
        return last is null || clock.GetUtcNow() - last.Value >= TradeTapeFallbackAfter;
    }

    /// <summary>체결강도 원천 — "ws"(신선한 틱) · "rest"(체결 내역 보정) · "none"(표본 없음).</summary>
    public string FlowSource(string symbol)
    {
        if (!_flows.TryGetValue(symbol, out var flow)) return "none";
        lock (flow.Gate)
        {
            if (flow.Trades.Count == 0) return "none";
            if (flow.LastWsAt is { } ws && clock.GetUtcNow() - ws < TradeTapeFallbackAfter) return "ws";
            return flow.LastRestAt is not null ? "rest" : "ws";
        }
    }

    /// <summary>최근 표본에서 중앙값의 k배를 넘는 체결 수. 표본이 없거나 중앙값이 0이면 null이다.</summary>
    public int? BlockTradeCount(string symbol, int sample = BlockTradeSampleSize)
    {
        if (sample <= 0 || !_flows.TryGetValue(symbol, out var flow)) return null;
        decimal[] volumes;
        lock (flow.Gate)
        {
            if (flow.Trades.Count == 0) return null;
            volumes = [.. flow.Trades.Skip(Math.Max(0, flow.Trades.Count - sample)).Select(x => x.Volume)];
        }
        var median = Median(volumes);
        if (median <= 0) return null;
        var threshold = BlockTradeVolumeFactor * median;
        return volumes.Count(x => x > threshold);
    }

    public (decimal Buy, decimal Sell)? Flow(string symbol, DateTimeOffset from, DateTimeOffset to)
    {
        if (!_flows.TryGetValue(symbol, out var flow)) return null;
        lock (flow.Gate)
        {
            decimal buy = 0, sell = 0; var any = false;
            foreach (var t in flow.Trades) { if (t.At < from || t.At >= to) continue; any = true; buy += t.Buy; sell += t.Sell; }
            return any ? (buy, sell) : null;
        }
    }

    public void Clear() => _flows.Clear();

    /// <summary>재구독 시 계속 볼 종목의 테이프만 남긴다.</summary>
    public void Retain(IEnumerable<string> symbols)
    {
        var keep = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _flows.Keys) if (!keep.Contains(key)) _flows.TryRemove(key, out _);
    }

    public void Remove(string symbol) => _flows.TryRemove(symbol, out _);

    bool Append(SymbolTape flow, DateTimeOffset at, decimal price, decimal volume)
    {
        if (price <= 0 || volume < 0 || at < flow.LastAt) return false;
        if (!flow.Seen.Add((at, price, volume))) return false;
        var direction = flow.LastPrice == 0 ? 0 : price > flow.LastPrice ? 1 : price < flow.LastPrice ? -1 : flow.LastDirection;
        flow.LastPrice = price; flow.LastDirection = direction; flow.LastAt = at;
        flow.Trades.Enqueue(new(at, price, volume, direction > 0 ? volume : 0, direction < 0 ? volume : 0));
        var cutoff = clock.GetUtcNow() - Window;
        while (flow.Trades.Count > MaxTrades || (flow.Trades.Count > 0 && flow.Trades.Peek().At < cutoff))
        {
            var oldest = flow.Trades.Dequeue();
            flow.Seen.Remove((oldest.At, oldest.Price, oldest.Volume));
        }
        return true;
    }

    static decimal Median(decimal[] values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
