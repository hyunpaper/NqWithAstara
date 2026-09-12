using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>켈트너 채널 한 봉의 출력 (C3-2, #170).</summary>
public sealed record KeltnerPoint(DateTimeOffset BarEnd, double? Upper, double? Middle, double? Lower, bool Warmup);

/// <summary>켈트너 채널: EMA(20) ± 1.5·ATR(20) (C3-2, #170). 기존 ATR(14) 인스턴스와 무관한 별도 계산이다.</summary>
public static class KeltnerChannel
{
    public const int DefaultEmaPeriod = 20;
    public const int DefaultAtrPeriod = 20;
    public const double DefaultAtrFactor = 1.5;

    public static ImmutableArray<KeltnerPoint> Series(IReadOnlyList<IndicatorBar> bars,
        decimal? previousSessionClose, int emaPeriod = DefaultEmaPeriod, int atrPeriod = DefaultAtrPeriod,
        double atrFactor = DefaultAtrFactor)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(emaPeriod, nameof(emaPeriod));
        IndicatorMath.RequirePeriod(atrPeriod, nameof(atrPeriod));

        var ema = Ema.Series(bars, emaPeriod);
        var atr = AverageTrueRange.Series(bars, previousSessionClose, atrPeriod);
        var result = ImmutableArray.CreateBuilder<KeltnerPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (ema[i].Value is not { } basis || atr[i].Value is not { } range)
            {
                result.Add(new KeltnerPoint(bars[i].End, null, null, null, true));
                continue;
            }
            var width = atrFactor * range;
            result.Add(new KeltnerPoint(bars[i].End, IndicatorRounding.Price(basis + width),
                IndicatorRounding.Price(basis), IndicatorRounding.Price(basis - width), false));
        }
        return result.ToImmutable();
    }
}
