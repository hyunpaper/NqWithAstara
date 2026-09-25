using System.Collections.Immutable;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D2 — 설계 §9.4 + §16B EntryQuality.
// 순위 지표이며 확률이 아니다. 자격은 구조/이벤트/비용 상태가 결정하고 이 점수는 위치 품질만 비교한다.
// 첫 버전에서 BUY=70 같은 과거 기준을 붙이지 않는다(§9.4 마지막 문단).

/// <summary>구성요소의 원값·변환값·필수 여부를 함께 저장한다(§9.4).</summary>
public sealed record QualityComponent(string Name, double? Raw, double? Value, bool Required);

public sealed record EntryQualityResult(double? Score, ImmutableArray<QualityComponent> Components,
    ImmutableArray<string> UsedComponents, ImmutableArray<string> MissingRequired,
    ImmutableArray<string> ZeroRequired, ImmutableArray<string> Reasons)
{
    /// <summary>§16B: 필수 null은 점수 null/READY 금지, 필수 0은 점수 0/READY 금지다.</summary>
    public bool ReadyAllowed => Score is > 0;

    public string Fingerprint() => string.Join('|', StructureMath.Number(Score),
        string.Join(',', Components.Select(x => $"{x.Name}:{StructureMath.Number(x.Raw)}:{StructureMath.Number(x.Value)}:{(x.Required ? "R" : "o")}")),
        string.Join(',', UsedComponents), string.Join(',', MissingRequired), string.Join(',', ZeroRequired),
        string.Join(',', Reasons));
}

/// <summary>
/// EntryQuality 입력. 가격은 decimal, 정규화 입력은 double이며 결측은 null로 전달한다(0으로 대체하지 않는다).
/// </summary>
public sealed record EntryQualityInput(SetupKind Kind, double? InvalidationStrength, double? TargetStrength,
    decimal? NetR, decimal EntryReference, decimal? InvalidationAnchor, double? Atr1mAtPlan,
    double? RelativeVolume, double? SignedTrend, decimal? TriggerClose, decimal? SupportUpper,
    decimal? StopBuffer = null, TradeSide Side = TradeSide.Long);

/// <summary>
/// 설계 §9.4/§16B. 종류별 필수 구성요소의 기하평균(§16A)만 점수로 만들고 참고 지표는 넣지 않는다.
/// </summary>
public static class EntryQualityEvaluator
{
    public const string ReasonNoVolumeBaseline = "NO_VOLUME_BASELINE";
    public const string ReasonTrendUnavailable = "TREND_UNAVAILABLE";
    public const string ReasonAtrUnavailable = "ATR_UNAVAILABLE";
    public const string ReasonMissingRequiredComponent = "MISSING_REQUIRED_QUALITY_COMPONENT";
    public const string ReasonZeroRequiredComponent = "ZERO_REQUIRED_QUALITY_COMPONENT";

    public const string InvalidationQuality = "invalidationQuality";
    public const string TargetQuality = "targetQuality";
    public const string RoomQuality = "roomQuality";
    public const string ExtensionQuality = "extensionQuality";
    public const string TriggerVolumeQuality = "triggerVolumeQuality";
    public const string AlignmentQuality = "alignmentQuality";
    public const string ReclaimQuality = "reclaimQuality";

    /// <summary>
    /// §16B 종류별 필수 요소 목록(5요소, #209). 이 외의 참고 지표는 기하평균에 들어가지 않는다.
    /// netR은 MinimumNetR·MaxNetR 자격 게이트가 소비하므로 roomQuality로 다시 계상하지 않는다(§9.3/§9.4).
    /// </summary>
    public static ImmutableArray<string> RequiredComponents(SetupKind kind) => kind switch
    {
        SetupKind.Rebound =>
        [
            InvalidationQuality, TargetQuality, ExtensionQuality, TriggerVolumeQuality, ReclaimQuality
        ],
        _ =>
        [
            InvalidationQuality, TargetQuality, ExtensionQuality, TriggerVolumeQuality, AlignmentQuality
        ]
    };

    public static EntryQualityResult Evaluate(EntryQualityInput input, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var required = RequiredComponents(input.Kind);
        var components = ImmutableArray.CreateBuilder<QualityComponent>();

        // §9.4 invalidationQuality/targetQuality: 선택된 구조의 strength(§16B 이름 통일).
        components.Add(Component(InvalidationQuality, input.InvalidationStrength, input.InvalidationStrength, required));
        components.Add(Component(TargetQuality, input.TargetStrength, input.TargetStrength, required));

        // roomQuality=1-exp(-max(netR,0)/2). 참고값이며 점수에 들어가지 않는다(#209).
        double? roomRaw = input.NetR is { } netR ? (double)netR : null;
        double? room = roomRaw is null ? null : Normalized(1 - Math.Exp(-Math.Max(roomRaw.Value, 0) / policy.RoomQualityScale));
        components.Add(Component(RoomQuality, roomRaw, room, required));

        // §16A: ATR 결측·0·비유한은 결측으로 흘린다. IndicatorFloor는 NaN 방지용이며 결측 대체값이 아니다.
        double? usableAtr = input.Atr1mAtPlan is { } a && double.IsFinite(a) && a > 0 ? a : null;

        // extensionQuality=exp(-max((Entry-anchor)-stopBuffer,0)/max(ATR1m,0.01)/3)
        // #209: 손절 buffer는 진입가가 anchor에서 떨어진 거리가 아니라 손절 자체의 폭이다 — 추격 거리에서 뺀다.
        double? extensionRaw = null;
        double? extension = null;
        if (input.InvalidationAnchor is { } anchor)
        {
            if (usableAtr is { } atr)
            {
                var directionalDistance = input.Side == TradeSide.Long
                    ? input.EntryReference - anchor
                    : anchor - input.EntryReference;
                var distance = (double)(directionalDistance - (input.StopBuffer ?? 0m));
                if (distance < 0) distance = 0;
                extensionRaw = distance / Math.Max(atr, policy.IndicatorFloor);
                extension = Normalized(Math.Exp(-extensionRaw.Value / policy.ExtensionQualityAtrScale));
            }
            else reasons.Add(ReasonAtrUnavailable);
        }
        components.Add(Component(ExtensionQuality, extensionRaw, extension, required));

        // triggerVolumeQuality=relativeVolume/(1+relativeVolume); 분모 baseline 결측은 null+NO_VOLUME_BASELINE(§16A)
        double? volume = null;
        if (input.RelativeVolume is { } rv && double.IsFinite(rv) && rv >= 0) volume = Normalized(rv / (1 + rv));
        else reasons.Add(ReasonNoVolumeBaseline);
        components.Add(Component(TriggerVolumeQuality, input.RelativeVolume, volume, required));

        if (required.Contains(AlignmentQuality))
        {
            // PULLBACK/BREAKOUT의 alignmentQuality=(1+signedTrend/100)/2
            double? alignment = null;
            if (input.SignedTrend is { } signed && double.IsFinite(signed))
            {
                var directional = input.Side == TradeSide.Long ? signed : -signed;
                alignment = Normalized((1 + directional / 100) / 2);
            }
            else reasons.Add(ReasonTrendUnavailable);
            components.Add(Component(AlignmentQuality, input.SignedTrend, alignment, required));
        }

        if (required.Contains(ReclaimQuality))
        {
            // REBOUND: 회복 종가가 support Upper 위로 진행한 거리/ATR. 추세 점수를 필수로 요구하지 않는다(§8).
            double? reclaimRaw = null;
            double? reclaim = null;
            if (input.TriggerClose is { } close && input.SupportUpper is { } upper)
            {
                if (usableAtr is { } atr)
                {
                    var distance = (double)(input.Side == TradeSide.Long ? close - upper : upper - close);
                    if (distance < 0) distance = 0;
                    reclaimRaw = distance / Math.Max(atr, policy.IndicatorFloor);
                    reclaim = Normalized(1 - Math.Exp(-reclaimRaw.Value));
                }
                else reasons.Add(ReasonAtrUnavailable);
            }
            components.Add(Component(ReclaimQuality, reclaimRaw, reclaim, required));
            // 참고: alignmentQuality는 REBOUND에서 제외한다(§9.4).
            components.Add(new QualityComponent(AlignmentQuality, input.SignedTrend,
                input.SignedTrend is { } s && double.IsFinite(s)
                    ? Normalized((1 + (input.Side == TradeSide.Long ? s : -s) / 100) / 2)
                    : null, false));
        }

        var byName = components.ToImmutable();
        var missingRequired = required.Where(name => byName.First(x => x.Name == name).Value is null)
            .OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
        var zeroRequired = required.Where(name => byName.First(x => x.Name == name).Value is 0)
            .OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();

        double? score = null;
        if (missingRequired.Length > 0) reasons.Add(ReasonMissingRequiredComponent);
        else
        {
            var values = required.Select(name => byName.First(x => x.Name == name).Value!.Value).ToArray();
            score = 100 * StructureMath.GeometricMean(values);
            if (zeroRequired.Length > 0) reasons.Add(ReasonZeroRequiredComponent);
        }

        return new EntryQualityResult(score, byName,
            required.Where(name => byName.First(x => x.Name == name).Value is not null).ToImmutableArray(),
            missingRequired, zeroRequired, reasons.ToImmutableArray());
    }

    static QualityComponent Component(string name, double? raw, double? value, ImmutableArray<string> required) =>
        new(name, raw, value is { } v && double.IsFinite(v) && v >= 0 ? v : null, required.Contains(name));

    /// <summary>0~1 범위를 벗어난 부동소수점 잔차나 비유한 값을 점수로 통과시키지 않는다.</summary>
    static double? Normalized(double value)
    {
        if (!double.IsFinite(value)) return null;
        if (value < 0) return 0;
        return value > 1 ? 1 : value;
    }
}
