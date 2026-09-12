using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>EMA 한 봉의 출력. warmup 중에는 Value가 null이다 (C2, #166).</summary>
public sealed record EmaPoint(DateTimeOffset BarEnd, double? Value, bool Warmup);

/// <summary>EMA(n): α=2/(n+1) 재귀, 세션 첫 n봉 SMA 시딩 (C2, #166).</summary>
public static class Ema
{
    public static int RequiredBars(int period) => period;

    public static ImmutableArray<EmaPoint> Series(IReadOnlyList<IndicatorBar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(period, nameof(period));
        var closes = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++) closes[i] = (double)bars[i].Close;
        var values = Values(closes, period);
        var result = ImmutableArray.CreateBuilder<EmaPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
            result.Add(new EmaPoint(bars[i].End, IndicatorRounding.Price(values[i]), values[i] is null));
        return result.ToImmutable();
    }

    /// <summary>반올림 전 내부 EMA 값 배열. 다른 지표(MACD)가 중간 계산에 재사용한다 (C2, #166).</summary>
    public static double?[] Values(IReadOnlyList<double> source, int period)
    {
        ArgumentNullException.ThrowIfNull(source);
        IndicatorMath.RequirePeriod(period, nameof(period));
        var values = new double?[source.Count];
        if (source.Count < period) return values;

        double seed = 0;
        for (var i = 0; i < period; i++) seed += source[i];
        var ema = seed / period;
        values[period - 1] = ema;

        var alpha = 2.0 / (period + 1);
        for (var i = period; i < source.Count; i++)
        {
            ema = (source[i] - ema) * alpha + ema;
            values[i] = ema;
        }
        return values;
    }
}
