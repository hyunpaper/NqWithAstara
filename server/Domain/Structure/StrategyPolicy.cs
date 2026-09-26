using Astra.Server;

namespace Astra.Server.Domain.Structure;

public enum StrategyDirection { TrendUp, TrendDown, Range }
public enum VolatilityBand { Low, Normal, High }

/// <summary>미래 봉을 보지 않고 신호 시점에 고정하는 방향×변동성 구간.</summary>
public sealed record StrategyRegime(StrategyDirection Direction, VolatilityBand Volatility)
{
    public string Key => $"{Direction.ToString().ToUpperInvariant()}_{Volatility.ToString().ToUpperInvariant()}";
}

public sealed record StrategyRegimeAssessment(string Status, StrategyRegime? Regime, double? Confidence,
    int CompletedBars, double? AtrToMedianRangeRatio, IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Limitations)
{
    public const string Available = "available";
    public const string InsufficientData = "insufficient_data";
}

public static class StrategyRegimeClassifier
{
    public static StrategyRegime Classify(TrendAssessment trend, IReadOnlyList<StructureBar> bars,
        StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(trend);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(policy);
        var direction = trend.State switch
        {
            TrendState.Up => StrategyDirection.TrendUp,
            TrendState.Down => StrategyDirection.TrendDown,
            _ => StrategyDirection.Range
        };
        var ranges = bars.TakeLast(Math.Max(1, policy.RegimeVolatilityLookbackBars))
            .Select(x => (double)(x.High - x.Low)).Where(x => double.IsFinite(x) && x > 0)
            .OrderBy(x => x).ToArray();
        var baseline = ranges.Length == 0 ? (double?)null : ranges[ranges.Length / 2];
        var ratio = trend.Atr1m is > 0 && baseline is > 0 ? trend.Atr1m.Value / baseline.Value : (double?)null;
        var volatility = ratio switch
        {
            null => VolatilityBand.Normal,
            _ when ratio.Value < policy.LowVolatilityRatio => VolatilityBand.Low,
            _ when ratio.Value > policy.HighVolatilityRatio => VolatilityBand.High,
            _ => VolatilityBand.Normal
        };
        return new(direction, volatility);
    }

    public static StrategyRegimeAssessment Assess(TrendAssessment trend, IReadOnlyList<StructureBar> bars,
        StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(trend);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(policy);

        var lookback = Math.Max(1, policy.RegimeVolatilityLookbackBars);
        var completed = bars.Where(x => x.End <= trend.AnalysisCutoff).OrderBy(x => x.Start).ToArray();
        var ranges = completed.TakeLast(lookback).Select(x => (double)(x.High - x.Low))
            .Where(x => double.IsFinite(x) && x > 0).Order().ToArray();
        var limitations = new List<string>();
        if (!trend.Available) limitations.Add("TREND_UNAVAILABLE");
        if (trend.Atr1m is not > 0 || !double.IsFinite(trend.Atr1m.Value)) limitations.Add("ATR_UNAVAILABLE");
        if (ranges.Length < lookback) limitations.Add($"INSUFFICIENT_COMPLETED_BARS:{ranges.Length}/{lookback}");

        if (limitations.Count > 0)
            return new(StrategyRegimeAssessment.InsufficientData, null, null, completed.Length, null,
                Evidence(trend, ranges.Length, lookback, null), limitations);

        var baseline = ranges[ranges.Length / 2];
        var ratio = trend.Atr1m!.Value / baseline;
        var regime = Classify(trend, completed, policy);
        var magnitude = Math.Clamp(Math.Abs(trend.SignedTrend!.Value) / 100d, 0, 1);
        var confidence = Math.Round(regime.Direction == StrategyDirection.Range ? 1 - magnitude : magnitude, 6);
        return new(StrategyRegimeAssessment.Available, regime, confidence, completed.Length, ratio,
            Evidence(trend, ranges.Length, lookback, ratio), []);
    }

    public static bool IsSideAligned(StrategyRegime regime, TradeSide side) =>
        regime.Direction == StrategyDirection.Range ||
        regime.Direction == StrategyDirection.TrendUp && side == TradeSide.Long ||
        regime.Direction == StrategyDirection.TrendDown && side == TradeSide.Short;

    static string[] Evidence(TrendAssessment trend, int ranges, int lookback, double? ratio) =>
    [
        $"TREND_STATE:{trend.State.ToString().ToUpperInvariant()}",
        $"SIGNED_TREND:{StructureMath.Number(trend.SignedTrend)}",
        $"COMPLETED_RANGE_BARS:{ranges}/{lookback}",
        $"ATR_TO_MEDIAN_RANGE:{StructureMath.Number(ratio)}",
        "CONFIDENCE_BASIS:TREND_MAGNITUDE"
    ];
}
