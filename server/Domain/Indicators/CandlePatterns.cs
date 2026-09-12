using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>캔들 패턴 — TA-Lib CDLENGULFING·CDLHAMMER 정의와 핀바 수기 정의 (C3-2, #170).</summary>
public static class CandlePatterns
{
    public const int EngulfingLookback = 2;
    public const int HammerLookback = 11;

    /// <summary>TA-Lib 기본 캔들 설정 — BodyShort(RealBody,10,1.0)·ShadowVeryShort(HighLow,10,0.1)·Near(HighLow,5,0.2).</summary>
    public const int BodyShortAvgPeriod = 10;
    public const int ShadowVeryShortAvgPeriod = 10;
    public const int NearAvgPeriod = 5;
    public const double ShadowVeryShortFactor = .1;
    public const double NearFactor = .2;

    /// <summary>핀바 꼬리 비율 — 꼬리가 전체 범위의 2/3 이상이면 핀바다(TA-Lib에 없는 수기 정의).</summary>
    public const double PinBarTailRatio = 2.0 / 3.0;

    /// <summary>CDLENGULFING: 장악형. 강세 +, 약세 −이며 경계가 한쪽만 같으면 TA-Lib와 같이 크기 80이다.</summary>
    public static ImmutableArray<int> Engulfing(IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var result = ImmutableArray.CreateBuilder<int>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (i < EngulfingLookback) { result.Add(0); continue; }
            double open = (double)bars[i].Open, close = (double)bars[i].Close;
            double previousOpen = (double)bars[i - 1].Open, previousClose = (double)bars[i - 1].Close;
            var color = close >= open ? 1 : -1;
            var previousColor = previousClose >= previousOpen ? 1 : -1;
            double body, opposite;
            if (color == 1 && previousColor == -1) { body = close - previousOpen; opposite = previousClose - open; }
            else if (color == -1 && previousColor == 1) { body = open - previousClose; opposite = previousOpen - close; }
            else { result.Add(0); continue; }

            var equalities = (body == 0 ? 1 : 0) + (opposite == 0 ? 1 : 0);
            result.Add(body < 0 || opposite < 0 || equalities == 2 ? 0 : color * (100 - 20 * equalities));
        }
        return result.ToImmutable();
    }

    /// <summary>CDLHAMMER: 망치형. 성립하면 +100이다.</summary>
    public static ImmutableArray<int> Hammer(IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var result = ImmutableArray.CreateBuilder<int>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (i < HammerLookback) { result.Add(0); continue; }
            var bodyAverage = Average(bars, i - BodyShortAvgPeriod, i - 1, RealBody) * 1.0;
            var veryShortAverage = Average(bars, i - ShadowVeryShortAvgPeriod, i - 1, HighLowRange) *
                                   ShadowVeryShortFactor;
            var nearAverage = Average(bars, i - 1 - NearAvgPeriod, i - 2, HighLowRange) * NearFactor;
            var body = RealBody(bars[i]);
            var hammer = body < bodyAverage &&
                         LowerShadow(bars[i]) > body &&
                         UpperShadow(bars[i]) < veryShortAverage &&
                         BodyBottom(bars[i]) <= (double)bars[i - 1].Low + nearAverage;
            result.Add(hammer ? 100 : 0);
        }
        return result.ToImmutable();
    }

    /// <summary>강세 핀바(수기 정의): 아래꼬리가 전체 범위의 2/3 이상 — 몸통이 반대쪽(위) 끝에 놓인다.</summary>
    public static ImmutableArray<int> BullishPinBar(IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var result = ImmutableArray.CreateBuilder<int>(bars.Count);
        foreach (var bar in bars)
        {
            var range = HighLowRange(bar);
            result.Add(range > 0 && LowerShadow(bar) >= PinBarTailRatio * range ? 100 : 0);
        }
        return result.ToImmutable();
    }

    public static double RealBody(IndicatorBar bar) => Math.Abs((double)(bar.Close - bar.Open));

    public static double HighLowRange(IndicatorBar bar) => (double)(bar.High - bar.Low);

    public static double UpperShadow(IndicatorBar bar) =>
        (double)bar.High - Math.Max((double)bar.Open, (double)bar.Close);

    public static double LowerShadow(IndicatorBar bar) => BodyBottom(bar) - (double)bar.Low;

    static double BodyBottom(IndicatorBar bar) => Math.Min((double)bar.Open, (double)bar.Close);

    /// <summary>TA-Lib 이동 합의 평균. 구간 [from, to]의 봉이 없으면 0이며 호출부가 lookback으로 막는다.</summary>
    static double Average(IReadOnlyList<IndicatorBar> bars, int from, int to, Func<IndicatorBar, double> range)
    {
        if (from < 0 || to < from) return 0;
        double sum = 0;
        for (var i = from; i <= to; i++) sum += range(bars[i]);
        return sum / (to - from + 1);
    }
}
