using System.Collections.Immutable;
using System.Globalization;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D2 — 설계 §9.1~§9.3 + §16A/§16B 목표·손절·비용.
// 구조 근거가 없으면 진입 보류가 정답이다. ATR 배수·1.5R 역산으로 목표나 손절을 만들어내지 않는다.
// 모든 가격 연산은 decimal 경로를 벗어나지 않는다(§16A).

/// <summary>§9.3/§16B 비용 가정. 자격 평가용 비용과 실현 손익 비용을 같은 값으로 취급하지 않는다.</summary>
public sealed record CostAssumptions(decimal FeePerShare, decimal ExtraCostPerShare, decimal? ValidSpread,
    bool MissingLiquidity, double RoundTripFeePercent, string EligibilityCostModelVersion,
    string RealizedFillCostModelVersion, ImmutableArray<string> Notes);

/// <summary>
/// 설계 §11 StructuralTradePlan. Stop/Target은 구조에서 나온 값만 담으며, 계획이 성립하지 않으면
/// 이 객체를 만들지 않는다(null Plan에 0을 넣어 직렬화를 통과시키지 않는다, §11).
/// </summary>
public sealed record StructuralTradePlan(string PlanId, string Kind, decimal EntryReference,
    decimal InvalidationAnchor, decimal Stop, decimal Target, PriceZone InvalidationZoneSnapshot,
    PriceZone TargetZoneSnapshot, decimal Buffer, string BufferBasis, decimal FrontRunBuffer,
    decimal NetReward, decimal NetRisk, decimal NetR, double RiskPercent, double? Atr1mAtPlan,
    CostAssumptions Costs, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string EngineVersion,
    string PolicyHash, ImmutableArray<string> ReasonCodes, string HumanExplanation)
{
    public string Fingerprint() => string.Join('|', PlanId, Kind, StructureMath.Price(EntryReference),
        StructureMath.Price(InvalidationAnchor), StructureMath.Price(Stop), StructureMath.Price(Target),
        InvalidationZoneSnapshot.Id, StructureMath.Price(InvalidationZoneSnapshot.Lower),
        StructureMath.Price(InvalidationZoneSnapshot.Upper), TargetZoneSnapshot.Id,
        StructureMath.Price(TargetZoneSnapshot.Lower), StructureMath.Price(TargetZoneSnapshot.Upper),
        StructureMath.Price(Buffer), BufferBasis, StructureMath.Price(NetReward), StructureMath.Price(NetRisk),
        StructureMath.Price(NetR), StructureMath.Number(RiskPercent), StructureMath.Number(Atr1mAtPlan),
        StructureMath.Price(Costs.FeePerShare), StructureMath.Price(Costs.ExtraCostPerShare),
        Costs.MissingLiquidity ? "1" : "0", EngineVersion, PolicyHash, string.Join(',', ReasonCodes));
}

/// <summary>계획 계산 입력. 가격은 decimal, ATR은 double이며 시각은 명시적으로 전달한다(§4, §16A).</summary>
public sealed record PlanRequest(string Symbol, string EventId, string Kind, decimal EntryReference,
    decimal? InvalidationAnchor, PriceZone? InvalidationZone, ImmutableArray<PriceZone> Zones,
    double? Atr1mAtPlan, decimal? ValidSpread, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    bool PriceTickSupported = true);

/// <summary>
/// 계획 계산 결과. 계획이 성립하지 않아도 화면·관측이 이유를 설명할 수 있도록 중간값을 남기지만,
/// <see cref="Plan"/>이 null이면 그 값들은 계획이 아니다(§16B: plan은 READY/ENTERED에서만 존재한다).
/// </summary>
public sealed record PlanEvaluation(StructuralTradePlan? Plan, ImmutableArray<string> ReasonCodes,
    decimal? Anchor, decimal? Buffer, string? BufferBasis, decimal? Stop, decimal? Target,
    decimal? NetReward, decimal? NetRisk, decimal? NetR, double? RiskPercent,
    PriceZone? InvalidationZone, PriceZone? TargetZone, decimal? ValidSpread, bool MissingLiquidity,
    double? Atr1mAtPlan, ImmutableArray<string> Warnings)
{
    public bool Viable => Plan is not null;
}

/// <summary>§16B 유효 spread 규칙. 교차/결측/지연 호가는 사용할 수 없고 locked spread=0은 허용한다.</summary>
public static class StructureLiquidityRules
{
    public const string ReasonMissing = "SPREAD_MISSING";
    public const string ReasonStale = "SPREAD_STALE";
    public const string ReasonFuture = "SPREAD_FUTURE_TIMESTAMP";
    public const string ReasonCrossed = "SPREAD_CROSSED";
    public const string ReasonNonPositive = "SPREAD_NON_POSITIVE_PRICE";
    public const string ReasonSizeUnknown = "SPREAD_SIZE_UNKNOWN";
    public const string ReasonSizeNonPositive = "SPREAD_NON_POSITIVE_SIZE";
    public const string ReasonOutsideSession = "SPREAD_OUTSIDE_SESSION";

    public static (decimal? Spread, ImmutableArray<string> Reasons) Validate(StructureLiquidity? liquidity,
        DateTimeOffset now, DateTimeOffset sessionStart, DateTimeOffset sessionEnd, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        if (liquidity is null || liquidity.BestBid is null || liquidity.BestAsk is null || liquidity.At is null)
            return (null, [ReasonMissing]);

        var bid = liquidity.BestBid.Value;
        var ask = liquidity.BestAsk.Value;
        var at = liquidity.At.Value;
        if (bid <= 0 || ask <= 0) reasons.Add(ReasonNonPositive);
        if (ask < bid) reasons.Add(ReasonCrossed);
        if ((now - at).TotalSeconds > policy.SpreadMaxAgeSeconds) reasons.Add(ReasonStale);
        if ((at - now).TotalSeconds > policy.QuoteFutureToleranceSeconds) reasons.Add(ReasonFuture);
        if (at < sessionStart || at > sessionEnd) reasons.Add(ReasonOutsideSession);
        // 호가 잔량은 선택 입력이다. 없으면 사용은 허용하되 근사 사실을 남긴다(D2 결정).
        if (liquidity.BidSize is null || liquidity.AskSize is null) reasons.Add(ReasonSizeUnknown);
        else if (liquidity.BidSize <= 0 || liquidity.AskSize <= 0) reasons.Add(ReasonSizeNonPositive);

        var blocking = reasons.Where(x => x != ReasonSizeUnknown).ToArray();
        return blocking.Length > 0 ? (null, reasons.ToImmutableArray()) : (ask - bid, reasons.ToImmutableArray());
    }
}

/// <summary>
/// 설계 §9.1~§9.3. 무효화 구조에서 손절을, 가장 가까운 자격 있는 저항에서 목표를 만들고
/// 비용·위험·공간 조건으로 거절한다. 구조가 없으면 null과 사유이며 폴백을 만들지 않는다(§19-5).
/// </summary>
public static class StructuralPlanner
{
    public const string NoInvalidationStructure = "NO_INVALIDATION_STRUCTURE";
    public const string NoTargetStructure = "NO_TARGET_STRUCTURE";
    public const string NoTargetRoom = "NO_TARGET_ROOM";
    public const string CostExceedsRoom = "COST_EXCEEDS_ROOM";
    public const string InsufficientRewardToRisk = "INSUFFICIENT_REWARD_TO_RISK";
    public const string RiskTooWide = "RISK_TOO_WIDE";
    public const string StopNotBelowEntry = "STOP_NOT_BELOW_ENTRY";
    public const string StopNotPositive = "STOP_NOT_POSITIVE";
    public const string InvalidEntryReference = "INVALID_ENTRY_REFERENCE";
    public const string PriceBelowMinimumSupported = "PRICE_BELOW_MINIMUM_SUPPORTED";
    public const string UnsupportedPriceTick = "UNSUPPORTED_PRICE_TICK";
    public const string MissingLiquidityCost = "MISSING_LIQUIDITY_COST";
    public const string BufferFromTickOnly = "BUFFER_FROM_TICK_ONLY";

    public const string BasisAtrNoise = "ATR_NOISE";
    public const string BasisSpread = "SPREAD";
    public const string BasisTickFloor = "TICK_FLOOR";

    public static PlanEvaluation Evaluate(PlanRequest request, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        var reasons = new List<string>();
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var entry = request.EntryReference;

        var spread = request.ValidSpread;
        var missingLiquidity = spread is null;
        // §16B: 호가 결측만으로 READY를 막지는 않는다. assumedSpread=0과 사유를 표시하고 비용 일부 미반영을 명시한다.
        if (missingLiquidity) warnings.Add(MissingLiquidityCost);
        var extraCost = spread ?? 0m;

        if (!request.PriceTickSupported) reasons.Add(UnsupportedPriceTick);
        if (entry <= 0) reasons.Add(InvalidEntryReference);
        else if (entry < policy.MinimumSupportedPrice) reasons.Add(PriceBelowMinimumSupported);
        if (reasons.Count > 0)
            return Rejected(reasons, warnings, request, null, null, null, null, spread, missingLiquidity);

        // ── §9.1 손절: 구조 anchor 없으면 Stop=null, READY 금지 ──
        decimal? buffer = null, stop = null;
        string? bufferBasis = null;
        var anchor = request.InvalidationAnchor;
        if (anchor is null || request.InvalidationZone is null)
        {
            reasons.Add(NoInvalidationStructure);
        }
        else
        {
            var atrBuffer = StructureMath.ToPriceDelta(
                request.Atr1mAtPlan is { } atrValue && double.IsFinite(atrValue) && atrValue > 0
                    ? atrValue * policy.StopBufferAtrFactor
                    : null);
            if (atrBuffer is null) warnings.Add(BufferFromTickOnly);
            var candidates = new (decimal Value, string Basis)[]
            {
                (atrBuffer ?? 0m, BasisAtrNoise),
                (extraCost, BasisSpread),
                (policy.StopBufferFloor, BasisTickFloor)
            };
            var best = candidates[0];
            foreach (var candidate in candidates)
                if (candidate.Value > best.Value) best = candidate;
            buffer = best.Value;
            bufferBasis = best.Basis;

            var computed = StructureMath.FloorToCent(anchor.Value - buffer.Value);
            stop = computed;
            if (computed <= 0) reasons.Add(StopNotPositive);
            if (computed >= entry) reasons.Add(StopNotBelowEntry);
        }

        // §9.1 초기 MaxRiskPercent는 사용 안전 정책이다. 넘으면 거절하고 손절을 구조 안쪽으로 당기지 않는다.
        double? riskPercent = null;
        if (stop is { } stopValue && stopValue > 0 && stopValue < entry)
        {
            riskPercent = (double)((entry - stopValue) / entry) * 100;
            if (riskPercent > policy.MaxRiskPercent) reasons.Add(RiskTooWide);
        }

        // ── §9.2 목표: 가장 가까운 자격 있는 저항 앞 ──
        var frontRun = policy.FrontRunBufferFloor > extraCost ? policy.FrontRunBufferFloor : extraCost;
        var targetZone = NearestQualifiedResistance(request.Zones, entry);
        decimal? target = null;
        if (targetZone is null) reasons.Add(NoTargetStructure);
        else
        {
            target = StructureMath.FloorToCent(targetZone.Lower - frontRun);
            if (target <= entry) reasons.Add(NoTargetRoom);
        }

        // ── §9.3 비용과 진입 자격 ──
        decimal? netReward = null, netRisk = null, netR = null;
        // §9.3 feeCostPerShare=Entry*RoundTripFeePercent/100. 0.2%는 설정 기본값이며 계좌별 실제 수수료가 아니다.
        var fee = entry * (decimal)policy.RoundTripFeePercent / 100m;
        if (stop is { } s2 && s2 > 0 && s2 < entry && target is { } t2 && t2 > entry)
        {
            netReward = t2 - entry - fee - extraCost;
            netRisk = entry - s2 + fee + extraCost;
            if (netReward <= 0) reasons.Add(CostExceedsRoom);
            if (netRisk <= 0) reasons.Add(InvalidEntryReference);
            else
            {
                netR = netReward.Value / netRisk.Value;
                if (netReward > 0 && netR < (decimal)policy.MinimumNetR) reasons.Add(InsufficientRewardToRisk);
            }
        }

        var costs = new CostAssumptions(fee, extraCost, spread, missingLiquidity, policy.RoundTripFeePercent,
            policy.EligibilityCostModelVersion, policy.RealizedFillCostModelVersion, warnings.ToImmutableArray());

        if (reasons.Count > 0)
            return Rejected(reasons, warnings, request, anchor, buffer, stop, target, spread, missingLiquidity,
                bufferBasis, netReward, netRisk, netR, riskPercent, targetZone);

        var plan = new StructuralTradePlan(
            PlanId(request, entry, stop!.Value, target!.Value, policy),
            request.Kind, entry, anchor!.Value, stop.Value, target.Value, request.InvalidationZone!, targetZone!,
            buffer!.Value, bufferBasis!, frontRun, netReward!.Value, netRisk!.Value, netR!.Value,
            riskPercent!.Value, request.Atr1mAtPlan, costs, request.CreatedAt, request.ExpiresAt,
            policy.Version, policy.PolicyHash, warnings.ToImmutableArray(),
            Explain(request, entry, anchor.Value, stop.Value, target.Value, targetZone!, netR.Value));

        return new PlanEvaluation(plan, ImmutableArray<string>.Empty, anchor, buffer, bufferBasis, stop, target,
            netReward, netRisk, netR, riskPercent, request.InvalidationZone, targetZone, spread, missingLiquidity,
            request.Atr1mAtPlan, warnings.ToImmutableArray());
    }

    /// <summary>
    /// §9.2: 진입가 위에 있고 평가 시점 이전에 확인된 자격 있는 저항 중 가장 가까운 것.
    /// 가까운 저항을 무시하고 먼 저항을 골라 손익비를 예쁘게 만들지 않는다(§19-5).
    /// </summary>
    public static PriceZone? NearestQualifiedResistance(ImmutableArray<PriceZone> zones, decimal entry) =>
        zones
            .Where(x => x.Eligible && !x.Retired && !x.ProfileOnly)
            .Where(x => x.Role is ZoneRole.Resistance or ZoneRole.FlippedResistance)
            .Where(x => x.Lower > entry)
            .OrderBy(x => x.Lower).ThenBy(x => x.Upper).ThenBy(x => x.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    static PlanEvaluation Rejected(List<string> reasons, SortedSet<string> warnings, PlanRequest request,
        decimal? anchor, decimal? buffer, decimal? stop, decimal? target, decimal? spread, bool missingLiquidity,
        string? bufferBasis = null, decimal? netReward = null, decimal? netRisk = null, decimal? netR = null,
        double? riskPercent = null, PriceZone? targetZone = null) =>
        new(null, reasons.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray(),
            anchor, buffer, bufferBasis, stop, target, netReward, netRisk, netR, riskPercent,
            request.InvalidationZone, targetZone, spread, missingLiquidity, request.Atr1mAtPlan,
            warnings.ToImmutableArray());

    static string PlanId(PlanRequest request, decimal entry, decimal stop, decimal target, StructurePolicy policy) =>
        StructureMath.SourceId("plan", request.Symbol, request.EventId, StructureMath.Price(entry),
            StructureMath.Price(stop), StructureMath.Price(target), policy.PolicyHash);

    /// <summary>§13 이유 표기. 색깔이나 점수가 아니라 구조 근거를 문장으로 남긴다.</summary>
    static string Explain(PlanRequest request, decimal entry, decimal anchor, decimal stop, decimal target,
        PriceZone targetZone, decimal netR)
    {
        var zone = request.InvalidationZone!;
        string P(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        return $"진입 {P(entry)} / 손절 {P(stop)}: 무효화 구조 {P(zone.Lower)}~{P(zone.Upper)} 하단과 anchor {P(anchor)} 아래 " +
               $"/ 목표 {P(target)}: 다음 저항 {P(targetZone.Lower)}~{P(targetZone.Upper)} 하단 앞 " +
               $"/ 비용 반영 손익비 {netR.ToString("0.000", CultureInfo.InvariantCulture)}";
    }
}
