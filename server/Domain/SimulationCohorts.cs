namespace Astra.Server.Domain;

// 이슈 #27 — v5 시뮬레이션 코호트 집계의 Domain 절반.
// 원천은 각 거래에 저장된 FrozenStructureContext뿐이다(§10/§11): 진입 이후의 재계산 값으로
// 과거 거래를 채우지 않으며, 컨텍스트가 없는 v5 거래는 추정하지 않고 "미수집" 코호트로 남긴다.
// 순수 함수만 두고 시계·저장소·HTTP에 의존하지 않는다(ArchitectureBoundaryTests).

/// <summary>
/// 코호트 하나의 성과 요약. 승률의 분모는 <see cref="ValidClosed"/>(손익이 유효한 청산 건)이며
/// 표본이 없으면 0이 아니라 null이다. <see cref="AvgPnl"/>은 왕복 수수료 0.2%가 차감된 실현 손익 평균이고,
/// <see cref="AvgPlannedNetR"/>은 동결 계획(FrozenPlan)의 비용 모델 기준 계획값이다 — 실현 손익이 아니다.
/// </summary>
public sealed record CohortStats(int Total, int Open, int Closed, int ValidClosed, int Wins,
    double? WinRate, double? AvgPnl, int MissingPnl, int EstimatedExits,
    double? AvgPlannedNetR, int PlannedNetRSamples);

/// <summary><see cref="Collected"/>=false는 값이 수집되지 않았음을 뜻한다(0·"검증 완료"로 표시하지 않는다).</summary>
public sealed record SimulationCohort(string Key, string Label, bool Collected, CohortStats Stats);

public sealed record SimulationCohortGroup(string Dimension, string Title, IReadOnlyList<SimulationCohort> Cohorts);

/// <summary>
/// v5 소유 거래(<see cref="StructuralSimulation.OwnsTrade"/>)만의 코호트 보고서.
/// v4/legacy 거래는 기존 버전별 집계(ByVersion)가 담당하므로 여기 들어오지 않는다.
/// <see cref="ContextMissing"/>은 v5 거래인데 동결 컨텍스트가 없는(부분 손상) 건수다.
/// </summary>
public sealed record StructureCohortReport(CohortStats V5Stats, int ContextMissing,
    IReadOnlyList<SimulationCohortGroup> Groups);

public static class SimulationCohorts
{
    public const string ContextMissingKey = "CONTEXT_MISSING";
    public const string QualityUncollectedKey = "QUALITY_UNCOLLECTED";
    public const string MissingLiquidityKey = "MISSING_LIQUIDITY_COST";
    public const string CostOkKey = "COST_OK";

    /// <summary>#111 재진입 태그 자체가 없는 거래(태그 도입 이전 데이터). 첫 진입으로 바꾸지 않는다.</summary>
    public const string ReentryUntaggedKey = "REENTRY_UNTAGGED";
    public const string FirstEntryKey = "FIRST_ENTRY";
    public const string ReentryBarsUncollectedKey = "REENTRY_BARS_UNCOLLECTED";
    public const string SameTargetZoneKey = "SAME_TARGET_ZONE";
    public const string OtherTargetZoneKey = "OTHER_TARGET_ZONE";
    public const string TargetZoneUncollectedKey = "TARGET_ZONE_UNCOLLECTED";

    const string ContextMissingLabel = "동결 컨텍스트 누락 (미수집)";

    static readonly HashSet<string> UncollectedKeys = new(StringComparer.Ordinal)
    {
        QualityUncollectedKey, ReentryUntaggedKey, ReentryBarsUncollectedKey, TargetZoneUncollectedKey
    };

    public static StructureCohortReport Build(IReadOnlyList<SimTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        var v5 = trades.Where(StructuralSimulation.OwnsTrade).ToArray();
        var groups = new[]
        {
            Group(v5, "engineVersion", "엔진 버전", t => t.Structure!.PlanSnapshot.EngineVersion, key => key),
            Group(v5, "policyHash", "정책 해시", t => t.Structure!.PlanSnapshot.PolicyHash, key => key),
            Group(v5, "exitPolicy", "청산 정책", t => t.Structure!.StructuralExitPolicyVersion, key => key),
            Group(v5, "costModel", "비용 모델 (자격 / 실현 체결)",
                t => t.Structure!.PlanSnapshot.EligibilityCostModelVersion + " / " +
                     t.Structure!.PlanSnapshot.RealizedFillCostModelVersion, key => key),
            Group(v5, "dataQuality", "데이터 품질 (비용 산정)",
                t => t.Structure!.PlanSnapshot.MissingLiquidity ? MissingLiquidityKey : CostOkKey,
                key => key == MissingLiquidityKey ? "비용 결측 — 호가 없음 · 스프레드 0 가정" : "비용 산정 정상"),
            Group(v5, "entryQuality", "진입 품질 구간 (EntryQuality)", QualityBand, QualityLabel, QualityOrder),
            Group(v5, "trend", "진입 시점 추세", t => t.Structure!.TrendAtEntry, TrendLabel, TrendOrder),
            Group(v5, "setup", "셋업 종류", t => t.Structure!.PlanSnapshot.Kind, SetupLabel),
            Group(v5, "reentry", "재진입 간격 (직전 청산 봉 이후 완료 봉)", ReentryBand, ReentryLabel, ReentryOrder),
            Group(v5, "reentryTargetZone", "직전 거래와 목표 구간 일치", TargetZoneBand, TargetZoneLabel,
                TargetZoneOrder),
            Group(v5, "reentryPrevExit", "직전 거래 청산 사유", PrevExitBand, PrevExitLabel, PrevExitOrder)
        };
        return new StructureCohortReport(Stats(v5), v5.Count(t => t.Structure is null), groups);
    }

    /// <summary>
    /// EntryQuality(0~100)의 중립 구간. 순위 지표라 좋음/나쁨 라벨을 붙이지 않는다(§9.4 — BUY=70 같은 기준 금지).
    /// null은 진입 당시에도 수집되지 않은 값이므로 구간에 넣지 않고 미수집으로 남긴다.
    /// </summary>
    static string QualityBand(SimTrade trade) => trade.Structure!.EntryQualityAtEntry switch
    {
        null => QualityUncollectedKey,
        < 25 => "Q0_25",
        < 50 => "Q25_50",
        < 75 => "Q50_75",
        _ => "Q75_100"
    };

    static string QualityLabel(string key) => key switch
    {
        "Q0_25" => "0 ~ 25",
        "Q25_50" => "25 ~ 50",
        "Q50_75" => "50 ~ 75",
        "Q75_100" => "75 ~ 100",
        _ => "EntryQuality 미수집"
    };

    static int QualityOrder(string key) => key switch
    {
        "Q0_25" => 0, "Q25_50" => 1, "Q50_75" => 2, "Q75_100" => 3, _ => 4
    };

    static string TrendLabel(string key) => key switch
    {
        "UP" => "상승", "DOWN" => "하락", "RANGE" => "횡보", "TRANSITION" => "전환",
        "UNKNOWN" => "판정 불가", _ => key
    };

    static int TrendOrder(string key) => key switch
    {
        "UP" => 0, "TRANSITION" => 1, "RANGE" => 2, "DOWN" => 3, "UNKNOWN" => 4, _ => 5
    };

    /// <summary>
    /// #111 재진입 간격 구간. 임계값 탐색이 아니라 관측 분리이므로 좁은 구간부터 고정 경계로 나눈다.
    /// 태그 없음(도입 이전)과 봉 수 미수집은 첫 진입과 다른 코호트로 남긴다.
    /// </summary>
    static string ReentryBand(SimTrade trade) => trade.Structure!.Reentry switch
    {
        null => ReentryUntaggedKey,
        { PrevExitStatus: null } => FirstEntryKey,
        { SameSymbolWithinBars: null } => ReentryBarsUncollectedKey,
        { SameSymbolWithinBars: <= 2 } => "R0_2",
        { SameSymbolWithinBars: <= 5 } => "R3_5",
        _ => "R6_PLUS"
    };

    static string ReentryLabel(string key) => key switch
    {
        FirstEntryKey => "첫 진입 (직전 거래 없음)",
        "R0_2" => "0 ~ 2봉 이내 재진입",
        "R3_5" => "3 ~ 5봉 재진입",
        "R6_PLUS" => "6봉 이상 재진입",
        ReentryBarsUncollectedKey => "재진입이지만 봉 수 미수집",
        _ => "재진입 태그 미수집"
    };

    static int ReentryOrder(string key) => key switch
    {
        FirstEntryKey => 0, "R0_2" => 1, "R3_5" => 2, "R6_PLUS" => 3, ReentryBarsUncollectedKey => 4, _ => 5
    };

    static string TargetZoneBand(SimTrade trade) => trade.Structure!.Reentry switch
    {
        null => ReentryUntaggedKey,
        { PrevExitStatus: null } => FirstEntryKey,
        { SameTargetZone: null } => TargetZoneUncollectedKey,
        { SameTargetZone: true } => SameTargetZoneKey,
        _ => OtherTargetZoneKey
    };

    static string TargetZoneLabel(string key) => key switch
    {
        FirstEntryKey => "첫 진입 (직전 거래 없음)",
        SameTargetZoneKey => "직전 거래와 같은 목표 구간 (lineage 포함)",
        OtherTargetZoneKey => "다른 목표 구간",
        TargetZoneUncollectedKey => "직전 거래에 동결 계획 없음 (미수집)",
        _ => "재진입 태그 미수집"
    };

    static int TargetZoneOrder(string key) => key switch
    {
        FirstEntryKey => 0, SameTargetZoneKey => 1, OtherTargetZoneKey => 2, TargetZoneUncollectedKey => 3, _ => 4
    };

    static string PrevExitBand(SimTrade trade) => trade.Structure!.Reentry switch
    {
        null => ReentryUntaggedKey,
        { PrevExitStatus: null } => FirstEntryKey,
        { PrevExitStatus: { } status } => "PREV_" + status.ToUpperInvariant()
    };

    static string PrevExitLabel(string key) => key switch
    {
        FirstEntryKey => "첫 진입 (직전 거래 없음)",
        ReentryUntaggedKey => "재진입 태그 미수집",
        "PREV_STOP" => "직전 손절 (STOP)",
        "PREV_TARGET" => "직전 목표 도달 (TARGET)",
        "PREV_EOD" => "직전 장 마감 청산 (EOD)",
        "PREV_CUT" => "직전 강제 청산 (CUT)",
        _ => key
    };

    static int PrevExitOrder(string key) => key == FirstEntryKey ? 0 : 1;

    static string SetupLabel(string key) => key switch
    {
        "PULLBACK" => "눌림목 (PULLBACK)", "BREAKOUT" => "돌파 (BREAKOUT)", "REBOUND" => "과매도 반등 (REBOUND)",
        _ => key
    };

    /// <summary>
    /// 한 차원의 코호트 분할. 컨텍스트 없는 거래는 어느 차원에서도 추정하지 않고 CONTEXT_MISSING 한 코호트로
    /// 모은다 — 그래서 모든 차원에서 코호트 합계 = v5 전체가 성립한다(전체/필터 집계 일치).
    /// </summary>
    static SimulationCohortGroup Group(IReadOnlyList<SimTrade> v5, string dimension, string title,
        Func<SimTrade, string> keyOf, Func<string, string> labelOf, Func<string, int>? orderOf = null)
    {
        var cohorts = v5
            .GroupBy(t => t.Structure is null ? ContextMissingKey : keyOf(t), StringComparer.Ordinal)
            .Select(g => new SimulationCohort(g.Key,
                g.Key == ContextMissingKey ? ContextMissingLabel : labelOf(g.Key),
                Collected: g.Key != ContextMissingKey && !UncollectedKeys.Contains(g.Key),
                Stats(g.ToArray())))
            .OrderBy(c => c.Key == ContextMissingKey ? 1 : 0)
            .ThenBy(c => orderOf?.Invoke(c.Key) ?? 0)
            .ThenByDescending(c => c.Stats.Total)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();
        return new SimulationCohortGroup(dimension, title, cohorts);
    }

    static CohortStats Stats(IReadOnlyList<SimTrade> rows)
    {
        var closed = rows.Where(x => x.Status != "OPEN").ToArray();
        var valid = closed.Where(x => x.PnlPercent is { } pnl && double.IsFinite(pnl)).ToArray();
        var wins = valid.Count(x => x.PnlPercent > 0);
        // 계획 netR은 진입 시점에 동결된 계획값이라 OPEN 거래에도 존재한다. 실현 손익과 절대 합산하지 않는다.
        var plannedNetR = rows.Where(x => x.Structure is not null)
            .Select(x => (double)x.Structure!.PlanSnapshot.NetR).Where(double.IsFinite).ToArray();
        return new CohortStats(rows.Count, rows.Count(x => x.Status == "OPEN"), closed.Length, valid.Length, wins,
            valid.Length == 0 ? null : Math.Round(wins * 100d / valid.Length, 1),
            valid.Length == 0 ? null : Math.Round(valid.Average(x => x.PnlPercent!.Value), 2),
            closed.Length - valid.Length, closed.Count(x => x.ExitEstimated == true),
            plannedNetR.Length == 0 ? null : Math.Round(plannedNetR.Average(), 2), plannedNetR.Length);
    }
}
