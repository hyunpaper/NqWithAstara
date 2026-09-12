using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>MACD 한 봉의 출력. Signal warmup 전에는 Signal·Histogram이 null이다 (C2, #166).</summary>
public sealed record MacdPoint(DateTimeOffset BarEnd, double? Macd, double? Signal, double? Histogram, bool Warmup);

/// <summary>MACD(12/26/9): EMA12−EMA26, Signal은 MACD 첫 9개 SMA 시딩 EMA9, Hist=MACD−Signal (C2, #166).</summary>
public static class Macd
{
    public const int FastPeriod = 12;
    public const int SlowPeriod = 26;
    public const int SignalPeriod = 9;

    public static int RequiredBars(int slowPeriod = SlowPeriod, int signalPeriod = SignalPeriod)
        => slowPeriod + signalPeriod - 1;

    public static ImmutableArray<MacdPoint> Series(IReadOnlyList<IndicatorBar> bars,
        int fastPeriod = FastPeriod, int slowPeriod = SlowPeriod, int signalPeriod = SignalPeriod)
    {
        ArgumentNullException.ThrowIfNull(bars);
        IndicatorMath.RequirePeriod(fastPeriod, nameof(fastPeriod));
        IndicatorMath.RequirePeriod(slowPeriod, nameof(slowPeriod));
        IndicatorMath.RequirePeriod(signalPeriod, nameof(signalPeriod));
        if (fastPeriod >= slowPeriod) throw new ArgumentOutOfRangeException(nameof(fastPeriod));

        var closes = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++) closes[i] = (double)bars[i].Close;
        var fast = Ema.Values(closes, fastPeriod);
        var slow = Ema.Values(closes, slowPeriod);

        var macd = new double?[bars.Count];
        var compact = new List<double>(bars.Count);
        var compactIndex = new List<int>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            if (fast[i] is not { } f || slow[i] is not { } s) continue;
            macd[i] = f - s;
            compact.Add(f - s);
            compactIndex.Add(i);
        }

        var signal = new double?[bars.Count];
        var signalValues = Ema.Values(compact, signalPeriod);
        for (var k = 0; k < compactIndex.Count; k++) signal[compactIndex[k]] = signalValues[k];

        var result = ImmutableArray.CreateBuilder<MacdPoint>(bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            var hist = macd[i] is { } m && signal[i] is { } g ? m - g : (double?)null;
            result.Add(new MacdPoint(bars[i].End,
                IndicatorRounding.Price(macd[i]), IndicatorRounding.Price(signal[i]), IndicatorRounding.Price(hist),
                signal[i] is null));
        }
        return result.ToImmutable();
    }
}
