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
    BlockedByMissingLiquidityCost
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
    EntryConfirmation? Confirmation = null, bool RequireCompleteLiquidityCost = false);

public sealed record StructuralEntryResult(List<SimTrade> Trades, StructuralEntryOutcome Outcome, SimTrade? Trade);

public static class StructuralSimulation
{
    /// <summary>
    /// v5 청산 정책 버전(§11 StructuralExitPolicyVersion). 동결된 구조 Stop/Target과 EOD만으로 관리하고
    /// 조기 청산/트레일링/v4 CUT을 적용하지 않는다는 계약을 저장 데이터에 남긴다.
    /// </summary>
    public const string ExitPolicyVersion = "v5-exit.frozen-plan.1";

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
        double? signedTrendAtEntry, double? entryQualityAtEntry, DateTimeOffset analysisAsOf, DateTimeOffset? quoteAt)
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
        return new FrozenStructureContext(entryEventId, snapshot, trendAtEntry, signedTrendAtEntry,
            entryQualityAtEntry, analysisAsOf, quoteAt, ExitPolicyVersion, null, plan.Regime, plan.Evidence);
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

        var existing = trades.FirstOrDefault(x =>
            string.Equals(x.Structure?.EntryEventId, request.Context.EntryEventId, StringComparison.Ordinal));
        if (existing is not null) return new StructuralEntryResult(trades, StructuralEntryOutcome.AlreadyEntered, existing);

        if (trades.Any(x => x.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase) && x.Status == "OPEN"))
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByOpenTrade, null);

        if (StopCooldownActive(trades, request, (policy ?? StructurePolicy.Default).StopReentryCooldownBars))
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByStopCooldown, null);

        var plan = request.Context.PlanSnapshot;
        var entry = request.Confirmation is { Decision: PendingEntryDecision.Confirmed, FillPrice: > 0 } confirmation
            ? confirmation.FillPrice!.Value : (double)plan.EntryReference;
        var stop = (double)plan.Stop;
        var target = (double)plan.Target;
        var ordered = plan.Side == TradeSide.Long
            ? stop > 0 && stop < entry && target > entry
            : stop > entry && target > 0 && target < entry;
        if (!ordered)
            return new StructuralEntryResult(trades, StructuralEntryOutcome.InvalidPlan, null);
        if (request.RequireCompleteLiquidityCost && plan.MissingLiquidity)
            return new StructuralEntryResult(trades, StructuralEntryOutcome.BlockedByMissingLiquidityCost, null);

        // 결정적 ID: 같은 이벤트의 재시도가 다른 거래처럼 보이지 않게 한다(§16B 재시작 규칙과 같은 방향).
        var id = StructureMath.SourceId("simtrade", request.Symbol, request.Context.EntryEventId)[..8];
        var context = request.Context with { PlanSnapshot = plan, Reentry = Reentry(trades, request) };
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
