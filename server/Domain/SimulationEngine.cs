namespace Astra.Server.Domain;

public sealed record SimulationEntry(
    string Kind, double EntryPrice, double Target, double Stop,
    string? TargetBasis, string? StopBasis, int Score, double ExtSigma,
    double RelativeVolume, double? BuyShare, double Rsi, string[] Reasons,
    DateTimeOffset EnteredAt, DateTimeOffset? SessionEnd, DateTimeOffset TriggerBarAt,
    TradeSide Side = TradeSide.Long);

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
                if (IsStopHit(trade.Side, quotePrice, trade.Stop))
                    trade = Close(trade, "STOP", quotePrice, quoteAt, quotePrice, false, "SAMPLED_QUOTE", "QUOTE_STOP");
                else if (!TargetRemoved(trade) && IsTargetHit(trade.Side, quotePrice, trade.Target))
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
            if (entry.Side == TradeSide.Long && (entry.Target <= entry.EntryPrice || entry.Stop >= entry.EntryPrice) ||
                entry.Side == TradeSide.Short && (entry.Target >= entry.EntryPrice || entry.Stop <= entry.EntryPrice)) continue;
            trades.Add(new(Guid.NewGuid().ToString("N")[..8], symbol, entry.Kind, entry.EnteredAt,
                entry.EntryPrice, entry.Target, entry.Stop, entry.TargetBasis, entry.StopBasis,
                "OPEN", null, null, null, entry.EntryPrice, entry.Score, entry.ExtSigma,
                entry.RelativeVolume, entry.BuyShare, entry.Rsi, entry.Reasons, LogicVersion,
                null, entry.SessionEnd, null, entry.EnteredAt, entry.TriggerBarAt, null,
                EntryProvenance(entry.EnteredAt), entry.Side));
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
        // §9.2 목표 해제(#245 H-B3-3): StructuralTargetExtensionR==0이면 목표 판정을 전부 건너뛰고 트레일·손절·EOD로만 닫는다.
        var targetActive = !TargetRemoved(trade);
        // A stop gap fills at the opening print; this avoids the optimistic assumption
        // that a sell order could execute at a stop already skipped by the market.
        if (IsStopHit(trade.Side, bar.Open, trade.Stop))
            return Close(trade, "STOP", bar.Open, bar.Timestamp, bar.Close, false, "GAP_OPEN", "GAP_STOP", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        // A gap-up open already at/beyond target has the target order filled before the bar's
        // own low can be checked against stop — the target print came first. Fill conservatively
        // at Target rather than the more favorable Open (mirrors the stop-gap treatment above).
        if (targetActive && IsTargetHit(trade.Side, bar.Open, trade.Target))
            return Close(trade, "TARGET", trade.Target, bar.Timestamp, bar.Close, false, "GAP_OPEN", "GAP_TARGET", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        if (IsStopHit(trade.Side, bar.Low, trade.Stop) || IsStopHit(trade.Side, bar.High, trade.Stop))
            return Close(trade, "STOP", trade.Stop, bar.Timestamp, bar.Close, false, "COMPLETED_BAR_REPLAY",
                targetActive && (IsTargetHit(trade.Side, bar.High, trade.Target) || IsTargetHit(trade.Side, bar.Low, trade.Target))
                    ? "SAME_BAR_STOP_FIRST" : "BAR_STOP", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        // When both levels occur within one minute and order is unknowable, stop-first is conservative.
        if (targetActive && (IsTargetHit(trade.Side, bar.High, trade.Target) || IsTargetHit(trade.Side, bar.Low, trade.Target)))
            return Close(trade, "TARGET", trade.Target, bar.Timestamp, bar.Close, false, "COMPLETED_BAR_REPLAY", "BAR_TARGET_AFTER_STOP_CHECK", bar) with { LastEvaluatedBarAt = bar.Timestamp };
        var observed = trade with { LastPrice = bar.Close, LastPriceAt = BarCloseAt(bar), LastEvaluatedBarAt = bar.Timestamp,
            Execution = WithBarEvidence(trade.Execution, bar) };
        return ArmTrailingStop(ArmTwoRFeeBreakEvenStop(observed, bar.Close), bar);
    }

    /// <summary>§9.2 목표 해제 여부(#245 H-B3-3). 동결 계약의 StructuralTargetExtensionR가 0이면 목표를 쓰지 않는다.</summary>
    static bool TargetRemoved(SimTrade trade) => trade.Structure?.StructuralTargetExtensionR == 0;

    static SimTrade ArmTwoRFeeBreakEvenStop(SimTrade trade, double completedClose)
    {
        var context = trade.Structure;
        // §9.2 트레일 꼬리표가 붙어도 BE 판정은 기본 청산 버전으로 한다(#245 H-B3-3). 둘은 함께 작동한다.
        var baseVersion = context?.StructuralExitPolicyVersion is { } version ? StructuralSimulation.BaseExitVersion(version) : null;
        if (baseVersion is not (StructuralSimulation.TwoRFeeBreakEvenExitPolicyVersion or
            StructuralSimulation.TwoRTargetAndFeeBreakEvenExitPolicyVersion or
            StructuralSimulation.HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion or
            StructuralSimulation.HalfRQualifiedTransitionFeeBreakEvenExitPolicyVersion))
            return trade;
        var plan = context!.PlanSnapshot;
        var originalStop = (double)plan.Stop;
        var risk = Math.Abs(trade.EntryPrice - originalStop);
        if (!(risk > 0) || !double.IsFinite(risk)) return trade;
        var triggerR = baseVersion is StructuralSimulation.HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion or
            StructuralSimulation.HalfRQualifiedTransitionFeeBreakEvenExitPolicyVersion ? .5 : 2;
        var reachedTrigger = trade.Side == TradeSide.Long
            ? completedClose >= trade.EntryPrice + triggerR * risk
            : completedClose <= trade.EntryPrice - triggerR * risk;
        if (!reachedTrigger) return trade;
        var costs = (double)(plan.FeePerShare + plan.ExtraCostPerShare + (plan.BorrowCostPerShare ?? 0));
        var feeBreakEven = trade.Side == TradeSide.Long
            ? trade.EntryPrice + costs : trade.EntryPrice - costs;
        if (trade.Side == TradeSide.Long && feeBreakEven <= trade.Stop ||
            trade.Side == TradeSide.Short && feeBreakEven >= trade.Stop) return trade;
        return trade with { Stop = feeBreakEven, StopBasis = $"{triggerR:0.##}R 완료봉 이후 비용 회수 손절" };
    }

    /// <summary>§9.2 트레일 손절(#245 H-B3-3). 동결 계약의 트리거·거리가 둘 다 있으면: 완료봉 종가가 진입+트리거R에
    /// 한 번이라도 도달한 뒤부터 손절을 max(현재 손절, 보유 중 최고가 − 거리R)로 올린다. 단조 상향만 하며,
    /// 무장 상태는 StopBasis 꼬리표로 보존해 종가가 트리거 아래로 되돌아와도 계속 고점을 추종한다. BE 손절과 함께 작동한다.</summary>
    const string TrailingStopBasis = "트레일: 고점 추종 손절";

    static SimTrade ArmTrailingStop(SimTrade trade, Candle bar)
    {
        var context = trade.Structure;
        if (context?.TrailingStopTriggerR is not { } triggerR || context.TrailingStopDistanceR is not { } distanceR)
            return trade;
        if (!double.IsFinite(triggerR) || !double.IsFinite(distanceR) || triggerR < 0 || !(distanceR > 0)) return trade;
        var risk = Math.Abs(trade.EntryPrice - (double)context.PlanSnapshot.Stop);
        if (!(risk > 0) || !double.IsFinite(risk)) return trade;

        var armed = string.Equals(trade.StopBasis, TrailingStopBasis, StringComparison.Ordinal);
        if (!armed)
        {
            var reachedTrigger = trade.Side == TradeSide.Long
                ? bar.Close >= trade.EntryPrice + triggerR * risk
                : bar.Close <= trade.EntryPrice - triggerR * risk;
            if (!reachedTrigger) return trade;
        }
        var candidate = trade.Side == TradeSide.Long
            ? bar.High - distanceR * risk
            : bar.Low + distanceR * risk;
        var newStop = trade.Side == TradeSide.Long
            ? Math.Max(trade.Stop, candidate)
            : Math.Min(trade.Stop, candidate);
        return trade with { Stop = newStop, StopBasis = TrailingStopBasis };
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
    {
        var gross = (trade.Side == TradeSide.Long
            ? exitPrice / trade.EntryPrice - 1
            : 1 - exitPrice / trade.EntryPrice) * 100;
        var borrow = trade.Side == TradeSide.Short && trade.Structure?.PlanSnapshot.BorrowCostPerShare is { } cost
            ? cost / (decimal)trade.EntryPrice * 100 : 0;
        return trade with
        {
            Status = status, ExitPrice = exitPrice, ExitAt = exitAt,
            PnlPercent = Math.Round(gross - MarketRules.RoundTripFeePercent - (double)borrow, 2),
            LastPrice = lastPrice, LastPriceAt = estimated ? trade.LastPriceAt : bar is null ? exitAt : BarCloseAt(bar), ExitEstimated = estimated,
            Execution = WithExitEvidence(trade.Execution, source, estimated ? trade.LastPriceAt : bar is null ? exitAt : BarCloseAt(bar), decision, bar)
        };
    }

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
    static bool IsStopHit(TradeSide side, double price, double stop) =>
        side == TradeSide.Long ? price <= stop : price >= stop;
    static bool IsTargetHit(TradeSide side, double price, double target) =>
        side == TradeSide.Long ? price >= target : price <= target;
    static bool CanApplyQuote(SimTrade trade, DateTimeOffset quoteAt) => trade.Execution?.LastQuoteAt is not { } lastQuoteAt || quoteAt > lastQuoteAt;
    static bool SameMinute(DateTimeOffset left, DateTimeOffset right) => left.Year == right.Year && left.Month == right.Month &&
        left.Day == right.Day && left.Hour == right.Hour && left.Minute == right.Minute && left.Offset == right.Offset;
}
