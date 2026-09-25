namespace Astra.Server.Domain.Validation;

public sealed record RiskFrequencyThresholds(int MinRealizedTrades, int MinSessions,
    double MinAverageTradesPerSession, double MaxAverageTradesPerSession,
    double MaxDrawdownPercent, double MaxAverageExposureMinutesPerSession)
{
    public static readonly RiskFrequencyThresholds Default = new(20, 3, 0.2, 3d, 10d, 120d);
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RiskFrequencyVerdict>))]
public enum RiskFrequencyVerdict { InsufficientSample, FrequencyOutsideTarget, RiskLimitExceeded, MeetsContract }

public sealed record DailyRiskFrequencyMetrics(DateOnly TradingDate, int Opportunities, int Trades,
    int RealizedTrades, double? ExposureMinutes);

public sealed record SymbolRiskFrequencyMetrics(string Symbol, int Sessions, int Opportunities, int Trades,
    int RealizedTrades, double AverageOpportunitiesPerSession, double AverageTradesPerSession,
    double? WinRatePercent, double? PayoffRatio, double? ProfitFactor, double? MaxDrawdownPercent,
    double? TotalExposureMinutes, double? AverageExposureMinutesPerTrade,
    double? AverageExposureMinutesPerSession, RiskFrequencyVerdict Verdict,
    IReadOnlyList<string> VerdictReasons, IReadOnlyList<DailyRiskFrequencyMetrics> Daily);

public sealed record RiskFrequencyFold(int Index, DateOnly InSampleFrom, DateOnly InSampleTo,
    DateOnly OutOfSampleFrom, DateOnly OutOfSampleTo, IReadOnlyList<SymbolRiskFrequencyMetrics> OutOfSample);

public sealed record RiskFrequencyReport(string Method, RiskFrequencyThresholds Thresholds,
    IReadOnlyList<SymbolRiskFrequencyMetrics> Overall, IReadOnlyList<RiskFrequencyFold> Folds,
    IReadOnlyList<string> Contract, IReadOnlyList<string> Limitations);

public static class RiskFrequencyEvaluator
{
    public const string Method = "anchored-walk-forward-by-ny-trading-date";
    public const int DefaultFolds = 3;
    public const string InsufficientSessions = "INSUFFICIENT_SESSIONS_FOR_RISK_FREQUENCY_WALK_FORWARD";

    public static readonly string[] Contract =
    [
        "임계값은 결과를 보기 전에 고정하며 각 종목과 모든 out-of-sample fold에 동일하게 적용한다.",
        "기회는 중복 poll을 접은 EventId 한 건, 거래는 연결된 진입 한 건, 성과는 기준 시각까지 확정된 청산만 센다.",
        "손익비와 PF는 이익·손실 표본이 모두 있을 때만 계산하고, 결측을 0이나 무한대로 바꾸지 않는다.",
        "MDD는 청산 시각 순 비용 후 누적 손익률의 고점 대비 최대 하락이며 복리 자산곡선으로 해석하지 않는다.",
        "노출시간은 확정 청산 거래의 진입부터 청산까지이며 중첩 거래 시간은 합산한다.",
        "학습 구간은 임계값 선택에 사용하지 않고 검증 구간은 항상 그보다 뒤에 있는 New York 거래일로 분리한다."
    ];

    public static RiskFrequencyReport Evaluate(IReadOnlyList<LinkedCandidate> candidates,
        RiskFrequencyThresholds? thresholds = null, int folds = DefaultFolds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (folds < 1) throw new ArgumentOutOfRangeException(nameof(folds));
        var limits = thresholds ?? RiskFrequencyThresholds.Default;
        Validate(limits);
        var rows = candidates.Where(x => x is not null).ToArray();
        var sessions = rows.Select(x => x.TradingDate).Distinct().Order().ToArray();
        var overall = Metrics(rows, limits);
        if (sessions.Length < folds + 1)
            return new RiskFrequencyReport(Method, limits, overall, [], Contract,
                [$"{InsufficientSessions}:{sessions.Length}/{folds + 1}"]);

        var blocks = Split(sessions, folds + 1);
        var result = new List<RiskFrequencyFold>(folds);
        for (var i = 0; i < folds; i++)
        {
            var inSample = blocks.Take(i + 1).SelectMany(x => x).ToArray();
            var outOfSample = blocks[i + 1];
            var outDates = outOfSample.ToHashSet();
            result.Add(new RiskFrequencyFold(i + 1, inSample[0], inSample[^1], outOfSample[0], outOfSample[^1],
                Metrics(rows.Where(x => outDates.Contains(x.TradingDate)).ToArray(), limits)));
        }

        var limitations = result.SelectMany(x => x.OutOfSample)
            .Where(x => x.Verdict != RiskFrequencyVerdict.MeetsContract)
            .Select(x => $"OUT_OF_SAMPLE_NOT_MET:{x.Symbol}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new RiskFrequencyReport(Method, limits, overall, result, Contract, limitations);
    }

    static IReadOnlyList<SymbolRiskFrequencyMetrics> Metrics(IReadOnlyList<LinkedCandidate> rows,
        RiskFrequencyThresholds limits) => rows.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
        .Select(x => Metric(x.Key, x.ToArray(), limits)).ToArray();

    static SymbolRiskFrequencyMetrics Metric(string symbol, IReadOnlyList<LinkedCandidate> rows,
        RiskFrequencyThresholds limits)
    {
        var sessions = rows.Select(x => x.TradingDate).Distinct().Count();
        var trades = rows.Where(x => x.Trade is not null).ToArray();
        var realized = trades.Where(x => x.HasRealizedPnl)
            .OrderBy(x => x.Trade!.ExitAt).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
        var wins = realized.Select(x => x.RealizedPnlPercent!.Value).Where(x => x > 0).ToArray();
        var losses = realized.Select(x => x.RealizedPnlPercent!.Value).Where(x => x <= 0).ToArray();
        var exposure = realized.Where(x => x.Trade!.ExitAt >= x.Trade.EnteredAt)
            .Select(x => (x.Trade!.ExitAt!.Value - x.Trade.EnteredAt).TotalMinutes).ToArray();
        var opportunitiesPerSession = sessions == 0 ? 0d : rows.Count / (double)sessions;
        var tradesPerSession = sessions == 0 ? 0d : trades.Length / (double)sessions;
        var totalExposure = exposure.Length == 0 ? (double?)null : exposure.Sum();
        var daily = rows.GroupBy(x => x.TradingDate).OrderBy(x => x.Key).Select(day =>
        {
            var dayTrades = day.Where(x => x.Trade is not null).ToArray();
            var dayRealized = dayTrades.Where(x => x.HasRealizedPnl).ToArray();
            var dayExposure = dayRealized.Where(x => x.Trade!.ExitAt >= x.Trade.EnteredAt)
                .Select(x => (x.Trade!.ExitAt!.Value - x.Trade.EnteredAt).TotalMinutes).ToArray();
            return new DailyRiskFrequencyMetrics(day.Key, day.Count(), dayTrades.Length, dayRealized.Length,
                dayExposure.Length == 0 ? null : Round(dayExposure.Sum()));
        }).ToArray();
        var reasons = new List<string>();
        if (realized.Length < limits.MinRealizedTrades)
            reasons.Add($"REALIZED_TRADES_BELOW_MIN:{realized.Length}/{limits.MinRealizedTrades}");
        if (sessions < limits.MinSessions) reasons.Add($"SESSIONS_BELOW_MIN:{sessions}/{limits.MinSessions}");
        if (tradesPerSession < limits.MinAverageTradesPerSession)
            reasons.Add($"AVERAGE_TRADES_BELOW_MIN:{Round(tradesPerSession)}/{limits.MinAverageTradesPerSession}");
        if (tradesPerSession > limits.MaxAverageTradesPerSession)
            reasons.Add($"AVERAGE_TRADES_ABOVE_MAX:{Round(tradesPerSession)}/{limits.MaxAverageTradesPerSession}");
        var drawdown = MaxDrawdown(realized.Select(x => x.RealizedPnlPercent!.Value));
        var exposurePerSession = totalExposure is null || sessions == 0 ? null : totalExposure / sessions;
        if (drawdown > limits.MaxDrawdownPercent)
            reasons.Add($"MDD_ABOVE_MAX:{Round(drawdown)}/{limits.MaxDrawdownPercent}");
        if (exposurePerSession > limits.MaxAverageExposureMinutesPerSession)
            reasons.Add($"EXPOSURE_ABOVE_MAX:{Round(exposurePerSession)}/{limits.MaxAverageExposureMinutesPerSession}");

        var verdict = reasons.Any(x => x.StartsWith("REALIZED_", StringComparison.Ordinal)
                                           || x.StartsWith("SESSIONS_", StringComparison.Ordinal))
            ? RiskFrequencyVerdict.InsufficientSample
            : reasons.Any(x => x.StartsWith("AVERAGE_TRADES_", StringComparison.Ordinal))
                ? RiskFrequencyVerdict.FrequencyOutsideTarget
                : reasons.Count > 0 ? RiskFrequencyVerdict.RiskLimitExceeded : RiskFrequencyVerdict.MeetsContract;
        return new SymbolRiskFrequencyMetrics(symbol, sessions, rows.Count, trades.Length, realized.Length,
            Round(opportunitiesPerSession), Round(tradesPerSession),
            realized.Length == 0 ? null : Round(wins.Length * 100d / realized.Length),
            wins.Length == 0 || losses.Length == 0 ? null : Round(wins.Average() / Math.Abs(losses.Average())),
            wins.Length == 0 || losses.Length == 0 ? null : Round(wins.Sum() / Math.Abs(losses.Sum())),
            realized.Length == 0 ? null : Round(drawdown), Round(totalExposure),
            totalExposure is null ? null : Round(totalExposure / exposure.Length), Round(exposurePerSession),
            verdict, reasons, daily);
    }

    static double MaxDrawdown(IEnumerable<double> pnl)
    {
        var equity = 0d;
        var peak = 0d;
        var max = 0d;
        foreach (var value in pnl)
        {
            equity += value;
            peak = Math.Max(peak, equity);
            max = Math.Max(max, peak - equity);
        }
        return max;
    }

    static void Validate(RiskFrequencyThresholds value)
    {
        if (value.MinRealizedTrades < 1 || value.MinSessions < 1 || value.MinAverageTradesPerSession < 0
            || value.MaxAverageTradesPerSession < value.MinAverageTradesPerSession || value.MaxDrawdownPercent < 0
            || value.MaxAverageExposureMinutesPerSession < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    static DateOnly[][] Split(DateOnly[] sessions, int count)
    {
        var result = new DateOnly[count][];
        var size = sessions.Length / count;
        var remainder = sessions.Length % count;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var length = size + (i < remainder ? 1 : 0);
            result[i] = sessions.Skip(offset).Take(length).ToArray();
            offset += length;
        }
        return result;
    }

    static double Round(double value) => Math.Round(value, 4);

    static double? Round(double? value) => value is null ? null : Round(value.Value);
}
