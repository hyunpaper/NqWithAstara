using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>RSI 한 봉의 출력 (C2, #166).</summary>
public sealed record RsiPoint(DateTimeOffset BarEnd, double? Value, bool Warmup);

/// <summary>RSI(14): Wilder RMA, 첫 period개 상승·하락 평균 SMA 시딩 (C2, #166).</summary>
public static class Rsi
{
    public const int DefaultPeriod = 14;

    public static int RequiredBars(int period = DefaultPeriod) => period + 1;

    public static ImmutableArray<RsiPoint> Series(IReadOnlyList<IndicatorBar> bars, int period = DefaultPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(period, nameof(period));

        var values = new double?[bars.Count];
        if (bars.Count > period)
        {
            double gain = 0, loss = 0;
            for (var i = 1; i <= period; i++)
            {
                var change = (double)(bars[i].Close - bars[i - 1].Close);
                if (change > 0) gain += change; else loss -= change;
            }
            var avgGain = gain / period;
            var avgLoss = loss / period;
            values[period] = Compute(avgGain, avgLoss);

            for (var i = period + 1; i < bars.Count; i++)
            {
                var change = (double)(bars[i].Close - bars[i - 1].Close);
                var up = change > 0 ? change : 0;
                var down = change < 0 ? -change : 0;
                avgGain = IndicatorMath.WilderNext(avgGain, up, period);
                avgLoss = IndicatorMath.WilderNext(avgLoss, down, period);
                values[i] = Compute(avgGain, avgLoss);
            }
        }

        var result = ImmutableArray.CreateBuilder<RsiPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
            result.Add(new RsiPoint(bars[i].End, IndicatorRounding.Ratio(values[i]), values[i] is null));
        return result.ToImmutable();
    }

    static double Compute(double avgGain, double avgLoss)
    {
        var sum = avgGain + avgLoss;
        return sum < 1e-14 ? 0 : 100.0 * avgGain / sum;
    }
}
