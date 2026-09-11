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
                // Quote timestamps are an ordered observation stream. A delayed duplicate must not
                // change an already observed price or retroactively claim an earlier barrier hit.
                if (!CanApplyQuote(trade, quoteAt)) { trades[i] = trade; continue; }
                if (quotePrice <= trade.Stop)
                    trade = Close(trade, "STOP", quotePrice, quoteAt, quotePrice, false, "SAMPLED_QUOTE", "QUOTE_STOP");
                else if (quotePrice >= trade.Target)
                    trade = Close(trade, "TARGET", trade.Target, quoteAt, quotePrice, false, "SAMPLED_QUOTE", "QUOTE_TARGET");
                // v4 전용 조기 청산(score<40 CUT). v5 구조 거래에는 적용하지 않는다 — v5는 동결된 구조
                // Stop/Target과 EOD만으로 관리한다(설계 §10 "v5에 v4 score<40 CUT을 적용하지 않는다").
                else if (trade.Kind != "REBOUND" && !StructuralSimulation.OwnsTrade(trade) && score < 40 && completedBars.Count > 0 && completedBars[^1].Close < vwap)
                    trade = Close(trade, "CUT", quotePrice, quoteAt, quotePrice, false, "SAMPLED_QUOTE", "QUOTE_CUT");
                else trade = ObserveQuote(trade, quotePrice, quoteAt);
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
                null, entry.SessionEnd, null, entry.EnteredAt, entry.TriggerBarAt, null,
                EntryProvenance(entry.EnteredAt)));
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
            return Close(t, "EOD", t.LastPrice, exitAt, t.LastPrice, true, "EOD_LAST_PRICE_FALLBACK", "EOD_LAST_PRICE");
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
            return Close(trade, "STOP", bar.Open, bar.Timestamp, bar.Close, false, "GAP_OPEN", "GAP_STOP", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        // A gap-up open already at/beyond target has the target order filled before the bar's
        // own low can be checked against stop — the target print came first. Fill conservatively
        // at Target rather than the more favorable Open (mirrors the stop-gap treatment above).
        if (bar.Open >= trade.Target)
            return Close(trade, "TARGET", trade.Target, bar.Timestamp, bar.Close, false, "GAP_OPEN", "GAP_TARGET", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        if (bar.Low <= trade.Stop)
            return Close(trade, "STOP", trade.Stop, bar.Timestamp, bar.Close, false, "COMPLETED_BAR_REPLAY",
                bar.High >= trade.Target ? "SAME_BAR_STOP_FIRST" : "BAR_STOP", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        // When both levels occur within one minute and order is unknowable, stop-first is conservative.
        if (bar.High >= trade.Target)
            return Close(trade, "TARGET", trade.Target, bar.Timestamp, bar.Close, false, "COMPLETED_BAR_REPLAY", "BAR_TARGET_AFTER_STOP_CHECK", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        return trade with { LastPrice = bar.Close, LastPriceAt = BarCloseAt(bar), LastEvaluatedBarAt = bar.Timestamp,
            Execution = WithBarEvidence(trade.Execution, bar) };
    }

    static SimTrade ObserveQuote(SimTrade trade, double quotePrice, DateTimeOffset quoteAt)
    {
        var execution = trade.Execution;
        if (execution is not null && SameMinute(quoteAt, execution.EntryBarStart) && quoteAt >= trade.EnteredAt &&
            (!execution.EntryMinuteEvidenceAt.HasValue || quoteAt >= execution.EntryMinuteEvidenceAt.Value))
            execution = execution with { EntryMinuteCoverage = "PARTIALLY_OBSERVED_QUOTE", EntryMinuteEvidenceAt = quoteAt, LastQuoteAt = quoteAt };
        else if (execution is not null) execution = execution with { LastQuoteAt = quoteAt };
        return trade with { LastPrice = quotePrice, LastPriceAt = quoteAt, Execution = execution };
    }

    static SimTrade Close(SimTrade trade, string status, double exitPrice, DateTimeOffset exitAt, double lastPrice, bool estimated,
        string source, string decision, Candle? bar = null)
        => trade with
        {
            Status = status, ExitPrice = exitPrice, ExitAt = exitAt,
            PnlPercent = Math.Round((exitPrice / trade.EntryPrice - 1) * 100 - MarketRules.RoundTripFeePercent, 2),
            LastPrice = lastPrice, LastPriceAt = estimated ? trade.LastPriceAt : bar is null ? exitAt : BarCloseAt(bar), ExitEstimated = estimated,
            Execution = WithExitEvidence(trade.Execution, source, estimated ? trade.LastPriceAt : bar is null ? exitAt : BarCloseAt(bar), decision, bar)
        };

    static ExecutionProvenance EntryProvenance(DateTimeOffset enteredAt)
    {
        var start = new DateTimeOffset(enteredAt.Year, enteredAt.Month, enteredAt.Day, enteredAt.Hour, enteredAt.Minute, 0, enteredAt.Offset);
        return new(start, start.AddMinutes(1), "UNOBSERVED", null);
    }

    static ExecutionProvenance? WithExitEvidence(ExecutionProvenance? execution, string source, DateTimeOffset? evidenceAt,
        string decision, Candle? bar)
    {
        if (execution is null) return null;
        var entryObserved = source == "SAMPLED_QUOTE" && evidenceAt is { } at && SameMinute(at, execution.EntryBarStart) && at >= execution.EntryBarStart;
        return execution with
        {
            EntryMinuteCoverage = entryObserved ? "PARTIALLY_OBSERVED_QUOTE" : execution.EntryMinuteCoverage,
            EntryMinuteEvidenceAt = entryObserved ? evidenceAt : execution.EntryMinuteEvidenceAt,
            ExitSource = source, ExitEvidenceAt = evidenceAt, EvaluatedBarStart = bar?.Timestamp,
            EvaluatedBarCloseAt = bar is null ? null : BarCloseAt(bar), BarrierDecision = decision,
            EvaluatedBarOpen = bar?.Open, EvaluatedBarHigh = bar?.High,
            EvaluatedBarLow = bar?.Low, EvaluatedBarClose = bar?.Close,
            LastQuoteAt = source == "SAMPLED_QUOTE" ? evidenceAt : execution.LastQuoteAt
        };
    }

    static ExecutionProvenance? WithBarEvidence(ExecutionProvenance? execution, Candle bar) => execution is null ? null : execution with
    {
        EvaluatedBarStart = bar.Timestamp, EvaluatedBarCloseAt = BarCloseAt(bar),
        EvaluatedBarOpen = bar.Open, EvaluatedBarHigh = bar.High,
        EvaluatedBarLow = bar.Low, EvaluatedBarClose = bar.Close
    };

    static DateTimeOffset BarCloseAt(Candle bar) => bar.Timestamp.AddMinutes(1);
    static bool CanApplyQuote(SimTrade trade, DateTimeOffset quoteAt) => trade.Execution?.LastQuoteAt is not { } lastQuoteAt || quoteAt > lastQuoteAt;
    static bool SameMinute(DateTimeOffset left, DateTimeOffset right) => left.Year == right.Year && left.Month == right.Month &&
        left.Day == right.Day && left.Hour == right.Hour && left.Minute == right.Minute && left.Offset == right.Offset;
}
