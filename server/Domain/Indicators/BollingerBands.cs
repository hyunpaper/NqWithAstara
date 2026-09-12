using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>볼린저 밴드 한 봉의 출력. PercentB·BandWidth는 밴드 폭 0이면 null이다 (C2, #166).</summary>
public sealed record BollingerPoint(DateTimeOffset BarEnd, double? Upper, double? Middle, double? Lower,
    double? PercentB, double? BandWidth, bool Warmup);

/// <summary>BB(20,2): SMA20 ± 2·모집단 σ(n 분모) (C2, #166).</summary>
public static class BollingerBands
{
    public const int DefaultPeriod = 20;
    public const double DefaultDeviations = 2.0;

    public static int RequiredBars(int period = DefaultPeriod) => period;

    public static ImmutableArray<BollingerPoint> Series(IReadOnlyList<IndicatorBar> bars,
        int period = DefaultPeriod, double deviations = DefaultDeviations)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(period, nameof(period));

        var result = ImmutableArray.CreateBuilder<BollingerPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (i < period - 1)
            {
                result.Add(new BollingerPoint(bars[i].End, null, null, null, null, null, true));
                continue;
            }

            double sum = 0, sumSquares = 0;
            for (var k = i - period + 1; k <= i; k++)
            {
                var close = (double)bars[k].Close;
                sum += close;
                sumSquares += close * close;
            }
            var mean = sum / period;
            var variance = sumSquares / period - mean * mean;
            if (variance < 1e-14) variance = 0;
            var sigma = Math.Sqrt(variance);
            var upper = mean + deviations * sigma;
            var lower = mean - deviations * sigma;
            var width = upper - lower;
            var close_i = (double)bars[i].Close;
            double? percentB = width > 0 ? (close_i - lower) / width : null;
            double? bandWidth = mean != 0 ? width / mean : null;

            result.Add(new BollingerPoint(bars[i].End,
                IndicatorRounding.Price(upper), IndicatorRounding.Price(mean), IndicatorRounding.Price(lower),
                IndicatorRounding.Ratio(percentB), IndicatorRounding.Ratio(bandWidth), false));
        }
        return result.ToImmutable();
    }
}
