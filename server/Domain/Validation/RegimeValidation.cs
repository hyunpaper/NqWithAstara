using Astra.Server.Domain.Structure;

namespace Astra.Server.Domain.Validation;

public sealed record RegimeValidationPolicy(int VolatilityLookbackBars, double LowVolatilityRatio,
    double HighVolatilityRatio, int MinimumCompletedBars)
{
    public static readonly RegimeValidationPolicy Default = new(20, .75, 1.25, 20);
}

public sealed record RegimeClassification(string Status, string? Key, StrategyDirection? Direction,
    VolatilityBand? Volatility, int CompletedBars, string? UnavailableReason)
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
}

public static class RegimeValidationClassifier
{
    public const string InsufficientBars = "INSUFFICIENT_COMPLETED_SESSION_BARS";
    public const string AtrUnavailable = "ATR_UNAVAILABLE";
    public const string InvalidPolicy = "INVALID_REGIME_VALIDATION_POLICY";

    public static RegimeClassification Classify(TrendState trend, double? atr,
        IReadOnlyList<StructureBar> bars, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
        DateTimeOffset asOf, RegimeValidationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var limits = policy ?? RegimeValidationPolicy.Default;
        if (limits.VolatilityLookbackBars < 1 || limits.MinimumCompletedBars < 1 ||
            !double.IsFinite(limits.LowVolatilityRatio) || !double.IsFinite(limits.HighVolatilityRatio) ||
            limits.LowVolatilityRatio <= 0 || limits.LowVolatilityRatio >= limits.HighVolatilityRatio)
            return Missing(0, InvalidPolicy);

        var completed = bars.Where(x => x.Start >= sessionStart && x.End <= sessionEnd && x.End <= asOf)
            .OrderBy(x => x.Start).ToArray();
        if (completed.Length < limits.MinimumCompletedBars)
            return Missing(completed.Length, $"{InsufficientBars}:{completed.Length}/{limits.MinimumCompletedBars}");
        if (atr is not > 0 || !double.IsFinite(atr.Value))
            return Missing(completed.Length, AtrUnavailable);

        var ranges = completed.TakeLast(limits.VolatilityLookbackBars)
            .Select(x => (double)(x.High - x.Low)).Where(x => double.IsFinite(x) && x > 0)
            .Order().ToArray();
        if (ranges.Length < Math.Min(limits.MinimumCompletedBars, limits.VolatilityLookbackBars))
            return Missing(completed.Length, $"{InsufficientBars}:{ranges.Length}/{Math.Min(limits.MinimumCompletedBars, limits.VolatilityLookbackBars)}");

        var baseline = ranges[ranges.Length / 2];
        var ratio = atr.Value / baseline;
        var volatility = ratio < limits.LowVolatilityRatio ? VolatilityBand.Low
            : ratio > limits.HighVolatilityRatio ? VolatilityBand.High : VolatilityBand.Normal;
        var direction = trend switch
        {
            TrendState.Up => StrategyDirection.TrendUp,
            TrendState.Down => StrategyDirection.TrendDown,
            _ => StrategyDirection.Range
        };
        var regime = new StrategyRegime(direction, volatility);
        return new(RegimeClassification.Available, regime.Key, direction, volatility, completed.Length, null);
    }

    static RegimeClassification Missing(int bars, string reason) =>
        new(RegimeClassification.Unavailable, null, null, null, bars, reason);
}

public sealed record RegimeValidationSample(string EventId, string Symbol, DateOnly SessionDate,
    DateTimeOffset SignalAt, string? Regime, string EntryKind, bool Approved, bool Entered, string? ExitReason,
    double? StopDistancePercent, double? TargetDistancePercent, double? NetPnlPercent);

public sealed record RegimePolicyMeasurement(string Regime, string EntryKind, int Sessions, int Symbols,
    int Signals, int Approved, int Entries, int Closed, int Stops, int Targets, int Eod,
    double? EntryRatePercent, double? WinRatePercent, double? AverageStopDistancePercent,
    double? AverageTargetDistancePercent, double? NetPnlPercent);

public sealed record RegimeValidationReport(string Status, DateOnly TrainFrom, DateOnly TrainTo,
    DateOnly OutOfSampleFrom, DateOnly OutOfSampleTo, int OutOfSampleSessions,
    IReadOnlyList<RegimePolicyMeasurement> Measurements, IReadOnlyList<string> Limitations);

public static class RegimeValidationEvaluator
{
    public const int MinimumOutOfSampleSessions = 2;
    public const string InsufficientOosSessions = "INSUFFICIENT_OUT_OF_SAMPLE_SESSIONS";
    public const string OutOfSampleWindowUnavailable = "OUT_OF_SAMPLE_WINDOW_UNAVAILABLE";
    public const string RegimeUnavailable = "REGIME_UNAVAILABLE";

    public static RegimeValidationReport Evaluate(IReadOnlyList<RegimeValidationSample> samples,
        DateOnly trainFrom, DateOnly trainTo, DateOnly outOfSampleFrom, DateOnly outOfSampleTo)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (trainFrom > trainTo) throw new ArgumentException("훈련 구간 시작일은 종료일보다 늦을 수 없습니다.");
        if (outOfSampleFrom > outOfSampleTo) throw new ArgumentException("OOS 구간 시작일은 종료일보다 늦을 수 없습니다.");
        if (trainTo >= outOfSampleFrom) throw new ArgumentException("훈련 구간과 OOS 구간은 겹칠 수 없습니다.");

        var oos = samples.Where(x => x.SessionDate >= outOfSampleFrom && x.SessionDate <= outOfSampleTo)
            .OrderBy(x => x.SessionDate).ThenBy(x => x.SignalAt).ThenBy(x => x.EventId, StringComparer.Ordinal)
            .ToArray();
        var sessions = oos.Select(x => x.SessionDate).Distinct().Count();
        var limitations = new List<string>();
        if (sessions < MinimumOutOfSampleSessions)
            limitations.Add($"{InsufficientOosSessions}:{sessions}/{MinimumOutOfSampleSessions}");
        if (oos.Any(x => string.IsNullOrWhiteSpace(x.Regime))) limitations.Add(RegimeUnavailable);

        var measurements = oos.Where(x => !string.IsNullOrWhiteSpace(x.Regime))
            .GroupBy(x => (Regime: x.Regime!, x.EntryKind))
            .OrderBy(x => x.Key.Regime, StringComparer.Ordinal).ThenBy(x => x.Key.EntryKind, StringComparer.Ordinal)
            .Select(Measure).ToArray();
        var status = limitations.Count == 0 ? RegimeClassification.Available : RegimeClassification.Unavailable;
        return new(status, trainFrom, trainTo, outOfSampleFrom, outOfSampleTo, sessions, measurements, limitations);
    }

    static RegimePolicyMeasurement Measure(IGrouping<(string Regime, string EntryKind), RegimeValidationSample> group)
    {
        var rows = group.ToArray();
        var entered = rows.Where(x => x.Entered).ToArray();
        var closed = entered.Where(x => x.ExitReason is not null).ToArray();
        var pnl = closed.Where(x => x.NetPnlPercent is not null && double.IsFinite(x.NetPnlPercent.Value))
            .Select(x => x.NetPnlPercent!.Value).ToArray();
        var stops = entered.Where(x => x.StopDistancePercent is not null && double.IsFinite(x.StopDistancePercent.Value))
            .Select(x => x.StopDistancePercent!.Value).ToArray();
        var targets = entered.Where(x => x.TargetDistancePercent is not null && double.IsFinite(x.TargetDistancePercent.Value))
            .Select(x => x.TargetDistancePercent!.Value).ToArray();
        return new(group.Key.Regime, group.Key.EntryKind, rows.Select(x => x.SessionDate).Distinct().Count(),
            rows.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count(), rows.Length,
            rows.Count(x => x.Approved), entered.Length, closed.Length,
            closed.Count(x => x.ExitReason == "STOP"), closed.Count(x => x.ExitReason == "TARGET"),
            closed.Count(x => x.ExitReason == "EOD"), Percent(entered.Length, rows.Length),
            pnl.Length == 0 ? null : Math.Round(pnl.Count(x => x > 0) * 100d / pnl.Length, 2),
            Average(stops), Average(targets), pnl.Length == 0 ? null : Math.Round(pnl.Sum(), 6));
    }

    static double? Percent(int numerator, int denominator) =>
        denominator == 0 ? null : Math.Round(numerator * 100d / denominator, 2);

    static double? Average(double[] values) => values.Length == 0 ? null : Math.Round(values.Average(), 6);
}
