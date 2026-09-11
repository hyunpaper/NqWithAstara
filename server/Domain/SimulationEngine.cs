namespace Astra.Server.Domain;

public sealed record SimulationEntry(
    string Kind, double EntryPrice, double Target, double Stop,
    string? TargetBasis, string? StopBasis, int Score, double ExtSigma,
    double RelativeVolume, double? BuyShare, double Rsi, string[] Reasons,
    DateTimeOffset EnteredAt, DateTimeOffset? SessionEnd, DateTimeOffset TriggerBarAt);

public static class SimulationEngine
{
    public const string LogicVersion = "v4";

    public static List<SimTrade> Process(
        IReadOnlyList<SimTrade> source, string symbol, IReadOnlyList<Candle> completedBars,
        double quotePrice, DateTimeOffset quoteAt, int score, double vwap,
        IReadOnlyList<SimulationEntry> entries)
    {
        var trades = ReplayBars(source, symbol, completedBars);
        for (var i = 0; i < trades.Count; i++)
        {
            var trade = trades[i];
            if (!trade.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) || trade.Status != "OPEN") continue;
            if (trade.Status == "OPEN" && quoteAt >= trade.EnteredAt && quoteAt < SessionEndOf(trade))
            {
                if (quotePrice <= trade.Stop)
                    trade = Close(trade, "STOP", quotePrice, quoteAt, quotePrice, false);
                else if (quotePrice >= trade.Target)
                    trade = Close(trade, "TARGET", trade.Target, quoteAt, quotePrice, false);
                // v4 전용 조기 청산(score<40 CUT). v5 구조 거래에는 적용하지 않는다 — v5는 동결된 구조
                // Stop/Target과 EOD만으로 관리한다(설계 §10 "v5에 v4 score<40 CUT을 적용하지 않는다").
                else if (trade.Kind != "REBOUND" && !StructuralSimulation.OwnsTrade(trade) && score < 40 && completedBars.Count > 0 && completedBars[^1].Close < vwap)
                    trade = Close(trade, "CUT", quotePrice, quoteAt, quotePrice, false);
                else trade = trade with { LastPrice = quotePrice, LastPriceAt = quoteAt };
            }
            trades[i] = trade;
        }

        foreach (var entry in entries)
        {
            if (trades.Any(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && x.Status == "OPEN")) break;
            if (trades.Any(x => x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                                x.Kind == entry.Kind && x.TriggerBarAt == entry.TriggerBarAt)) continue;
            if (entry.Target <= entry.EntryPrice || entry.Stop >= entry.EntryPrice) continue;
            trades.Add(new(Guid.NewGuid().ToString("N")[..8], symbol, entry.Kind, entry.EnteredAt,
                entry.EntryPrice, entry.Target, entry.Stop, entry.TargetBasis, entry.StopBasis,
                "OPEN", null, null, null, entry.EntryPrice, entry.Score, entry.ExtSigma,
                entry.RelativeVolume, entry.BuyShare, entry.Rsi, entry.Reasons, LogicVersion,
                null, entry.SessionEnd, null, entry.EnteredAt, entry.TriggerBarAt));
        }

        // OPEN records are operational state and must never be evicted. Retain the newest
        // closed records up to the remaining capacity.
        if (trades.Count > 500)
        {
            var open = trades.Where(x => x.Status == "OPEN").ToList();
            var closed = trades.Where(x => x.Status != "OPEN").TakeLast(Math.Max(0, 500 - open.Count));
            trades = [.. open, .. closed];
        }
        return trades;
    }

    public static List<SimTrade> ReplayBars(IReadOnlyList<SimTrade> source, string symbol, IReadOnlyList<Candle> completedBars)
    {
        var trades = source.ToList();
        for (var i = 0; i < trades.Count; i++)
        {
            var trade = trades[i];
            if (!trade.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) || trade.Status != "OPEN") continue;
            foreach (var bar in completedBars.Where(x => x.Timestamp >= trade.EnteredAt &&
                         x.Timestamp < SessionEndOf(trade) &&
                         (!trade.LastEvaluatedBarAt.HasValue || x.Timestamp > trade.LastEvaluatedBarAt.Value)).OrderBy(x => x.Timestamp))
            {
                trade = EvaluateBar(trade, bar);
                if (trade.Status != "OPEN") break;
            }
            trades[i] = trade;
        }
        return trades;
    }

    public static List<SimTrade> CloseExpiredSessions(IReadOnlyList<SimTrade> source, DateTimeOffset now)
    {
        return source.Select(t =>
        {
            if (t.Status != "OPEN") return t;
            var inferredEnd = SessionEndOf(t);
            if (inferredEnd > now) return t;
            var exitAt = inferredEnd;
            return Close(t, "EOD", t.LastPrice, exitAt, t.LastPrice, true);
        }).ToList();
    }

    public static DateTimeOffset SessionEndOf(SimTrade trade) => trade.SessionEnd ?? InferLegacySessionEnd(trade.EnteredAt);
    public static bool IsExpired(SimTrade trade, DateTimeOffset now) => trade.Status == "OPEN" && SessionEndOf(trade) <= now;

    static DateTimeOffset InferLegacySessionEnd(DateTimeOffset enteredAt)
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var local = TimeZoneInfo.ConvertTime(enteredAt, eastern);
        var date = local.TimeOfDay >= TimeSpan.FromHours(16) ? local.Date.AddDays(1) : local.Date;
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        var close = new DateTime(date.Year, date.Month, date.Day, 16, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(close, eastern.GetUtcOffset(close));
    }

    static SimTrade EvaluateBar(SimTrade trade, Candle bar)
    {
        // A stop gap fills at the opening print; this avoids the optimistic assumption
        // that a sell order could execute at a stop already skipped by the market.
        if (bar.Open <= trade.Stop)
            return Close(trade, "STOP", bar.Open, bar.Timestamp, bar.Close, false) with { LastEvaluatedBarAt = bar.Timestamp };
        // A gap-up open already at/beyond target has the target order filled before the bar's
        // own low can be checked against stop — the target print came first. Fill conservatively
        // at Target rather than the more favorable Open (mirrors the stop-gap treatment above).
        if (bar.Open >= trade.Target)
            return Close(trade, "TARGET", trade.Target, bar.Timestamp, bar.Close, false) with { LastEvaluatedBarAt = bar.Timestamp };
        if (bar.Low <= trade.Stop)
            return Close(trade, "STOP", trade.Stop, bar.Timestamp, bar.Close, false) with { LastEvaluatedBarAt = bar.Timestamp };
        // When both levels occur within one minute and order is unknowable, stop-first is conservative.
        if (bar.High >= trade.Target)
            return Close(trade, "TARGET", trade.Target, bar.Timestamp, bar.Close, false) with { LastEvaluatedBarAt = bar.Timestamp };
        return trade with { LastPrice = bar.Close, LastPriceAt = bar.Timestamp, LastEvaluatedBarAt = bar.Timestamp };
    }

    static SimTrade Close(SimTrade trade, string status, double exitPrice, DateTimeOffset exitAt, double lastPrice, bool estimated)
        => trade with
        {
            Status = status, ExitPrice = exitPrice, ExitAt = exitAt,
            PnlPercent = Math.Round((exitPrice / trade.EntryPrice - 1) * 100 - MarketRules.RoundTripFeePercent, 2),
            LastPrice = lastPrice, LastPriceAt = estimated ? trade.LastPriceAt : exitAt, ExitEstimated = estimated
        };
}
