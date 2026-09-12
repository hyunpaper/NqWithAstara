using System.Collections.Immutable;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Confluence;

/// <summary>
/// C3-2 기법 어댑터 2군 6개 (#170). 1군과 같은 계약 — 완료 봉만 읽는 순수 함수이고 표준 파라미터는 고정이며
/// 전부 w=1.0 미검증으로 편입한다. 진입 판정에는 쓰이지 않는다(C1 1단계).
/// </summary>
public static partial class ConfluenceTechniques
{
    /// <summary>2군 6개를 평가 대상 봉 기준으로 계산한다.</summary>
    public static ImmutableArray<TechniqueSignal> Tier2(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        return
        [
            Candle(input, policy)
        ];
    }

    // ── 캔들 확인: 선행 하락(직전 5봉 저점 갱신) 위의 강세 장악형 +0.6 / 망치 +0.5 / 핀바 +0.4 ──
    public static TechniqueSignal Candle(ConfluenceInput input, ConfluencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var name = TechniqueNames.Candle;
        if (Last(input) is not { } i) return TechniqueSignal.WarmingUp(name);

        var lookback = Math.Max(1, policy.CandlePriorLowLookbackBars);
        var atr = Atr(input, policy)[i];
        if (i < lookback || atr.Warmup || atr.Value is not { } range || range <= 0)
            return TechniqueSignal.WarmingUp(name);

        var priorLow = LowestLow(input.Bars, i - lookback, i - 1);
        var newLow = (double)input.Bars[i].Low < priorLow;
        var engulfing = CandlePatterns.Engulfing(input.Bars)[i] > 0;
        var hammer = CandlePatterns.Hammer(input.Bars)[i] > 0;
        var pinBar = CandlePatterns.BullishPinBar(input.Bars)[i] > 0;

        var score = !newLow ? 0
            : engulfing ? policy.CandleEngulfingScore
            : hammer ? policy.CandleHammerScore
            : pinBar ? policy.CandlePinBarScore
            : 0;
        var body = CandlePatterns.RealBody(input.Bars[i]);
        var confidence = body >= policy.CandleBodyAtrRatio * range ? 1 : policy.CandleWeakBodyConfidence;
        return TechniqueSignal.Create(name, score, confidence,
            ("engulfing", engulfing ? 1 : 0), ("hammer", hammer ? 1 : 0), ("pinBar", pinBar ? 1 : 0),
            ("priorLow", priorLow), ("newLow", newLow ? 1 : 0), ("bodyAtr", body / range));
    }

    /// <summary>구간 [from, to]의 최저 저가. 호출부가 구간을 보장한다.</summary>
    static double LowestLow(ImmutableArray<IndicatorBar> bars, int from, int to)
    {
        var lowest = (double)bars[from].Low;
        for (var i = from + 1; i <= to; i++) lowest = Math.Min(lowest, (double)bars[i].Low);
        return lowest;
    }
}
