using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>ADX/DMI 한 봉의 출력. DI와 ADX는 warmup 길이가 다르다 (C2, #166).</summary>
public sealed record AdxPoint(DateTimeOffset BarEnd, double? PlusDi, double? MinusDi, double? Adx,
    bool DiWarmup, bool AdxWarmup);

/// <summary>ADX/DMI(14): +DM/−DM/TR 전 단계 Wilder 평활, ADX는 첫 period개 DX의 평균으로 시딩 (C2, #166).</summary>
public static class DirectionalMovement
{
    public const int DefaultPeriod = 14;

    public static int RequiredBarsForDi(int period = DefaultPeriod) => period + 1;

    public static int RequiredBarsForAdx(int period = DefaultPeriod) => 2 * period;

    public static ImmutableArray<AdxPoint> Series(IReadOnlyList<IndicatorBar> bars, int period = DefaultPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(period, nameof(period));

        var plusDi = new double?[bars.Count];
        var minusDi = new double?[bars.Count];
        var adx = new double?[bars.Count];

        double smoothedPlusDm = 0, smoothedMinusDm = 0, smoothedTr = 0, sumDx = 0, prevAdx = 0;
        for (var i = 1; i < bars.Count; i++)
        {
            double upMove = (double)(bars[i].High - bars[i - 1].High);
            double downMove = (double)(bars[i - 1].Low - bars[i].Low);
            var plusDm = upMove > 0 && upMove > downMove ? upMove : 0;
            var minusDm = downMove > 0 && downMove > upMove ? downMove : 0;
            var trueRange = IndicatorMath.TrueRange(bars[i], bars[i - 1].Close);

            if (i < period)
            {
                smoothedPlusDm += plusDm;
                smoothedMinusDm += minusDm;
                smoothedTr += trueRange;
                continue;
            }

            smoothedPlusDm = smoothedPlusDm - smoothedPlusDm / period + plusDm;
            smoothedMinusDm = smoothedMinusDm - smoothedMinusDm / period + minusDm;
            smoothedTr = smoothedTr - smoothedTr / period + trueRange;

            double plus = 0, minus = 0;
            if (smoothedTr > 1e-14)
            {
                plus = 100.0 * smoothedPlusDm / smoothedTr;
                minus = 100.0 * smoothedMinusDm / smoothedTr;
            }
            plusDi[i] = plus;
            minusDi[i] = minus;

            var diSum = plus + minus;
            var dx = diSum > 1e-14 ? 100.0 * Math.Abs(plus - minus) / diSum : 0;

            if (i < 2 * period - 1) { sumDx += dx; continue; }
            if (i == 2 * period - 1)
            {
                sumDx += dx;
                prevAdx = sumDx / period;
            }
            else if (diSum > 1e-14) prevAdx = IndicatorMath.WilderNext(prevAdx, dx, period);
            adx[i] = prevAdx;
        }

        var result = ImmutableArray.CreateBuilder<AdxPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
            result.Add(new AdxPoint(bars[i].End,
                IndicatorRounding.Ratio(plusDi[i]), IndicatorRounding.Ratio(minusDi[i]), IndicatorRounding.Ratio(adx[i]),
                plusDi[i] is null, adx[i] is null));
        return result.ToImmutable();
    }
}
