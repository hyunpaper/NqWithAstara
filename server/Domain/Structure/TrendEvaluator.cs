using System.Collections.Immutable;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D2 — 설계 §7 + §16B 추세 평가.
// 방향 강도와 진입 위치 품질을 분리한다. 여기서 만드는 숫자는 순위·설명용이며 승률이 아니다.
// 고정 가점을 더하지 않고 tanh로 연속 변환하며, 상관된 가격 파생 항목은 하나의 family로 묶는다.

/// <summary>설계 §3/§7 추세 상태 label. 숫자와 label을 둘 다 노출한다.</summary>
public enum TrendState { Unknown, Up, Down, Range, Transition }

/// <summary>추세 구성요소의 원값과 변환값(§9.4의 저장 규칙을 추세에도 적용).</summary>
public sealed record TrendComponent(string Name, string Family, double? Raw, double? Value);

/// <summary>추세 평가 입력. Domain은 현재 시각을 다시 읽지 않고 cutoff를 명시적으로 받는다(§4).</summary>
public sealed record TrendRequest(string Symbol, DateTimeOffset SessionStart, DateTimeOffset AnalysisCutoff,
    ImmutableArray<StructureBar> OneMinuteBars, ImmutableArray<StructureBar> FiveMinuteBars)
{
    public static TrendRequest Create(string symbol, DateTimeOffset sessionStart, DateTimeOffset analysisCutoff,
        ImmutableArray<StructureBar> oneMinute, ImmutableArray<StructureBar>? fiveMinute = null) =>
        new(symbol, sessionStart, analysisCutoff, oneMinute, fiveMinute ?? ImmutableArray<StructureBar>.Empty);
}

/// <summary>
/// 설계 §3.1 TrendAssessment. SignedTrend는 -100~+100이고 결측이면 null이다(0으로 대체하지 않는다).
/// </summary>
public sealed record TrendAssessment(TrendState State, double? SignedTrend, double? PriceDirection,
    double? StructureDirection, double? Efficiency, double? Atr1m, double? Ema9, double? Ema21,
    double? Vwap, double? VwapSd, bool StructureEvidenceMissing, int BarCount,
    DateTimeOffset AnalysisCutoff, ImmutableArray<TrendComponent> Components,
    ImmutableArray<string> UsedFamilies, ImmutableArray<string> MissingComponents,
    ImmutableArray<string> Warnings, ImmutableArray<string> BlockersForTrend,
    ImmutableArray<string> BlockersForReady)
{
    public bool Available => State != TrendState.Unknown && SignedTrend is not null;

    /// <summary>결정성 비교용 canonical 표현. record 자동 Equals는 배열 참조를 비교한다(D1 결정 14).</summary>
    public string Fingerprint() => string.Join('|', State.ToString(), StructureMath.Number(SignedTrend),
        StructureMath.Number(PriceDirection), StructureMath.Number(StructureDirection),
        StructureMath.Number(Efficiency), StructureMath.Number(Atr1m), StructureMath.Number(Ema9),
        StructureMath.Number(Ema21), StructureMath.Number(Vwap), StructureMath.Number(VwapSd),
        StructureEvidenceMissing ? "1" : "0", BarCount, StructureMath.Iso(AnalysisCutoff),
        string.Join(',', Components.Select(x => $"{x.Name}:{StructureMath.Number(x.Raw)}:{StructureMath.Number(x.Value)}")),
        string.Join(',', UsedFamilies), string.Join(',', MissingComponents),
        string.Join(',', Warnings), string.Join(',', BlockersForTrend), string.Join(',', BlockersForReady));
}

/// <summary>
/// 설계 §16B 지표 정의. 정규장 세션에서 초기화하고 미래 봉을 절대 보지 않는다.
/// ATR14는 D1이 소유하므로(<see cref="SessionAtr"/>) 여기서 다시 만들지 않는다(D1 결정 15).
/// </summary>
public static class SessionIndicators
{
    /// <summary>첫 완료 Close를 seed로 alpha=2/(period+1). 최초 세션 봉부터 순서대로 계산한다(§16B).</summary>
    public static ImmutableArray<double?> EmaSeries(IReadOnlyList<StructureBar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var values = new double?[bars.Count];
        if (bars.Count == 0) return values.ToImmutableArray();
        var alpha = 2.0 / (period + 1);
        var ema = (double)bars[0].Close;
        values[0] = ema;
        for (var i = 1; i < bars.Count; i++)
        {
            ema += alpha * ((double)bars[i].Close - ema);
            values[i] = double.IsFinite(ema) ? ema : null;
        }
        return values.ToImmutableArray();
    }

    /// <summary>typical price=(H+L+C)/3의 세션 누적 거래량 가중 평균과 같은 가중 population 표준편차. 거래량 합 0이면 둘 다 null(§16B).</summary>
    public static (double? Vwap, double? Sd) Vwap(IReadOnlyList<StructureBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        double weight = 0, weighted = 0;
        foreach (var bar in bars)
        {
            if (bar.Volume <= 0 || !double.IsFinite(bar.Volume)) continue;
            weight += bar.Volume;
            weighted += bar.Volume * Typical(bar);
        }
        if (weight <= 0 || !double.IsFinite(weight)) return (null, null);
        var vwap = weighted / weight;
        double variance = 0;
        foreach (var bar in bars)
        {
            if (bar.Volume <= 0 || !double.IsFinite(bar.Volume)) continue;
            var d = Typical(bar) - vwap;
            variance += bar.Volume * d * d;
        }
        variance /= weight;
        return double.IsFinite(vwap) && double.IsFinite(variance) ? (vwap, Math.Sqrt(variance)) : (null, null);
    }

    static double Typical(StructureBar bar) => ((double)bar.High + (double)bar.Low + (double)bar.Close) / 3;

    /// <summary>
    /// §16B 효율성: (lookback+1)개 Close의 처음/마지막 차이를 lookback개 절대 차이 합으로 나눈다.
    /// 분모 0이면 0이다(NaN을 만들지 않는다). 봉이 부족하면 null이다.
    /// </summary>
    public static double? Efficiency(IReadOnlyList<StructureBar> bars, int lookbackBars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (lookbackBars < 1) throw new ArgumentOutOfRangeException(nameof(lookbackBars));
        if (bars.Count < lookbackBars + 1) return null;
        var window = bars.Skip(bars.Count - lookbackBars - 1).ToArray();
        double sum = 0;
        for (var i = 1; i < window.Length; i++) sum += Math.Abs((double)window[i].Close - (double)window[i - 1].Close);
        if (sum <= 0) return 0;
        var net = Math.Abs((double)window[^1].Close - (double)window[0].Close);
        var value = net / sum;
        return double.IsFinite(value) ? value : 0;
    }

    /// <summary>
    /// §16B 상대 거래량: 트리거 거래량 / 직전 lookback개 완료 봉 평균. 트리거는 분모에서 제외한다.
    /// 분모 0 또는 봉 부족이면 null이며 신규 진입은 보류한다(§16A NO_VOLUME_BASELINE).
    /// 트리거 거래량 0과 양수 분모는 RV=0이다.
    /// </summary>
    public static double? RelativeVolume(IReadOnlyList<StructureBar> bars, DateTimeOffset triggerBarStart, int lookbackBars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (lookbackBars < 1) throw new ArgumentOutOfRangeException(nameof(lookbackBars));
        var trigger = bars.FirstOrDefault(x => x.Start == triggerBarStart);
        if (trigger is null) return null;
        var baseline = bars.Where(x => x.Start < triggerBarStart).OrderBy(x => x.Start).TakeLast(lookbackBars).ToArray();
        if (baseline.Length < lookbackBars) return null;
        var average = baseline.Average(x => x.Volume);
        if (average <= 0 || !double.IsFinite(average)) return null;
        var value = trigger.Volume / average;
        return double.IsFinite(value) && value >= 0 ? value : null;
    }
}

/// <summary>
/// 설계 §7 + §16B. 상관된 ema/slope/vwap을 priceDirection 한 family로 묶고, 확정 피벗의 구조 family를 더한다.
/// family 수를 늘려 가중치를 몰래 바꾸지 않으며, ATR&lt;=0/필수 지표 결측이면 추세는 null(UNKNOWN)이다.
/// </summary>
public static class TrendEvaluator
{
    public const string BlockerTrendUnavailable = "TREND_UNAVAILABLE";
    public const string BlockerMissing5mStructure = "MISSING_5M_STRUCTURE";
    public const string WarningInsufficientBars = "INSUFFICIENT_1M_BARS";
    public const string WarningAtrUnavailable = "ATR_UNAVAILABLE";
    public const string WarningVwapUnavailable = "VWAP_UNAVAILABLE";
    public const string WarningEfficiencyUnavailable = "EFFICIENCY_UNAVAILABLE";
    public const string WarningFutureBars = "FUTURE_BAR_INPUT_REJECTED";

    public static TrendAssessment Evaluate(TrendRequest request, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        // D1 결정 16과 동일하게 cutoff 초과 입력은 예외 대신 잘라내고 경고를 남긴다.
        var bars = Truncate(request.OneMinuteBars, request.AnalysisCutoff, warnings);
        var bars5m = Truncate(request.FiveMinuteBars, request.AnalysisCutoff, warnings);

        var atrSeries = SessionAtr.Series(bars, policy);
        var atr = bars.Length == 0 ? null : atrSeries[^1];
        var ema9 = SessionIndicators.EmaSeries(bars, policy.EmaFastPeriod);
        var ema21 = SessionIndicators.EmaSeries(bars, policy.EmaSlowPeriod);
        var (vwap, vwapSd) = SessionIndicators.Vwap(bars);
        var efficiency = SessionIndicators.Efficiency(bars, policy.EfficiencyLookbackBars);

        // §7 구조 family: 확정 피벗의 최신 delta. 5분 피벗은 우측 확인에 10분이 필요하다(§5.3).
        var pivots5m = PivotDetector.Detect(request.Symbol, request.SessionStart, BarTimeframe.FiveMinute,
            bars5m, request.AnalysisCutoff, policy);
        var (structureDirection, deltaHigh, deltaLow) = StructureFamily(pivots5m, atr);

        var enoughBars = bars.Length >= policy.Minimum1mBars;
        if (!enoughBars) warnings.Add(WarningInsufficientBars);
        if (atr is null or <= 0 || !double.IsFinite(atr ?? double.NaN)) { warnings.Add(WarningAtrUnavailable); missing.Add("atr1m"); }
        if (vwap is null) { warnings.Add(WarningVwapUnavailable); missing.Add("vwap"); }
        if (efficiency is null) { warnings.Add(WarningEfficiencyUnavailable); missing.Add("efficiency"); }

        double? emaDirection = null, slopeDirection = null, vwapDirection = null, priceDirection = null;
        double? emaRaw = null, slopeRaw = null, vwapRaw = null;
        var usableAtr = atr is { } a && double.IsFinite(a) && a > 0 ? a : (double?)null;

        if (usableAtr is { } atrValue && bars.Length > 0)
        {
            if (ema9[^1] is { } fast && ema21[^1] is { } slow)
            {
                emaRaw = (fast - slow) / atrValue;
                emaDirection = Math.Tanh(emaRaw.Value);
            }
            else missing.Add("emaDirection");

            var slopeIndex = bars.Length - 1 - policy.TrendSlopeLookbackBars;
            if (slopeIndex >= 0 && ema21[^1] is { } now && ema21[slopeIndex] is { } before)
            {
                slopeRaw = (now - before) / (policy.TrendSlopeLookbackBars * atrValue);
                slopeDirection = Math.Tanh(slopeRaw.Value);
            }
            else missing.Add("slopeDirection");

            if (vwap is { } vwapValue)
            {
                var denominator = Math.Max(Math.Max(vwapSd ?? 0, atrValue), policy.IndicatorFloor);
                vwapRaw = ((double)bars[^1].Close - vwapValue) / denominator;
                vwapDirection = Math.Tanh(vwapRaw.Value);
            }
            else missing.Add("vwapDirection");
        }
        else
        {
            missing.Add("emaDirection");
            missing.Add("slopeDirection");
            missing.Add("vwapDirection");
        }

        // §7 가격 파생 세 항목은 서로 상관돼 있으므로 하나의 family로 묶는다.
        if (emaDirection is { } e && slopeDirection is { } s && vwapDirection is { } v)
        {
            var mean = (e + s + v) / 3;
            if (double.IsFinite(mean)) priceDirection = mean;
        }

        var components = ImmutableArray.Create(
            new TrendComponent("emaDirection", "price", emaRaw, emaDirection),
            new TrendComponent("slopeDirection", "price", slopeRaw, slopeDirection),
            new TrendComponent("vwapDirection", "price", vwapRaw, vwapDirection),
            new TrendComponent("structureDirection", "structure", deltaHigh is null && deltaLow is null ? null : (deltaHigh ?? 0) + (deltaLow ?? 0), structureDirection),
            new TrendComponent("efficiency", "path", efficiency, efficiency));

        var structureMissing = structureDirection is null;
        if (structureMissing) missing.Add("structureDirection");

        var blockersForTrend = new SortedSet<string>(StringComparer.Ordinal);
        var blockersForReady = new SortedSet<string>(StringComparer.Ordinal);

        // §16B: ATR<=0/필수 지표 결측이면 trend=null. 30봉 미만도 UNKNOWN이다(§7).
        if (!enoughBars || priceDirection is null || efficiency is null)
        {
            blockersForTrend.Add(BlockerTrendUnavailable);
            blockersForReady.Add(BlockerTrendUnavailable);
            // 5m 구조 결측은 그 자체로 READY 차단 사유다(§16B).
            if (structureMissing) blockersForReady.Add(BlockerMissing5mStructure);
            return new TrendAssessment(TrendState.Unknown, null, priceDirection, structureDirection, efficiency,
                atr, ema9.Length == 0 ? null : ema9[^1], ema21.Length == 0 ? null : ema21[^1], vwap, vwapSd,
                structureMissing, bars.Length, request.AnalysisCutoff, components,
                ImmutableArray<string>.Empty, missing.ToImmutableArray(), warnings.ToImmutableArray(),
                blockersForTrend.ToImmutableArray(), blockersForReady.ToImmutableArray());
        }

        var used = ImmutableArray.CreateBuilder<string>();
        used.Add("price");
        double signed;
        if (structureDirection is { } structure)
        {
            used.Add("structure");
            signed = 100 * (priceDirection.Value + structure) / 2;
        }
        else
        {
            // §7: 구조 family가 없으면 가격 family만으로 표시하고 StructureEvidenceMissing을 남긴다.
            signed = 100 * priceDirection.Value;
            blockersForReady.Add(BlockerMissing5mStructure);
        }

        var state = State(signed, priceDirection.Value, structureDirection, efficiency.Value, policy);

        return new TrendAssessment(state, signed, priceDirection, structureDirection, efficiency, atr,
            ema9[^1], ema21[^1], vwap, vwapSd, structureMissing, bars.Length, request.AnalysisCutoff, components,
            used.ToImmutable(), missing.ToImmutableArray(), warnings.ToImmutableArray(),
            blockersForTrend.ToImmutableArray(), blockersForReady.ToImmutableArray());
    }

    /// <summary>
    /// §16B: efficiency&lt;임계값이면 RANGE. 그 외 임계값을 만족하고 두 family 부호가 반대가 아닐 때만 UP/DOWN이며
    /// 나머지는 TRANSITION이다. 부호는 수학적 sign이고 0은 반대 부호가 아니다.
    /// </summary>
    static TrendState State(double signedTrend, double priceDirection, double? structureDirection, double efficiency,
        StructurePolicy policy)
    {
        if (efficiency < policy.TrendEfficiencyThreshold) return TrendState.Range;
        var opposed = structureDirection is { } structure &&
                      Math.Sign(priceDirection) * Math.Sign(structure) < 0;
        if (!opposed && signedTrend >= policy.TrendStateThreshold) return TrendState.Up;
        if (!opposed && signedTrend <= -policy.TrendStateThreshold) return TrendState.Down;
        return TrendState.Transition;
    }

    /// <summary>
    /// §7/§16B 구조 방향. 확정된 최근 2개 high pivot과 2개 low pivot이 모두 있을 때만 계산하고
    /// delta는 최신 확정 값-직전 확정 값을 cutoff의 ATR로 나눈다.
    /// </summary>
    static (double? Direction, double? DeltaHigh, double? DeltaLow) StructureFamily(
        ImmutableArray<ConfirmedPivot> pivots, double? atr)
    {
        if (atr is null || !double.IsFinite(atr.Value) || atr.Value <= 0) return (null, null, null);
        var highs = pivots.Where(x => x.Kind == PivotKind.High)
            .OrderBy(x => x.ConfirmedAt).ThenBy(x => x.OccurredAt).TakeLast(2).ToArray();
        var lows = pivots.Where(x => x.Kind == PivotKind.Low)
            .OrderBy(x => x.ConfirmedAt).ThenBy(x => x.OccurredAt).TakeLast(2).ToArray();
        if (highs.Length < 2 || lows.Length < 2) return (null, null, null);
        var deltaHigh = (double)(highs[^1].Price - highs[0].Price) / atr.Value;
        var deltaLow = (double)(lows[^1].Price - lows[0].Price) / atr.Value;
        if (!double.IsFinite(deltaHigh) || !double.IsFinite(deltaLow)) return (null, null, null);
        var direction = (Math.Tanh(deltaHigh) + Math.Tanh(deltaLow)) / 2;
        return double.IsFinite(direction) ? (direction, deltaHigh, deltaLow) : (null, deltaHigh, deltaLow);
    }

    static ImmutableArray<StructureBar> Truncate(ImmutableArray<StructureBar> bars, DateTimeOffset cutoff,
        SortedSet<string> warnings)
    {
        var kept = bars.Where(x => x.End <= cutoff).OrderBy(x => x.Start).ToImmutableArray();
        if (kept.Length != bars.Length) warnings.Add(WarningFutureBars);
        return kept;
    }
}
