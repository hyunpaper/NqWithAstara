using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>ATR 한 봉의 출력 (C2, #166).</summary>
public sealed record AtrPoint(DateTimeOffset BarEnd, double? Value, bool Warmup);

/// <summary>ATR(14): Wilder 평활, 세션 첫 봉 TR은 전일 정규장 종가로 계산한다 (C2, #166).</summary>
public static class AverageTrueRange
{
    public const int DefaultPeriod = 14;

    public static int RequiredBars(int period = DefaultPeriod) => period;

    public static ImmutableArray<AtrPoint> Series(IReadOnlyList<IndicatorBar> bars,
        decimal? previousSessionClose, int period = DefaultPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(period, nameof(period));

        var values = new double?[bars.Count];
        if (bars.Count >= period)
        {
            var trueRange = new double[bars.Count];
            for (var i = 0; i < bars.Count; i++)
                trueRange[i] = IndicatorMath.TrueRange(bars[i], i == 0 ? previousSessionClose : bars[i - 1].Close);

            double seed = 0;
            for (var i = 0; i < period; i++) seed += trueRange[i];
            var atr = seed / period;
            values[period - 1] = atr;

            for (var i = period; i < bars.Count; i++)
            {
                atr = IndicatorMath.WilderNext(atr, trueRange[i], period);
                values[i] = atr;
            }
        }

        var result = ImmutableArray.CreateBuilder<AtrPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
            result.Add(new AtrPoint(bars[i].End, IndicatorRounding.Price(values[i]), values[i] is null));
        return result.ToImmutable();
    }
}
