namespace Astra.Server;

public sealed record PriceLevel(double Price, string Label);

/// <summary>당일 1분봉의 볼륨 프로파일(매물대)·거래량 급증 구간·당일 고저와 일봉의 전일 고저/종가·20일 고저로 지지/저항 레벨을 만들고, 손절은 지지 아래·목표가는 저항에 붙인다. ATR은 버퍼와 거리 상한으로만 쓴다.</summary>
public static class PriceLevels
{
    public static List<PriceLevel> Compute(IReadOnlyList<Candle> intraday, IReadOnlyList<Candle>? daily, double atr)
    {
        var levels = new List<PriceLevel>();
        if (intraday.Count > 0)
        {
            levels.Add(new(intraday[0].Open, "당일 시가"));
            levels.Add(new(intraday.Max(x => x.High), "당일 고가"));
            levels.Add(new(intraday.Min(x => x.Low), "당일 저가"));
            var lo = intraday.Min(x => x.Low); var hi = intraday.Max(x => x.High);
            if (hi > lo && atr > 0)
            {
                var bin = Math.Max(atr * .4, (hi - lo) / 40);
                var bins = new Dictionary<int, double>();
                foreach (var c in intraday)
                {
                    var key = (int)(((c.High + c.Low + c.Close) / 3 - lo) / bin);
                    bins[key] = bins.GetValueOrDefault(key) + c.Volume;
                }
                var poc = bins.MaxBy(x => x.Value);
                levels.Add(new(lo + (poc.Key + .5) * bin, "매물대 집중"));
                foreach (var kv in bins.Where(x => x.Key != poc.Key && x.Value >= poc.Value * .65).OrderByDescending(x => x.Value).Take(2))
                    levels.Add(new(lo + (kv.Key + .5) * bin, "매물대"));
            }
            var avgVolume = intraday.Average(x => x.Volume);
            if (avgVolume > 0)
                foreach (var c in intraday.Where(x => x.Volume >= avgVolume * 3).OrderByDescending(x => x.Volume).Take(3))
                    levels.Add(new((c.High + c.Low + c.Close) / 3, "거래량 급증 구간"));
            if (intraday.Count >= 15)
            {
                var opening = intraday.Take(15).ToArray();
                levels.Add(new(opening.Max(x => x.High), "개장 15분 고가"));
                levels.Add(new(opening.Min(x => x.Low), "개장 15분 저가"));
            }
        }
        if (daily is { Count: > 0 } && intraday.Count > 0)
        {
            var sessionDate = MarketRules.TradingDate(intraday.Max(x => x.Timestamp));
            var history = daily.Where(x => MarketRules.TradingDate(x.Timestamp) < sessionDate).OrderBy(x => x.Timestamp).TakeLast(20).ToArray();
            if (history.Length > 0)
            {
                var prev = history[^1];
                levels.Add(new(prev.High, "전일 고가"));
                levels.Add(new(prev.Low, "전일 저가"));
                levels.Add(new(prev.Close, "전일 종가"));
                levels.Add(new(history.Max(x => x.High), "20일 고가"));
                levels.Add(new(history.Min(x => x.Low), "20일 저가"));
            }
        }
        var merged = new List<PriceLevel>();
        foreach (var level in levels.Where(x => x.Price > 0).OrderBy(x => x.Price))
            if (merged.Count > 0 && atr > 0 && level.Price - merged[^1].Price < atr * .3)
            { if (Rank(level.Label) > Rank(merged[^1].Label)) merged[^1] = level; }
            else merged.Add(level);
        return merged;
    }

    static int Rank(string label) => label.StartsWith("매물대") ? 3 : label.StartsWith("전일") || label.StartsWith("20일") || label.StartsWith("거래량") || label.StartsWith("개장") ? 2 : 1;

    /// <summary>직전 봉 종가는 레벨 아래, 마지막 봉이 거래량(20봉 평균 1.5배↑)을 싣고 레벨 위에서 마감하면 저항 돌파로 본다. 돌파한 레벨 중 가장 높은 것을 반환.</summary>
    public static PriceLevel? DetectBreakout(IReadOnlyList<Candle> bars, IReadOnlyList<PriceLevel> levels, double relativeVolume)
    {
        if (bars.Count < 2 || relativeVolume < 1.5) return null;
        var prev = bars[^2]; var last = bars[^1];
        if (last.Close <= last.Open) return null;
        return levels.Where(l => prev.Close <= l.Price && last.Close > l.Price)
            .OrderByDescending(l => l.Price).FirstOrDefault();
    }

    public static Position Enter(double entry, double quantity, double atr, IReadOnlyList<PriceLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (!double.IsFinite(entry) || entry <= .01) throw new ArgumentOutOfRangeException(nameof(entry));
        if (!double.IsFinite(quantity) || quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!double.IsFinite(atr) || atr <= 0) throw new ArgumentOutOfRangeException(nameof(atr));
        // 1차 반복 개선: 너무 가까운 지지선 스톱(0.4% 미만)이 손절의 절반을 만들었음 → 최소 손절 거리(1.2 ATR·진입가 0.35%)를 만족하는 지지선만 사용
        var minRisk = Math.Max(atr * 1.5, entry * .0045);
        var support = levels.Where(x => x.Price <= entry - atr * .5 && entry - x.Price <= atr * 5 && entry - (x.Price - atr * .5) >= minRisk)
            .OrderByDescending(x => x.Price).FirstOrDefault();
        double stop; string stopBasis;
        if (support is not null) { stop = support.Price - atr * .5; stopBasis = $"{support.Label} {support.Price:0.##} 아래"; }
        // 3차 반복 개선: 장 후반 ATR 축소 시 폴백 손절이 0.1%대까지 좁아지는 버그 → 폴백에도 최소 손절폭 적용
        else { stop = entry - Math.Max(atr * 2.5, minRisk); stopBasis = atr * 2.5 >= minRisk ? "2.5 ATR" : "최소 손절폭"; }
        stop = Math.Clamp(stop, .01, Math.Max(.01, entry - .01));
        var risk = entry - stop;
        // 목표 최소 거리: 왕복 수수료 0.2%를 확실히 넘도록 진입가의 0.5% 이상
        var minMove = Math.Max(.01, Math.Max(atr * 1.2, entry * .005));
        var resistance = levels.Where(x => x.Price >= entry + minMove && x.Price - entry <= atr * 10)
            .OrderBy(x => x.Price).FirstOrDefault();
        double target; string targetBasis;
        if (resistance is not null) { target = resistance.Price; targetBasis = $"{resistance.Label} {resistance.Price:0.##}"; }
        else
        {
            var maxMove = Math.Max(atr * 10, minMove);
            var fallbackMove = levels.Count == 0 ? atr * 3 : risk * 1.5;
            var desiredMove = Math.Max(fallbackMove, minMove);
            target = entry + Math.Min(desiredMove, maxMove);
            targetBasis = desiredMove > maxMove ? "목표 거리 상한" : levels.Count == 0 && atr * 3 >= minMove ? "3 ATR" : levels.Count == 0 ? "최소 목표폭" : "손절폭 1.5배";
        }
        var roundedStop = Math.Floor(stop * 100) / 100;
        var roundedTarget = Math.Ceiling(target * 100) / 100;
        if (!(roundedStop > 0 && roundedStop < entry && roundedTarget > entry)) throw new InvalidOperationException("유효한 손절/목표 가격을 산정할 수 없습니다.");
        return new(entry, quantity, roundedTarget, roundedStop, targetBasis, stopBasis);
    }
}
