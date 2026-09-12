using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>돈치안 채널 한 봉의 출력 (C2, #166).</summary>
public sealed record DonchianPoint(DateTimeOffset BarEnd, double? Upper, double? Middle, double? Lower, bool Warmup);

/// <summary>Donchian(20): 최근 N봉 고가 최대·저가 최소와 그 중간값 (C2, #166).</summary>
public static class Donchian
{
    public const int DefaultPeriod = 20;

    public static int RequiredBars(int period = DefaultPeriod) => period;

    public static ImmutableArray<DonchianPoint> Series(IReadOnlyList<IndicatorBar> bars, int period = DefaultPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(period, nameof(period));

        var result = ImmutableArray.CreateBuilder<DonchianPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (i < period - 1)
            {
                result.Add(new DonchianPoint(bars[i].End, null, null, null, true));
                continue;
            }
            double upper = double.NegativeInfinity, lower = double.PositiveInfinity;
            for (var k = i - period + 1; k <= i; k++)
            {
                upper = Math.Max(upper, (double)bars[k].High);
                lower = Math.Min(lower, (double)bars[k].Low);
            }
            result.Add(new DonchianPoint(bars[i].End, IndicatorRounding.Price(upper),
                IndicatorRounding.Price((upper + lower) / 2), IndicatorRounding.Price(lower), false));
        }
        return result.ToImmutable();
    }
}
