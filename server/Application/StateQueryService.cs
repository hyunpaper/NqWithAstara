namespace Astra.Server.Application;

public sealed class StateQueryService(ILocalStore store, IMonitorSignals signals, IRealtimeMarketStream stream, MonitorRuntimeState runtime, TimeProvider clock, StructureAnalysisService? structure = null)
{
    public async Task<object> GetAsync()
    {
        var watch = await store.Read("watchlist.json", new List<WatchItem>()); var positions = await store.Read("positions.json", new Dictionary<string, Position>()); var now = clock.GetLocalNow(); var state = runtime.Snapshot();
        var start = state.Market.Start.GetValueOrDefault(); var end = state.Market.End.GetValueOrDefault(); var open = state.Market.Start.HasValue && state.Market.End.HasValue && MarketRules.IsOpen(now, start, end);
        var rows = watch.Select(item =>
        {
            positions.TryGetValue(item.Symbol, out var current);
            if (state.Running && open && signals.TryGet(item.Symbol, out var live) && !live.Stale && live.UpdatedAt >= start && live.UpdatedAt < end && live.UpdatedAt <= now.AddSeconds(5) && now - live.UpdatedAt <= TimeSpan.FromMinutes(3))
            {
                var price = live.Price;
                if (stream.TryGetLatest(item.Symbol, out var tick) && tick.Currency == "USD" && tick.Timestamp >= live.UpdatedAt && tick.Timestamp >= start && tick.Timestamp < end && tick.Timestamp <= now.AddSeconds(5) && now - tick.Timestamp <= TimeSpan.FromMinutes(3)) price = (double)tick.Price;
                var basePrice = live.Price / (1 + live.ChangePercent / 100d);
                object? position = current is null ? null : new { current.EntryPrice, current.Quantity, current.Target, current.Stop, current.TargetBasis, current.StopBasis, pnlPercent = Math.Round((price / current.EntryPrice - 1) * 100 - MarketRules.RoundTripFeePercent, 2), status = current.Stop.HasValue && price <= current.Stop ? "STOP" : current.Target.HasValue && price >= current.Target ? "TARGET" : "HOLD" };
                return (live.Score, (object)(live with { Price = price, ChangePercent = basePrice > 0 ? Math.Round((price / basePrice - 1) * 100, 2) : live.ChangePercent, Position = position }));
            }
            return (0, (object)new { item.Symbol, item.Name, price = (double?)null, changePercent = (double?)null, score = 0, action = "WAIT", reasons = new[] { open ? "데이터 대기 중" : "정규장 외" }, updatedAt = (DateTimeOffset?)null, stale = true, indicators = (object?)null, bars = Array.Empty<object>(), position = current, setup = (string?)null, setupAt = (DateTimeOffset?)null, breakout = (string?)null, breakoutAt = (DateTimeOffset?)null });
        }).OrderByDescending(x => x.Item1).Select(x => x.Item2).ToArray();
        var websocket = stream.Status == "connected";
        // 설계 §12: additive `structureSummary`만 붙이고 기존 score/action(=v4)의 의미는 덮어쓰지 않는다.
        var structureSummary = structure?.Summary(watch.Select(x => x.Symbol));
        return new { running = state.Running, mode = "live", connection = new { status = state.ConnectionStatus, message = state.ConnectionMessage, guideUrl = state.ConnectionMessage.Contains("허용 IP") ? "https://developers.tossinvest.com/docs" : null }, transport = new { mode = websocket ? "websocket" : "polling", status = stream.Status, message = websocket ? stream.Message : "REST 폴링 사용 중 · " + stream.Message, lastTickAt = stream.LastTickAt }, market = new { isOpen = open, label = open ? "정규장" : state.Market.Label, nextOpen = state.Market.NextOpen }, updatedAt = state.UpdatedAt, watchlist = watch, signals = rows, events = Array.Empty<object>(), structureSummary };
    }
}
