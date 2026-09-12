using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>스토캐스틱 슬로우 한 봉의 출력 (C2, #166).</summary>
public sealed record StochasticPoint(DateTimeOffset BarEnd, double? SlowK, double? SlowD, bool Warmup);

/// <summary>Stoch Slow(14,3,3): FastK를 SMA3 → %K, 다시 SMA3 → %D. 고=저 구간은 FastK 0 (C2, #166).</summary>
public static class StochasticSlow
{
    public const int DefaultFastKPeriod = 14;
    public const int DefaultSlowKPeriod = 3;
    public const int DefaultSlowDPeriod = 3;

    public static int RequiredBars(int fastK = DefaultFastKPeriod, int slowK = DefaultSlowKPeriod, int slowD = DefaultSlowDPeriod)
        => fastK + slowK + slowD - 2;

    public static ImmutableArray<StochasticPoint> Series(IReadOnlyList<IndicatorBar> bars,
        int fastKPeriod = DefaultFastKPeriod, int slowKPeriod = DefaultSlowKPeriod, int slowDPeriod = DefaultSlowDPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(fastKPeriod, nameof(fastKPeriod));
        IndicatorMath.RequirePeriod(slowKPeriod, nameof(slowKPeriod));
        IndicatorMath.RequirePeriod(slowDPeriod, nameof(slowDPeriod));

        var fastK = new double?[bars.Count];
        for (var i = fastKPeriod - 1; i < bars.Count; i++)
        {
            double highest = double.NegativeInfinity, lowest = double.PositiveInfinity;
            for (var k = i - fastKPeriod + 1; k <= i; k++)
            {
                highest = Math.Max(highest, (double)bars[k].High);
                lowest = Math.Min(lowest, (double)bars[k].Low);
            }
            var range = highest - lowest;
            fastK[i] = range != 0 ? 100.0 * ((double)bars[i].Close - lowest) / range : 0;
        }

        var slowKValues = SimpleAverage(fastK, slowKPeriod);
        var slowDValues = SimpleAverage(slowKValues, slowDPeriod);

        var result = ImmutableArray.CreateBuilder<StochasticPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            var warmup = slowDValues[i] is null;
            result.Add(new StochasticPoint(bars[i].End,
                warmup ? null : IndicatorRounding.Ratio(slowKValues[i]),
                IndicatorRounding.Ratio(slowDValues[i]), warmup));
        }
        return result.ToImmutable();
    }

    static double?[] SimpleAverage(double?[] source, int period)
    {
        var values = new double?[source.Length];
        for (var i = period - 1; i < source.Length; i++)
        {
            double sum = 0;
            var complete = true;
            for (var k = i - period + 1; k <= i; k++)
            {
                if (source[k] is not { } v) { complete = false; break; }
                sum += v;
            }
            if (complete) values[i] = sum / period;
        }
        return values;
    }
}
