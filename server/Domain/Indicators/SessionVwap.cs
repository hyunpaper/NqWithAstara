using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>VWAP 한 봉의 출력. 누적 거래량이 0이면 warmup이다 (C2, #166).</summary>
public sealed record VwapPoint(DateTimeOffset BarEnd, double? Vwap, double? StdDev, bool Warmup);

/// <summary>VWAP: 정규장 앵커, HLC/3×거래량 누적, σ는 거래량가중 편차 (C2, #166).</summary>
public static class SessionVwap
{
    public const int RequiredBars = 1;

    public static ImmutableArray<VwapPoint> Series(IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        double cumulativeVolume = 0, cumulativeNotional = 0, cumulativeSquares = 0;
        var result = ImmutableArray.CreateBuilder<VwapPoint>(bars.Count);
        foreach (var bar in bars)
        {
            var typical = (double)(bar.High + bar.Low + bar.Close) / 3.0;
            var volume = (double)bar.Volume;
            cumulativeVolume += volume;
            cumulativeNotional += typical * volume;
            cumulativeSquares += typical * typical * volume;

            if (cumulativeVolume <= 0)
            {
                result.Add(new VwapPoint(bar.End, null, null, true));
                continue;
            }

            var vwap = cumulativeNotional / cumulativeVolume;
            var variance = cumulativeSquares / cumulativeVolume - vwap * vwap;
            if (variance < 1e-14) variance = 0;
            result.Add(new VwapPoint(bar.End, IndicatorRounding.Price(vwap),
                IndicatorRounding.Price(Math.Sqrt(variance)), false));
        }
        return result.ToImmutable();
    }
}
