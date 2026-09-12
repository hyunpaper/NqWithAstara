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
    public const string WarningDiscontinuousBars = "DISCONTINUOUS_1M_BARS";
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
        var (structureDirection, deltaHigh, deltaLow) = StructureFamily(pivots5m, atr,
            bars.Length == 0 ? request.AnalysisCutoff : bars[^1].End, policy);

        // §16B "30개 연속 완료 1m 봉": 개수만이 아니라 cutoff 직전까지 끊기지 않은 구간을 요구한다.
        var enoughBars = TrailingConsecutiveBars(bars) >= policy.Minimum1mBars;
        if (bars.Length < policy.Minimum1mBars) warnings.Add(WarningInsufficientBars);
        else if (!enoughBars) warnings.Add(WarningDiscontinuousBars);
        if (atr is null or <= 0 || !double.IsFinite(atr ?? double.NaN)) { warnings.Add(WarningAtrUnavailable); missing.Add("atr1m"); }
        if (vwap is null) { warnings.Add(WarningVwapUnavailable); missing.Add("vwap"); }
        if (efficiency is null) { warnings.Add(WarningEfficiencyUnavailable); missing.Add("efficiency"); }

        var usableAtr = atr is { } a && double.IsFinite(a) && a > 0 ? a : (double?)null;
        var price = bars.Length == 0
            ? new PriceFamily(null, null, null, null, null, null, null, vwap, vwapSd)
            : PriceDirection(bars, bars.Length - 1, usableAtr, ema9, ema21, policy);
        if (price.EmaValue is null) missing.Add("emaDirection");
        if (price.SlopeValue is null) missing.Add("slopeDirection");
        if (price.VwapValue is null) missing.Add("vwapDirection");
        var priceDirection = price.Direction;

        var components = ImmutableArray.Create(
            new TrendComponent("emaDirection", "price", price.EmaRaw, price.EmaValue),
            new TrendComponent("slopeDirection", "price", price.SlopeRaw, price.SlopeValue),
            new TrendComponent("vwapDirection", "price", price.VwapRaw, price.VwapValue),
            // 두 delta는 별개 원값이라 합으로 뭉개지 않는다(§9.4, #65). structureDirection은 두 tanh의 평균이라
            // 되돌릴 수 있는 단일 원값이 없으므로 Raw는 결측이고, 원값은 아래 두 구성요소가 그대로 보존한다.
            new TrendComponent("structureDirection", "structure", null, structureDirection),
            new TrendComponent("structureDeltaHigh", "structure", deltaHigh, deltaHigh is null ? null : Math.Tanh(deltaHigh.Value)),
            new TrendComponent("structureDeltaLow", "structure", deltaLow, deltaLow is null ? null : Math.Tanh(deltaLow.Value)),
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

        var state = State(bars, atrSeries, ema9, ema21, pivots5m, policy);

        return new TrendAssessment(state, signed, priceDirection, structureDirection, efficiency, atr,
            ema9[^1], ema21[^1], vwap, vwapSd, structureMissing, bars.Length, request.AnalysisCutoff, components,
            used.ToImmutable(), missing.ToImmutableArray(), warnings.ToImmutableArray(),
            blockersForTrend.ToImmutableArray(), blockersForReady.ToImmutableArray());
    }

    /// <summary>봉 하나의 상태 판정 입력. 결측 봉은 <see cref="Available"/>=false다(§7, #148).</summary>
    readonly record struct BarSample(bool Available, double SignedTrend, double Efficiency, bool Opposed);

    /// <summary>
    /// §7 상태 히스테리시스(#148). 세션 완료 봉 시계열을 순차 적용하는 순수 함수이며 상태를 저장하지 않는다(§16B 재현성).
    /// UP/DOWN 진입·이탈은 각각 <see cref="StructurePolicy.TrendStateHoldBars"/> 연속 봉을 요구하고 TRANSITION은 즉시다.
    /// </summary>
    static TrendState State(ImmutableArray<StructureBar> bars, ImmutableArray<double?> atrSeries,
        ImmutableArray<double?> ema9, ImmutableArray<double?> ema21, ImmutableArray<ConfirmedPivot> pivots,
        StructurePolicy policy)
    {
        var hold = Math.Max(1, policy.TrendStateHoldBars);
        var state = TrendState.Unknown;
        int entryRun = 0, entryDirection = 0, exitRun = 0;

        foreach (var sample in Samples(bars, atrSeries, ema9, ema21, pivots, policy))
        {
            if (!sample.Available)
            {
                state = TrendState.Unknown;
                entryRun = entryDirection = exitRun = 0;
                continue;
            }

            // 두 family 부호 충돌은 안전 신호이므로 지연하지 않는다(§7).
            if (sample.Opposed)
            {
                state = TrendState.Transition;
                entryRun = entryDirection = exitRun = 0;
                continue;
            }

            if (state == TrendState.Unknown) state = TrendState.Range;
            var magnitude = Math.Abs(sample.SignedTrend);

            if (magnitude >= policy.TrendStateThreshold && sample.Efficiency >= policy.TrendEfficiencyThreshold)
            {
                var direction = Math.Sign(sample.SignedTrend);
                entryRun = direction == entryDirection ? entryRun + 1 : 1;
                entryDirection = direction;
                exitRun = 0;
                if (entryRun >= hold && direction != 0) state = direction > 0 ? TrendState.Up : TrendState.Down;
                continue;
            }

            entryRun = entryDirection = 0;
            if (sample.Efficiency < policy.TrendExitEfficiency || magnitude < policy.TrendExitSignedTrend)
            {
                exitRun++;
                if (state is TrendState.Up or TrendState.Down && exitRun >= hold) state = TrendState.Range;
            }
            else exitRun = 0;
        }

        return state;
    }

    /// <summary>§7 봉별 (signedTrend, efficiency, 부호충돌). 피벗은 확정 시각으로 걸러 봉 시점의 구조만 본다(§7, #148).</summary>
    static ImmutableArray<BarSample> Samples(ImmutableArray<StructureBar> bars, ImmutableArray<double?> atrSeries,
        ImmutableArray<double?> ema9, ImmutableArray<double?> ema21, ImmutableArray<ConfirmedPivot> pivots,
        StructurePolicy policy)
    {
        if (bars.Length == 0) return ImmutableArray<BarSample>.Empty;
        var samples = ImmutableArray.CreateBuilder<BarSample>(bars.Length);
        var consecutive = 0;
        for (var i = 0; i < bars.Length; i++)
        {
            consecutive = i > 0 && bars[i].Start == bars[i - 1].End ? consecutive + 1 : 1;
            var prefix = new BarPrefix(bars, i + 1);
            var atr = atrSeries[i] is { } candidate && double.IsFinite(candidate) && candidate > 0
                ? candidate : (double?)null;
            var efficiency = SessionIndicators.Efficiency(prefix, policy.EfficiencyLookbackBars);
            var price = PriceDirection(prefix, i, atr, ema9, ema21, policy).Direction;
            if (consecutive < policy.Minimum1mBars || price is null || efficiency is null)
            {
                samples.Add(new BarSample(false, 0, 0, false));
                continue;
            }

            var (structure, _, _) = StructureFamily(pivots, atr, bars[i].End, policy);
            var signed = structure is { } value ? 100 * (price.Value + value) / 2 : 100 * price.Value;
            var opposed = structure is { } opposing && Math.Sign(price.Value) * Math.Sign(opposing) < 0;
            samples.Add(new BarSample(double.IsFinite(signed), signed, efficiency.Value, opposed));
        }
        return samples.MoveToImmutable();
    }

    /// <summary>§7 가격 family. 상관된 ema/slope/vwap 세 항목의 평균이며 하나라도 결측이면 방향은 null이다.</summary>
    static PriceFamily PriceDirection(IReadOnlyList<StructureBar> bars, int index, double? atr,
        ImmutableArray<double?> ema9, ImmutableArray<double?> ema21, StructurePolicy policy)
    {
        var (vwap, vwapSd) = SessionIndicators.Vwap(bars);
        if (atr is not { } atrValue || bars.Count == 0)
            return new PriceFamily(null, null, null, null, null, null, null, vwap, vwapSd);

        double? emaRaw = null, emaValue = null, slopeRaw = null, slopeValue = null, vwapRaw = null, vwapValue = null;
        if (ema9[index] is { } fast && ema21[index] is { } slow)
        {
            emaRaw = (fast - slow) / atrValue;
            emaValue = Math.Tanh(emaRaw.Value);
        }

        var slopeIndex = index - policy.TrendSlopeLookbackBars;
        if (slopeIndex >= 0 && ema21[index] is { } now && ema21[slopeIndex] is { } before)
        {
            slopeRaw = (now - before) / (policy.TrendSlopeLookbackBars * atrValue);
            slopeValue = Math.Tanh(slopeRaw.Value);
        }

        if (vwap is { } vwapLevel)
        {
            var denominator = Math.Max(Math.Max(vwapSd ?? 0, atrValue), policy.IndicatorFloor);
            vwapRaw = ((double)bars[index].Close - vwapLevel) / denominator;
            vwapValue = Math.Tanh(vwapRaw.Value);
        }

        double? direction = null;
        if (emaValue is { } e && slopeValue is { } s && vwapValue is { } v)
        {
            var mean = (e + s + v) / 3;
            if (double.IsFinite(mean)) direction = mean;
        }
        return new PriceFamily(direction, emaRaw, emaValue, slopeRaw, slopeValue, vwapRaw, vwapValue, vwap, vwapSd);
    }

    /// <summary>가격 family의 원값과 변환값(§9.4 저장 규칙).</summary>
    readonly record struct PriceFamily(double? Direction, double? EmaRaw, double? EmaValue, double? SlopeRaw,
        double? SlopeValue, double? VwapRaw, double? VwapValue, double? Vwap, double? VwapSd);

    /// <summary>봉 배열의 앞 <c>count</c>개를 복사 없이 보는 뷰. 봉별 재계산이 O(n²) 할당을 만들지 않게 한다.</summary>
    sealed class BarPrefix(ImmutableArray<StructureBar> bars, int count) : IReadOnlyList<StructureBar>
    {
        public int Count => count;
        public StructureBar this[int index] => index >= 0 && index < count
            ? bars[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<StructureBar> GetEnumerator()
        {
            for (var i = 0; i < count; i++) yield return bars[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// §7/§16B 구조 방향. 확정된 최근 2개 high pivot과 2개 low pivot이 모두 있을 때만 계산하고
    /// delta는 최신 확정 값-직전 확정 값을 sqrt(StructureDirectionAtrScaleBars)·ATR로 나눈다(#148).
    /// </summary>
    static (double? Direction, double? DeltaHigh, double? DeltaLow) StructureFamily(
        ImmutableArray<ConfirmedPivot> pivots, double? atr, DateTimeOffset asOf, StructurePolicy policy)
    {
        if (atr is null || !double.IsFinite(atr.Value) || atr.Value <= 0) return (null, null, null);
        var scale = Math.Sqrt(Math.Max(1, policy.StructureDirectionAtrScaleBars)) * atr.Value;
        if (!double.IsFinite(scale) || scale <= 0) return (null, null, null);
        var highs = pivots.Where(x => x.Kind == PivotKind.High && x.ConfirmedAt <= asOf)
            .OrderBy(x => x.ConfirmedAt).ThenBy(x => x.OccurredAt).TakeLast(2).ToArray();
        var lows = pivots.Where(x => x.Kind == PivotKind.Low && x.ConfirmedAt <= asOf)
            .OrderBy(x => x.ConfirmedAt).ThenBy(x => x.OccurredAt).TakeLast(2).ToArray();
        if (highs.Length < 2 || lows.Length < 2) return (null, null, null);
        var deltaHigh = (double)(highs[^1].Price - highs[0].Price) / scale;
        var deltaLow = (double)(lows[^1].Price - lows[0].Price) / scale;
        if (!double.IsFinite(deltaHigh) || !double.IsFinite(deltaLow)) return (null, null, null);
        var direction = (Math.Tanh(deltaHigh) + Math.Tanh(deltaLow)) / 2;
        return double.IsFinite(direction) ? (direction, deltaHigh, deltaLow) : (null, deltaHigh, deltaLow);
    }

    /// <summary>마지막 봉에서 거꾸로 이어지는 완료 봉 개수. 앞 봉 End와 뒤 봉 Start가 같아야 연속이다.</summary>
    static int TrailingConsecutiveBars(ImmutableArray<StructureBar> bars)
    {
        if (bars.Length == 0) return 0;
        var run = 1;
        for (var i = bars.Length - 1; i > 0; i--)
        {
            if (bars[i].Start != bars[i - 1].End) break;
            run++;
        }
        return run;
    }

    static ImmutableArray<StructureBar> Truncate(ImmutableArray<StructureBar> bars, DateTimeOffset cutoff,
        SortedSet<string> warnings)
    {
        var kept = bars.Where(x => x.End <= cutoff).OrderBy(x => x.Start).ToImmutableArray();
        if (kept.Length != bars.Length) warnings.Add(WarningFutureBars);
        return kept;
    }
}
