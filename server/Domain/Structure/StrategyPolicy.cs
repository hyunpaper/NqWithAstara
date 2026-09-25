using Astra.Server;

namespace Astra.Server.Domain.Structure;

public enum StrategyDirection { TrendUp, TrendDown, Range }
public enum VolatilityBand { Low, Normal, High }

/// <summary>미래 봉을 보지 않고 신호 시점에 고정하는 방향×변동성 구간.</summary>
public sealed record StrategyRegime(StrategyDirection Direction, VolatilityBand Volatility)
{
    public string Key => $"{Direction.ToString().ToUpperInvariant()}_{Volatility.ToString().ToUpperInvariant()}";
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

    public static bool IsSideAligned(StrategyRegime regime, TradeSide side) =>
        regime.Direction == StrategyDirection.Range ||
        regime.Direction == StrategyDirection.TrendUp && side == TradeSide.Long ||
        regime.Direction == StrategyDirection.TrendDown && side == TradeSide.Short;
}
