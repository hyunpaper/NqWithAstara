using Astra.Server.Domain.Structure;
using Astra.Server;

namespace Astra.Server.Domain;

// v5 구조 엔진 D6 — 설계 §18 active 배선의 Domain 절반.
// active에서 신규 시뮬 거래는 v5 구조 계획(StructuralPlanner)이 소유하고, 체결 시점의 FrozenPlan을 함께 저장한다.
// 여기서는 계획을 만들지 않는다. 성립한 StructuralTradePlan을 받아 거래로 옮길 뿐이며,
// 구조 근거가 없을 때의 폴백(ATR 배수·1.5R 역산)은 존재하지 않는다(§19-5).
// 청산은 새로 만들지 않는다. v5 거래도 기존 SimulationEngine의 봉 replay·gap stop·same-bar stop-first·EOD를
// 그대로 재사용하되, v4 전용 score<40 CUT만 v5에 적용하지 않는다(§10).

/// <summary>구조 진입 시도의 결과 구분. 실패는 이유를 남기고 v4로 자동 fallback하지 않는다(§16B).</summary>
public enum StructuralEntryOutcome
{
    /// <summary>새 v5 거래가 생성됐다.</summary>
    Entered,
    /// <summary>같은 EntryEventId의 거래가 이미 존재한다(재시도·재시작 멱등성). 새 거래를 만들지 않는다.</summary>
    AlreadyEntered,
    /// <summary>해당 종목에 OPEN 거래가 있다. 한 종목 OPEN 하나 제한은 버전 공통이다(§18).</summary>
    BlockedByOpenTrade,
    /// <summary>동결 계획의 가격 순서(0 &lt; Stop &lt; Entry &lt; Target)가 성립하지 않는다. 거래를 만들지 않는다.</summary>
    InvalidPlan,
    /// <summary>
    /// 같은 심볼·같은 세션의 최신 STOP 청산 이후 완료 봉이 <see cref="StructurePolicy.StopReentryCooldownBars"/>개에
    /// 못 미친다(§10 손절 후 재진입 제한). 거래를 만들지 않는다.
    /// </summary>
    BlockedByStopCooldown,
    /// <summary>호가 비용이 결측인 계획은 실제 진입으로 승격하지 않는다.</summary>
    BlockedByMissingLiquidityCost,
    /// <summary>
    /// #245 U5 §10 전종목 손절 쿨다운/일일 손절 상한에 걸렸다. 어느 종목이든 최근 STOP 청산이
    /// <see cref="StructurePolicy.CrossSymbolStopCooldownMinutes"/> 분 안에 있거나 당일 누적 STOP이
    /// <see cref="StructurePolicy.MaxDailyStops"/>에 도달했다. 거래를 만들지 않는다.
    /// </summary>
    BlockedByCrossSymbolStopCooldown
}

/// <summary>
/// 구조 진입 요청. 거래 숫자(진입가·손절·목표)는 전부 <see cref="FrozenStructureContext.PlanSnapshot"/>에서 오며
/// 다른 어디서도 만들지 않는다. EnteredAt/SessionEnd는 Application이 명시적으로 전달한다(§4).
/// </summary>
public sealed record StructuralEntryRequest(string Symbol, DateTimeOffset TriggerBarStart,
    DateTimeOffset EnteredAt, DateTimeOffset SessionEnd, FrozenStructureContext Context,
    IReadOnlyList<DateTimeOffset>? CompletedBarStarts = null, DateTimeOffset? SessionStart = null,
    // #111: 목표 구간의 zone lineage(병합으로 흡수된 ID). 없으면 재진입 태그는 ID 동일 여부만 본다.
    IReadOnlyList<string>? TargetZoneAliases = null,
    // 확인봉 체결을 사용한 경우 관측 근거를 SimTrade에 전파한다. null은 기존 실시간 호출의
    // 미관측 호가 경로로 남겨 하위 호환한다.
    EntryConfirmation? Confirmation = null, bool RequireCompleteLiquidityCost = false,
    double? BenchmarkReturnPercent = null,
    // #245 U5: 이번 거래일(세션) 전종목 STOP 청산 시각들. 호출자는 EnteredAt 이후(미래)의 청산을 넣지 않는다(룩어헤드 금지).
    // null이면 전종목 쿨다운을 적용하지 않는다(기존 실시간·parallel replay 경로와 동일).
    IReadOnlyList<DateTimeOffset>? CrossSymbolSessionStopExits = null);

public sealed record StructuralEntryResult(List<SimTrade> Trades, StructuralEntryOutcome Outcome, SimTrade? Trade);

public static class StructuralSimulation
{
    /// <summary>
    /// v5 청산 정책 버전(§11 StructuralExitPolicyVersion). 동결된 구조 Stop/Target과 EOD만으로 관리하고
    /// 조기 청산/트레일링/v4 CUT을 적용하지 않는다는 계약을 저장 데이터에 남긴다.
    /// </summary>
    public const string ExitPolicyVersion = "v5-exit.frozen-plan.1";
    public const string TwoRFeeBreakEvenExitPolicyVersion = "v5-exit.two-r-fee-break-even.1";
    public const string TwoRTargetAndFeeBreakEvenExitPolicyVersion = "v5-exit.two-r-target-fee-break-even.1";
    public const string HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion = "v5-exit.positive-benchmark-half-r-fee-break-even.1";
    public const string HalfRQualifiedTransitionFeeBreakEvenExitPolicyVersion = "v5-exit.qualified-transition-half-r-fee-break-even.1";

    /// <summary>§9.2 트레일 청산이 켜진 거래의 청산 버전 꼬리표 접두(#245 H-B3-3). 기본 청산 버전 뒤에 붙여 provenance를 남긴다.</summary>
    const string TrailingExitPolicyVersionMarker = "+trail.";

    /// <summary>§9.2 트레일 꼬리표를 떼어 낸 기본 청산 버전. BE 손절 판정은 이 기본 버전으로 한다(#245 H-B3-3).</summary>
    public static string BaseExitVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var idx = version.IndexOf(TrailingExitPolicyVersionMarker, StringComparison.Ordinal);
        return idx < 0 ? version : version[..idx];
    }

    /// <summary>§9.2 트레일 필드가 켜졌을 때만 기본 청산 버전에 트레일 꼬리표를 붙인다. 꺼져 있으면 기본 버전 그대로다(hash·parity 불변, #245 H-B3-3).</summary>
    static string WithTrailing(string baseVersion, double? triggerR, double? distanceR, double? targetExtensionR)
    {
        if (triggerR is null && distanceR is null && targetExtensionR is null) return baseVersion;
        var trigger = triggerR is { } t ? t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "na";
        var distance = distanceR is { } d ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "na";
        var target = targetExtensionR is { } x ? x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "keep";
        return $"{baseVersion}{TrailingExitPolicyVersionMarker}t{trigger}-d{distance}-x{target}.1";
    }

    /// <summary>#326 진입 시 벤치마크 수익률 출처 상태.</summary>
    public const string BenchmarkAvailable = "AVAILABLE";
    public const string BenchmarkUnavailable = "UNAVAILABLE";

    /// <summary>v5 거래의 손절/목표 근거 표기(표시용). 숫자의 원천은 FrozenPlan이다.</summary>
    public const string StopBasis = "구조 무효화 anchor 아래";
    public const string TargetBasis = "다음 저항 하단 앞";

    /// <summary>
    /// v5 소유 거래인지. Logic 접두사가 단일 기준이며(설계 §11: 신규 활성 v5 거래는 "v5-structure.*"),
    /// 동결 컨텍스트가 남아 있는 행도 v5로 본다(부분 손상 데이터 방어).
    /// </summary>
    public static bool OwnsTrade(SimTrade trade) =>
        trade.Logic?.StartsWith("v5-structure", StringComparison.Ordinal) == true || trade.Structure is not null;

    /// <summary>
    /// 진입 시점의 구조 계획을 동결한다(§10 "체결 시 FrozenPlan을 저장한다").
    /// 이후 계산 결과가 바뀌어도 이 스냅샷은 다시 만들지 않는다.
    /// </summary>
    public static FrozenStructureContext Freeze(StructuralTradePlan plan, string entryEventId, string trendAtEntry,
        double? signedTrendAtEntry, double? entryQualityAtEntry, DateTimeOffset analysisAsOf, DateTimeOffset? quoteAt,
        StructurePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(entryEventId);
        var snapshot = new FrozenPlanSnapshot(plan.PlanId, plan.Kind, plan.EntryReference, plan.InvalidationAnchor,
            plan.Stop, plan.Target, plan.InvalidationZoneSnapshot.Id, plan.InvalidationZoneSnapshot.Lower,
            plan.InvalidationZoneSnapshot.Upper, plan.TargetZoneSnapshot.Id, plan.TargetZoneSnapshot.Lower,
            plan.TargetZoneSnapshot.Upper, plan.Buffer, plan.BufferBasis, plan.FrontRunBuffer, plan.NetReward,
            plan.NetRisk, plan.NetR, plan.RiskPercent, plan.Costs.FeePerShare, plan.Costs.ExtraCostPerShare,
            plan.Costs.ValidSpread, plan.Costs.MissingLiquidity, plan.Costs.EligibilityCostModelVersion,
            plan.Costs.RealizedFillCostModelVersion, plan.CreatedAt, plan.ExpiresAt, plan.EngineVersion,
            plan.PolicyHash, plan.ReasonCodes.ToArray(), plan.HumanExplanation, plan.Atr1mAtPlan,
            plan.Side, plan.Regime, plan.Evidence, plan.Costs.BorrowCostPerShare, plan.Costs.BorrowCostMissing);
        var selectedPolicy = policy ?? StructurePolicy.Default;
        var exitPolicyVersion = selectedPolicy.EnableTwoRFeeBreakEvenStop && selectedPolicy.CapStructuralTargetAtTwoR
            ? TwoRTargetAndFeeBreakEvenExitPolicyVersion
            : selectedPolicy.EnableTwoRFeeBreakEvenStop ? TwoRFeeBreakEvenExitPolicyVersion : ExitPolicyVersion;
        return new FrozenStructureContext(entryEventId, snapshot, trendAtEntry, signedTrendAtEntry,
            entryQualityAtEntry, analysisAsOf, quoteAt, exitPolicyVersion, null, plan.Regime, plan.Evidence);
    }

    /// <summary>
    /// 동결 계획으로 새 시뮬 거래를 만든다. 규칙(§10/§16B/§18):
    /// 같은 EntryEventId는 다시 진입하지 않고(재시작·저장 실패 재시도 멱등성), 종목당 OPEN 1개 제한은 v4/v5 공통이며,
    /// Score에는 EntryQuality를 끼워 넣지 않는다(§11 "Score는 과거 의미 보존"). Logic은 계획의 EngineVersion이다.
    /// </summary>
    public static StructuralEntryResult Enter(IReadOnlyList<SimTrade> source, StructuralEntryRequest request,
        StructurePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        var trades = source.ToList();
        var selectedPolicy = policy ?? StructurePolicy.Default;

        var existing = trades.FirstOrDefault(x =>
            string.Equals(x.Structure?.EntryEventId, request.Context.EntryEventId, StringComparison.Ordinal));
        if (existing is not null) return new StructuralEntryResult(trades, StructuralEntryOutcome.AlreadyEntered, existing);

        if (trades.Any(x => x.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase) && x.Status == "OPEN"))
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByOpenTrade, null);

        if (StopCooldownActive(trades, request, selectedPolicy.StopReentryCooldownBars))
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByStopCooldown, null);

        if (CrossSymbolStopCooldownActive(request, selectedPolicy))
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByCrossSymbolStopCooldown, null);

        var plan = request.Context.PlanSnapshot;
        var entry = request.Confirmation is { Decision: PendingEntryDecision.Confirmed, FillPrice: > 0 } confirmation
            ? confirmation.FillPrice!.Value : (double)plan.EntryReference;
        var stop = (double)plan.Stop;
        var target = (double)plan.Target;
        if (request.Context.StructuralExitPolicyVersion == TwoRTargetAndFeeBreakEvenExitPolicyVersion)
        {
            var risk = Math.Abs(entry - stop);
            var twoRTarget = plan.Side == TradeSide.Long ? entry + 2 * risk : entry - 2 * risk;
            target = plan.Side == TradeSide.Long ? Math.Min(target, twoRTarget) : Math.Max(target, twoRTarget);
        }
        // §9.2 구조 목표 연장(#245 H-B3-3). 양수면 목표를 진입+R로 연장하되(2R 상한 우선 override), 구조 목표가 더 멀면 그대로 둔다.
        // 0(해제)은 Target을 바꾸지 않고 봉 replay에서 목표 판정만 건너뛴다. 가격 순서는 유지된다.
        if (selectedPolicy.StructuralTargetExtensionR is { } extensionR && extensionR > 0)
        {
            var risk = Math.Abs(entry - stop);
            var extendedTarget = plan.Side == TradeSide.Long ? entry + extensionR * risk : entry - extensionR * risk;
            target = plan.Side == TradeSide.Long ? Math.Max(target, extendedTarget) : Math.Min(target, extendedTarget);
        }
        var ordered = plan.Side == TradeSide.Long
            ? stop > 0 && stop < entry && target > entry
            : stop > entry && target > 0 && target < entry;
        if (!ordered)
            return new StructuralEntryResult(trades, StructuralEntryOutcome.InvalidPlan, null);
        if (request.RequireCompleteLiquidityCost && plan.MissingLiquidity)
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByMissingLiquidityCost, null);

        // 결정적 ID: 같은 이벤트의 재시도가 다른 거래처럼 보이지 않게 한다(§16B 재시작 규칙과 같은 방향).
        var id = StructureMath.SourceId("simtrade", request.Symbol, request.Context.EntryEventId)[..8];
        var exitPolicyVersion = selectedPolicy.EnableHalfRFeeBreakEvenStopForQualifiedTransition &&
                                string.Equals(request.Context.TrendAtEntry, "Transition", StringComparison.OrdinalIgnoreCase)
            ? HalfRQualifiedTransitionFeeBreakEvenExitPolicyVersion
            : selectedPolicy.EnableHalfRFeeBreakEvenStopForPositiveBenchmark && request.BenchmarkReturnPercent > 0 &&
              !(selectedPolicy.ExemptBreakoutFromPositiveBenchmarkHalfRStop && string.Equals(plan.Kind, "BREAKOUT", StringComparison.Ordinal))
                ? HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion
                : request.Context.StructuralExitPolicyVersion;
        // §9.2 트레일 청산은 트리거·거리가 둘 다 있을 때만 무장한다. 둘 중 하나만 있으면 동결하지 않아 기존 동작이 유지된다(#245 H-B3-3).
        var trailTriggerR = selectedPolicy.TrailingStopTriggerR is { } tr && selectedPolicy.TrailingStopDistanceR is { } td && tr >= 0 && td > 0
            ? (double?)tr : null;
        var trailDistanceR = trailTriggerR is not null ? selectedPolicy.TrailingStopDistanceR : null;
        var targetExtensionR = selectedPolicy.StructuralTargetExtensionR;
        exitPolicyVersion = WithTrailing(BaseExitVersion(exitPolicyVersion), trailTriggerR, trailDistanceR, targetExtensionR);
        var benchmark = new EntryBenchmarkTags(
            request.BenchmarkReturnPercent is { } observed && double.IsFinite(observed) ? BenchmarkAvailable : BenchmarkUnavailable,
            request.BenchmarkReturnPercent is { } value && double.IsFinite(value) ? value : null);
        var context = request.Context with
        {
            PlanSnapshot = plan, Reentry = Reentry(trades, request), StructuralExitPolicyVersion = exitPolicyVersion,
            Benchmark = benchmark,
            TrailingStopTriggerR = trailTriggerR, TrailingStopDistanceR = trailDistanceR,
            StructuralTargetExtensionR = targetExtensionR
        };
        var trade = new SimTrade(id, request.Symbol, plan.Kind, request.EnteredAt, entry, target, stop,
            TargetBasis, StopBasis, "OPEN", null, null, null, entry,
            Score: null, ExtSigma: null, RelVolume: null, BuyShare: null, Rsi: null,
            Reasons: [plan.Explanation], Logic: plan.EngineVersion, LastEvaluatedBarAt: null,
            SessionEnd: request.SessionEnd, ExitEstimated: null, LastPriceAt: request.EnteredAt,
            TriggerBarAt: request.TriggerBarStart, Structure: context,
            Execution: EntryProvenance(request.EnteredAt, request.Confirmation), Side: plan.Side);
        trades.Add(trade);
        return new StructuralEntryResult(trades, StructuralEntryOutcome.Entered, trade);
    }

    static ExecutionProvenance EntryProvenance(DateTimeOffset enteredAt, EntryConfirmation? confirmation)
    {
        if (confirmation is { Decision: PendingEntryDecision.Confirmed } c)
            return new ExecutionProvenance(c.Pending.ConfirmationBarStart,
                c.ObservedAt, "OBSERVED_CONFIRMATION_BAR", c.ObservedAt,
                FillPrice: c.FillPrice, SpreadCost: c.SpreadCost, PriceSource: c.PriceSource);
        var start = new DateTimeOffset(enteredAt.Year, enteredAt.Month, enteredAt.Day,
                    enteredAt.Hour, enteredAt.Minute, 0, enteredAt.Offset);
        return new ExecutionProvenance(
                start, start.AddMinutes(1),
                "UNOBSERVED", null);
    }

    /// <summary>
    /// §10 손절 후 재진입 제한. 같은 심볼의 최신 STOP 청산이 일어난 완료 봉을 0번째로 세어, 트리거 봉이 그 봉으로부터
    /// cooldownBars개 뒤에 오기 전까지 차단한다(= 청산 봉 이후 닫힌 완료 봉이 cooldownBars개 미만이면 차단).
    /// 시계를 보지 않고 요청이 준 완료 봉 시각만 센다. ExitAt이 없는 legacy 거래, 이전 세션의 청산, 완료 봉 근거가
    /// 없는 요청은 차단 근거로 쓰지 않는다.
    /// </summary>
    static bool StopCooldownActive(List<SimTrade> trades, StructuralEntryRequest request, int cooldownBars)
    {
        if (cooldownBars <= 0 || request.CompletedBarStarts is not { Count: > 0 } bars) return false;

        DateTimeOffset? latestStop = null;
        foreach (var trade in trades)
        {
            if (!trade.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(trade.Status, "STOP", StringComparison.Ordinal)) continue;
            if (trade.ExitAt is not { } exit) continue;
            if (latestStop is null || exit > latestStop) latestStop = exit;
        }
        if (latestStop is not { } stoppedAt) return false;
        if (request.SessionStart is { } sessionStart && stoppedAt < sessionStart) return false;

        return BarCounting.CompletedBarsSince(stoppedAt, bars, request.TriggerBarStart) is { } completed
               && completed < cooldownBars;
    }

    /// <summary>
    /// #245 U5 §10 전종목 손절 쿨다운/일일 손절 상한 판정. 실시간·replay가 공유하는 단일 함수다.
    /// <paramref name="request"/>.CrossSymbolSessionStopExits에는 호출자가 이번 세션의 전종목 STOP 청산 시각(진입 시각 이하)만
    /// 넣는다 — 미래 청산을 넣으면 룩어헤드다. 둘 중 하나라도 걸리면 차단한다:
    /// (1) <see cref="StructurePolicy.CrossSymbolStopCooldownMinutes"/> 분 안에 든 청산이 하나라도 있으면,
    /// (2) 진입 시각 이하의 세션 누적 STOP 수가 <see cref="StructurePolicy.MaxDailyStops"/> 이상이면.
    /// </summary>
    public static bool CrossSymbolStopCooldownActive(StructuralEntryRequest request, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.CrossSymbolStopCooldownMinutes is not > 0 && policy.MaxDailyStops is not > 0) return false;
        if (request.CrossSymbolSessionStopExits is not { Count: > 0 } exits) return false;
        var now = request.EnteredAt;

        if (policy.MaxDailyStops is { } maxStops && maxStops > 0 && exits.Count(x => x <= now) >= maxStops)
            return true;

        if (policy.CrossSymbolStopCooldownMinutes is { } minutes && minutes > 0)
        {
            var window = TimeSpan.FromMinutes(minutes);
            foreach (var exit in exits)
                if (exit <= now && now - exit < window) return true;
        }
        return false;
    }

    /// <summary>
    /// #111 재진입 코호트 태그. 같은 심볼의 가장 최근 청산 거래를 직전 거래로 보고 관측값만 남긴다 — 이 값으로
    /// 진입을 막거나 허용하지 않는다. 직전 거래가 없으면 모든 값이 null이고(첫 진입), 셀 수 없는 값은
    /// 추정하지 않고 null로 둔다(§16A). 봉 수는 쿨다운과 같은 세기 규칙(<see cref="BarCounting"/>)이다.
    /// </summary>
    static ReentryTags Reentry(List<SimTrade> trades, StructuralEntryRequest request)
    {
        SimTrade? previous = null;
        foreach (var trade in trades)
        {
            if (!trade.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (trade.ExitAt is not { } exit) continue;
            if (previous?.ExitAt is not { } best || exit > best) previous = trade;
        }
        if (previous?.ExitAt is not { } exitedAt) return new ReentryTags(null, null, null, null, null);

        // 이전 세션의 청산은 이번 세션의 완료 봉으로 셀 수 없다. 사유·품질 태그는 그대로 남긴다.
        var withinSession = request.SessionStart is not { } start || exitedAt >= start;
        var bars = withinSession
            ? BarCounting.CompletedBarsSince(exitedAt, request.CompletedBarStarts, request.TriggerBarStart)
            : null;
        return new ReentryTags(bars, previous.Status, SameTargetZone(previous, request),
            previous.Structure?.EntryQualityAtEntry, previous.Structure?.TrendAtEntry);
    }

    /// <summary>직전 거래의 목표 구간이 이번 계획과 같은 lineage인지. 직전 거래에 동결 계획이 없으면 판정 불가(null).</summary>
    static bool? SameTargetZone(SimTrade previous, StructuralEntryRequest request)
    {
        if (previous.Structure?.PlanSnapshot.TargetZoneId is not { Length: > 0 } previousZone) return null;
        if (string.Equals(previousZone, request.Context.PlanSnapshot.TargetZoneId, StringComparison.Ordinal))
            return true;
        return request.TargetZoneAliases?.Contains(previousZone, StringComparer.Ordinal) == true;
    }
}
