namespace Astra.Server;

public static class Indicators
{
    public static double Ema(IReadOnlyList<double> values, int period)
    {
        if (values.Count == 0 || period < 1) throw new ArgumentException("EMA requires observations and a positive period.");
        var alpha = 2d / (period + 1); var value = values[0];
        for (var i = 1; i < values.Count; i++) value = alpha * values[i] + (1 - alpha) * value;
        return value;
    }

    public static double[] EmaSeries(IReadOnlyList<double> values, int period)
    {
        if (values.Count == 0 || period < 1) throw new ArgumentException("EMA requires observations and a positive period.");
        var alpha = 2d / (period + 1); var series = new double[values.Count]; series[0] = values[0];
        for (var i = 1; i < values.Count; i++) series[i] = alpha * values[i] + (1 - alpha) * series[i - 1];
        return series;
    }

    public static double[] VwapSeries(IReadOnlyList<Candle> candles)
    {
        var series = new double[candles.Count]; double pv = 0, vol = 0;
        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i]; pv += (c.High + c.Low + c.Close) / 3 * c.Volume; vol += c.Volume;
            series[i] = vol > 0 ? pv / vol : c.Close;
        }
        return series;
    }

    /// <summary>눌림 후 반등 진입 셋업 감지. 최근 8봉 내 EMA9/VWAP 존 터치(단, VWAP를 0.5 ATR 넘게 깨면 무효) 후 마지막 봉이 직전 봉 고점을 상향 돌파하면 "SETUP". VWAP 대비 2.5 ATR 초과 과확장은 "CHASE".</summary>
    public static string? DetectSetup(IReadOnlyList<Candle> candles, IndicatorSnapshot ind, int score)
    {
        if (candles.Count < 12 || ind.Atr <= 0) return null;
        var last = candles[^1];
        if ((last.Close - ind.Vwap) / Math.Max(ind.VwapSd, ind.Atr) > 2.5) return "CHASE";
        if (last.Close <= candles[^2].High) return null;
        var closes = candles.Select(x => x.Close).ToArray();
        // 과매도 반등: 최근 5봉 내 RSI 30 이하를 찍었고 아직 회복 초입(RSI<50)에서 직전 봉 고점을 돌파 — 점수와 무관하게 유효
        if (ind.Rsi < 50)
            for (var k = 1; k <= 5 && candles.Count - k > 15; k++)
                if (Rsi(closes[..(candles.Count - k)]) <= 30) return "REBOUND";
        if (score < 70) return null;
        // 1차 반복 개선(2026-09-09, 청산 10건 분석): 손실 셋업은 σ 1.6+·RSI 65+ 자리에서 진입했음 → 과확장·과열 자리의 셋업은 무효 처리
        if ((last.Close - ind.Vwap) / Math.Max(ind.VwapSd, ind.Atr) > 1.5 || ind.Rsi >= 65) return null;
        var ema9 = EmaSeries(closes, 9); var vwap = VwapSeries(candles);
        var touched = false;
        for (var j = Math.Max(1, candles.Count - 9); j < candles.Count - 1; j++)
        {
            if (candles[j].Low < vwap[j] - .5 * ind.Atr) return null;
            if (candles[j].Low <= Math.Max(ema9[j], vwap[j]) + .1 * ind.Atr) touched = true;
        }
        return touched ? "SETUP" : null;
    }

    public static double Rsi(IReadOnlyList<double> values, int period = 14)
    {
        if (values.Count <= period) throw new ArgumentException("RSI has insufficient observations.");
        double gain = 0, loss = 0;
        for (var i = 1; i <= period; i++) { var d = values[i] - values[i - 1]; gain += Math.Max(d, 0); loss += Math.Max(-d, 0); }
        gain /= period; loss /= period;
        for (var i = period + 1; i < values.Count; i++) { var d = values[i] - values[i - 1]; gain = ((period - 1) * gain + Math.Max(d, 0)) / period; loss = ((period - 1) * loss + Math.Max(-d, 0)) / period; }
        return loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
    }

    public static (double Lower, double Upper) Bollinger(IReadOnlyList<double> values, int period = 20, double k = 2)
    {
        if (values.Count < period) throw new ArgumentException("Bollinger bands have insufficient observations.");
        var x = values.Skip(values.Count - period).ToArray(); var mean = x.Average();
        var sd = Math.Sqrt(x.Sum(v => Math.Pow(v - mean, 2)) / (x.Length - 1));
        return (mean - k * sd, mean + k * sd);
    }

    public static double Vwap(IReadOnlyList<Candle> candles)
    {
        var volume = candles.Sum(x => x.Volume); if (volume <= 0) return candles.LastOrDefault()?.Close ?? 0;
        return candles.Sum(x => ((x.High + x.Low + x.Close) / 3) * x.Volume) / volume;
    }

    /// <summary>VWAP 주변 가격의 거래량 가중 표준편차. VWAP 이격을 세션 스케일로 정규화하는 단위(VWAP 밴드 σ)로 쓴다.</summary>
    public static double VwapStd(IReadOnlyList<Candle> candles, double vwap)
    {
        var volume = candles.Sum(x => x.Volume); if (volume <= 0) return 0;
        return Math.Sqrt(candles.Sum(x => { var d = (x.High + x.Low + x.Close) / 3 - vwap; return d * d * x.Volume; }) / volume);
    }

    public static double Atr(IReadOnlyList<Candle> candles, int period = 14)
    {
        if (candles.Count <= period) throw new ArgumentException("ATR has insufficient observations.");
        var tr = new List<double>();
        for (var i = 1; i < candles.Count; i++) tr.Add(Math.Max(candles[i].High - candles[i].Low, Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close), Math.Abs(candles[i].Low - candles[i - 1].Close))));
        var atr = tr.Take(period).Average(); for (var i = period; i < tr.Count; i++) atr = ((period - 1) * atr + tr[i]) / period; return atr;
    }

    /// <summary>강도 비례 점수: 각 조건이 고정 가점이 아니라 ATR로 정규화한 크기에 비례해 기여한다. VWAP에서 2 ATR을 넘는 과확장은 추격 페널티로 다시 깎여 점수가 중립으로 되돌아간다.</summary>
    public static SignalResult Evaluate(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 30) throw new ArgumentException("At least 30 completed candles are required.");
        var closes = candles.Select(x => x.Close).ToArray(); var last = candles[^1];
        var fast = Ema(closes, 9); var slow = Ema(closes, 21); var rsi = Rsi(closes); var bands = Bollinger(closes);
        var vwap = Vwap(candles); var atr = Atr(candles); var avgVolume = candles.Skip(Math.Max(0, candles.Count - 21)).Take(20).Average(x => x.Volume);
        var rv = avgVolume <= 0 ? 0 : last.Volume / avgVolume;
        var unit = atr > 0 ? atr : Math.Max(last.Close * .001, 1e-9);
        double score = 50; var reasons = new List<string>();

        var emaGap = (fast - slow) / unit;
        score += Math.Clamp(emaGap / 1.5, -1, 1) * 15;
        reasons.Add(emaGap >= 0 ? $"단기 EMA가 장기 EMA 위 (+{emaGap:0.0} ATR)" : $"단기 EMA가 장기 EMA 아래 ({emaGap:0.0} ATR)");

        var vwapSd = VwapStd(candles, vwap);
        var extUnit = Math.Max(vwapSd, unit);
        var ext = (last.Close - vwap) / extUnit;
        score += Math.Clamp(ext, -1, 1) * 12;
        reasons.Add(ext >= 0 ? $"가격이 VWAP 위 (+{ext:0.0}σ)" : $"가격이 VWAP 아래 ({ext:0.0}σ)");

        var rsiPts = rsi >= 75 ? -10 * Math.Min(1, (rsi - 75) / 10)
            : rsi > 68 ? 10 * (75 - rsi) / 7
            : rsi >= 50 ? 10 * (rsi - 50) / 18
            : rsi <= 30 ? 4 : 0;
        score += rsiPts;
        if (rsi >= 75) reasons.Add($"RSI 과열 구간 ({rsi:0})");
        else if (rsi >= 50) reasons.Add($"RSI 상승 모멘텀 구간 ({rsi:0})");
        else if (rsi <= 30) reasons.Add($"RSI 과매도 구간 ({rsi:0})");

        if (rv > 1)
        {
            score += Math.Min(1, rv - 1) * 10 * (last.Close >= vwap ? 1 : -1);
            if (rv >= 1.2) reasons.Add($"상대 거래량 {rv:0.0}×");
        }

        if (last.Close <= bands.Lower) { score += 5; reasons.Add("볼린저 하단 접근"); }
        else if (last.Close >= bands.Upper) { score -= 5; reasons.Add("볼린저 상단 이탈"); }

        // MACD(12,26,9) 교차가 최근 5봉 안에 있으면 반영
        var e12 = EmaSeries(closes, 12); var e26 = EmaSeries(closes, 26);
        var macd = new double[closes.Length];
        for (var i = 0; i < closes.Length; i++) macd[i] = e12[i] - e26[i];
        var macdSignal = EmaSeries(macd, 9);
        for (var i = closes.Length - 1; i >= Math.Max(1, closes.Length - 5); i--)
        {
            if (macd[i] > macdSignal[i] && macd[i - 1] <= macdSignal[i - 1]) { score += 4; reasons.Add("MACD 골든크로스"); break; }
            if (macd[i] < macdSignal[i] && macd[i - 1] >= macdSignal[i - 1]) { score -= 4; reasons.Add("MACD 데드크로스"); break; }
        }

        if (Math.Abs(ext) > 2)
        {
            score -= Math.Min(1, Math.Abs(ext) - 2) * 12 * Math.Sign(ext);
            reasons.Add(ext > 0 ? $"VWAP 대비 +{ext:0.0}σ 과확장 · 추격 주의" : $"VWAP 대비 {ext:0.0}σ 과확장 · 급락 추격 주의");
        }

        var final = (int)Math.Clamp(Math.Round(score), 0, 100);
        var action = final >= 70 ? "BUY" : final <= 30 ? "SELL" : "WATCH";
        return new(final, action, reasons.ToArray(), new(rsi, fast, slow, vwap, atr, rv, bands.Lower, bands.Upper, vwapSd));
    }
}
