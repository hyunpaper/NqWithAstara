using System.Collections.Immutable;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Confluence;

/// <summary>
/// C3 기법 어댑터 1군 10개 (#167). 전부 순수 함수이며 완료 봉과 이미 관측된 값만 읽는다(C6).
/// score·confidence는 C3 표의 정의 그대로이고 어떤 임계도 성과로 탐색하지 않았다.
/// </summary>
public static class ConfluenceTechniques
{
    /// <summary>C3 표의 1군 10개를 평가 대상 봉(마지막 완료 봉) 기준으로 계산한다.</summary>
    public static ImmutableArray<TechniqueSignal> Evaluate(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        return
        [
            Macd(input, policy),
            Rsi(input, policy),
            BollingerPercentB(input, policy),
            AdxDmi(input, policy),
            VwapDeviation(input, policy),
            RelativeVolume(input, policy),
            AtrChannel(input, policy),
            OpeningRange(input, policy),
            RelativeStrength(input, policy),
            OrderBookImbalance(input, policy)
        ];
    }

    // ── MACD: Hist를 ATR로 정규화 후 tanh, Signal 상향 교차 봉은 0선 위일 때만 +0.2 ──
    public static TechniqueSignal Macd(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.Macd;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var macd = Indicators.Macd.Series(input.Bars, policy.MacdFastPeriod, policy.MacdSlowPeriod,
            policy.MacdSignalPeriod);
        var atr = Atr(input, policy);
        if (macd[i].Warmup || macd[i].Histogram is not { } histogram || macd[i].Macd is not { } line ||
            macd[i].Signal is not { } signal || atr[i].Value is not { } range || range <= 0)
            return TechniqueSignal.WarmingUp(name);

        var score = ConfluenceMath.Tanh(histogram / range);
        var crossedUp = i > 0 && macd[i - 1].Macd is { } previousLine && macd[i - 1].Signal is { } previousSignal &&
                        previousLine <= previousSignal && line > signal;
        var bonus = crossedUp && line > 0;
        if (bonus) score += policy.MacdCrossBonus;
        var confidence = line > 0 ? 1 : policy.MacdBelowZeroConfidence;
        return TechniqueSignal.Create(name, score, confidence,
            ("histogram", histogram), ("macd", line), ("signal", signal), ("atr", range),
            ("crossBonus", bonus ? policy.MacdCrossBonus : 0));
    }

    // ── RSI: (RSI−50)/50, 추세장(ADX≥20) 1.0 아니면 0.7 ──
    public static TechniqueSignal Rsi(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.Rsi;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var rsi = Indicators.Rsi.Series(input.Bars, policy.RsiPeriod);
        if (rsi[i].Warmup || rsi[i].Value is not { } value) return TechniqueSignal.WarmingUp(name);

        var adx = DirectionalMovement.Series(input.Bars, policy.AdxPeriod)[i].Adx;
        var trending = adx is { } a && a >= policy.TrendAdxThreshold;
        return TechniqueSignal.Create(name, (value - 50) / 50, trending ? 1 : policy.RsiNonTrendConfidence,
            ("rsi", value), ("adx", adx));
    }

    // ── BB %B: (%B−0.5)×2 클램프, 스퀴즈 후 상단 이탈 +0.3, 밴드폭 극단이면 c=0.6 ──
    public static TechniqueSignal BollingerPercentB(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.BollingerPercentB;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var bands = BollingerBands.Series(input.Bars, policy.BollingerPeriod, policy.BollingerDeviations);
        if (bands[i].Warmup || bands[i].PercentB is not { } percentB || bands[i].Upper is not { } upper)
            return TechniqueSignal.WarmingUp(name);

        var score = ConfluenceMath.Clamp((percentB - .5) * 2);
        // 스퀴즈는 직전 봉까지만 본다 — 평가 봉의 밴드폭으로 자기 자신을 스퀴즈라 부르면 리페인팅이다(C6).
        var squeezed = IsBandWidthExtreme(bands, i - 1, policy, lowest: true);
        var breakout = (double)input.Bars[i].Close > upper;
        var bonus = squeezed && breakout;
        if (bonus) score += policy.BollingerSqueezeBonus;

        var extreme = IsBandWidthExtreme(bands, i, policy, lowest: true) ||
                      IsBandWidthExtreme(bands, i, policy, lowest: false);
        return TechniqueSignal.Create(name, score, extreme ? policy.BollingerExtremeConfidence : 1,
            ("percentB", percentB), ("bandWidth", bands[i].BandWidth), ("upper", upper),
            ("squeezeBonus", bonus ? policy.BollingerSqueezeBonus : 0));
    }

    // ── ADX/DMI: sign(+DI−−DI)×min(ADX/50,1), ADX≥20 1.0 미만 0.4 ──
    public static TechniqueSignal AdxDmi(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.AdxDmi;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var point = DirectionalMovement.Series(input.Bars, policy.AdxPeriod)[i];
        if (point.AdxWarmup || point.DiWarmup || point.Adx is not { } adx || point.PlusDi is not { } plus ||
            point.MinusDi is not { } minus)
            return TechniqueSignal.WarmingUp(name);

        var score = ConfluenceMath.Sign(plus - minus) * Math.Min(adx / policy.AdxScoreScale, 1);
        return TechniqueSignal.Create(name, score, adx >= policy.TrendAdxThreshold ? 1 : policy.AdxLowConfidence,
            ("adx", adx), ("plusDi", plus), ("minusDi", minus));
    }

    // ── VWAP 이격: tanh(σ 단위 이격), confidence 항상 1.0 ──
    public static TechniqueSignal VwapDeviation(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.VwapDeviation;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var point = SessionVwap.Series(input.Bars)[i];
        // σ=0은 이격을 σ 단위로 표현할 수 없는 상태다. 0으로 나누지 않고 warmup으로 남긴다(C2 warmup 규칙).
        if (point.Warmup || point.Vwap is not { } vwap || point.StdDev is not { } sigma || sigma <= 0)
            return TechniqueSignal.WarmingUp(name, ("vwap", point.Vwap), ("sigma", point.StdDev));

        var deviation = ((double)input.Bars[i].Close - vwap) / sigma;
        return TechniqueSignal.Create(name, ConfluenceMath.Tanh(deviation), 1,
            ("vwap", vwap), ("sigma", sigma), ("deviation", deviation));
    }

    // ── RVOL: tanh(RVOL20−1)×sign(당일 방향), 표본이 없으면 c=0 ──
    public static TechniqueSignal RelativeVolume(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.RelativeVolume;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);
        if (input.RelativeVolume is not { } rvol || !double.IsFinite(rvol))
            return TechniqueSignal.Missing(name);

        var sessionOpen = (double)input.Bars[0].Open;
        var change = (double)input.Bars[i].Close - sessionOpen;
        var direction = ConfluenceMath.Sign(change);
        return TechniqueSignal.Create(name, ConfluenceMath.Tanh(rvol - 1) * direction, 1,
            ("relativeVolume", rvol), ("direction", direction), ("sessionOpen", sessionOpen));
    }

    // ── ATR 채널: (Close−EMA20)/(k·ATR) 클램프, confidence 1.0 ──
    public static TechniqueSignal AtrChannel(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.AtrChannel;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var ema = Ema.Series(input.Bars, policy.AtrChannelEmaPeriod)[i];
        var atr = Atr(input, policy)[i];
        if (ema.Warmup || ema.Value is not { } basis || atr.Warmup || atr.Value is not { } range || range <= 0)
            return TechniqueSignal.WarmingUp(name);

        var width = policy.AtrChannelAtrFactor * range;
        return TechniqueSignal.Create(name, ConfluenceMath.Clamp(((double)input.Bars[i].Close - basis) / width), 1,
            ("ema", basis), ("atr", range), ("channelWidth", width));
    }

    // ── ORB(15): 레인지 상단 대비 위치, 개장 15분 구간이 완성되기 전엔 warmup ──
    public static TechniqueSignal OpeningRange(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.OpeningRange;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var boundary = input.SessionStart + policy.OpeningRangeSpan();
        var opening = input.Bars.Where(x => x.Start >= input.SessionStart && x.End <= boundary).ToArray();
        // 개장 15분 구간이 완료 봉으로 채워지고, 평가 봉이 그 구간을 지난 뒤에만 신호를 낸다(C6).
        if (opening.Length == 0 || opening[^1].End < boundary || input.Bars[i].End <= boundary)
            return TechniqueSignal.WarmingUp(name);

        var upper = (double)opening.Max(x => x.High);
        var lower = (double)opening.Min(x => x.Low);
        var atr = Atr(input, policy)[i].Value;
        // 스케일 분모는 레인지 폭이며, 폭이 0이면 ATR로 대체한다. 둘 다 없으면 위치를 정규화할 수 없다.
        var scale = upper - lower;
        if (scale <= 0) scale = atr is { } a && a > 0 ? a : 0;
        if (scale <= 0) return TechniqueSignal.WarmingUp(name, ("upper", upper), ("lower", lower));

        var close = (double)input.Bars[i].Close;
        var score = close > upper ? (close - upper) / scale : close < lower ? (close - lower) / scale : 0;
        return TechniqueSignal.Create(name, ConfluenceMath.Clamp(score), 1,
            ("upper", upper), ("lower", lower), ("scale", scale), ("close", close));
    }

    // ── RS vs QQQ: 당일 수익률 차 tanh(/ATR%), 벤치마크 시각 동기(±60초) 실패면 c=0 ──
    public static TechniqueSignal RelativeStrength(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.RelativeStrength;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var atr = Atr(input, policy)[i];
        var close = (double)input.Bars[i].Close;
        if (atr.Warmup || atr.Value is not { } range || range <= 0 || close <= 0)
            return TechniqueSignal.WarmingUp(name);

        var benchmark = Synchronized(input, policy, input.Bars[i].End);
        if (benchmark is null) return TechniqueSignal.Missing(name);

        var open = (double)input.Bars[0].Open;
        var benchmarkOpen = (double)input.BenchmarkBars[0].Open;
        if (open <= 0 || benchmarkOpen <= 0) return TechniqueSignal.Missing(name);

        var symbolReturn = (close / open - 1) * 100;
        var benchmarkReturn = ((double)benchmark.Close / benchmarkOpen - 1) * 100;
        var atrPercent = range / close * 100;
        if (atrPercent <= 0) return TechniqueSignal.WarmingUp(name);

        var difference = symbolReturn - benchmarkReturn;
        return TechniqueSignal.Create(name, ConfluenceMath.Tanh(difference / atrPercent), 1,
            ("symbolReturnPercent", symbolReturn), ("benchmarkReturnPercent", benchmarkReturn),
            ("atrPercent", atrPercent), ("differencePercent", difference));
    }

    // ── OBI: (Bid−Ask)/(Bid+Ask) 최근 3 poll 평균, 호가 결측이면 c=0 ──
    public static TechniqueSignal OrderBookImbalance(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.OrderBookImbalance;

        var window = input.OrderBook
            .Where(x => double.IsFinite(x.BidVolume) && double.IsFinite(x.AskVolume) &&
                        x.BidVolume >= 0 && x.AskVolume >= 0 && x.BidVolume + x.AskVolume > 0)
            .TakeLast(Math.Max(1, policy.OrderBookPollWindow))
            .ToArray();
        if (window.Length == 0) return TechniqueSignal.Missing(name, ("samples", 0));

        var sum = window.Sum(x => (x.BidVolume - x.AskVolume) / (x.BidVolume + x.AskVolume));
        return TechniqueSignal.Create(name, sum / window.Length, 1, ("samples", window.Length));
    }

    static int? Last(ConfluenceInput input) => input.Bars.Length == 0 ? null : input.Bars.Length - 1;

    static ImmutableArray<AtrPoint> Atr(ConfluenceInput input, ConfluencePolicy policy) =>
        AverageTrueRange.Series(input.Bars, input.PreviousSessionClose, policy.AtrPeriod);

    /// <summary>평가 봉과 ±허용 오차 안에서 끝나는 벤치마크 완료 봉. 없으면 null이며 신호는 c=0이다.</summary>
    static IndicatorBar? Synchronized(ConfluenceInput input, ConfluencePolicy policy, DateTimeOffset barEnd)
    {
        if (input.BenchmarkBars.Length == 0) return null;
        var tolerance = policy.BenchmarkSyncTolerance();
        IndicatorBar? best = null;
        var bestGap = TimeSpan.MaxValue;
        foreach (var bar in input.BenchmarkBars)
        {
            var gap = (bar.End - barEnd).Duration();
            if (gap > tolerance || gap >= bestGap) continue;
            best = bar;
            bestGap = gap;
        }
        return best;
    }

    /// <summary>밴드폭이 창 안에서 최저(스퀴즈) 또는 최고인가. 창을 채울 밴드폭이 없으면 false다.</summary>
    static bool IsBandWidthExtreme(ImmutableArray<BollingerPoint> bands, int index, ConfluencePolicy policy,
        bool lowest)
    {
        if (index < 0 || bands[index].BandWidth is not { } current) return false;
        var window = new List<double>(policy.SqueezeLookbackBars);
        for (var k = index; k >= 0 && window.Count < policy.SqueezeLookbackBars; k--)
            if (bands[k].BandWidth is { } value) window.Add(value);
        if (window.Count < policy.SqueezeLookbackBars) return false;
        return lowest ? current <= window.Min() : current >= window.Max();
    }
}
