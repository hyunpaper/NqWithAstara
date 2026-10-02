using Astra.Server;
using Astra.Server.Domain;
using Astra.Server.Domain.Validation;

/// <summary>
/// 이슈 #28 검증 fixture. 전부 코드로 만든 결정적 값이며 운영 실데이터가 아니다(운영 데이터는 커밋하지 않는다).
/// 세션 하루는 <see cref="SessionOn"/>의 New York 거래일 하나에 대응한다.
/// </summary>
static class Vx
{
    public const string Symbol = "TEST";
    public const string Engine = "v5-structure.2";
    public const string Policy = "hash-A";

    public static readonly DateTimeOffset Session = new(2026, 9, 9, 9, 30, 0, TimeSpan.FromHours(-4));

    /// <summary>평가 기준 시각. fixture의 모든 관측·거래보다 충분히 뒤다.</summary>
    public static readonly DateTimeOffset AsOf = Session.AddDays(30);

    public static DateTimeOffset SessionOn(int day) => Session.AddDays(day);

    public static DateTimeOffset At(int day, int minute) => SessionOn(day).AddMinutes(minute);

    public static ObservationCandidateRow Candidate(string eventId, string state = "READY", double? quality = 60,
        string kind = "PULLBACK", bool planned = true, bool missingLiquidity = false, decimal spread = 0.02m,
        string[]? rejections = null, int day = 0, int minute = 40) =>
        new(eventId, kind, "zone-1", state, quality, At(day, minute - 1), At(day, minute), At(day, minute + 5),
            planned ? missingLiquidity : null,
            planned && !missingLiquidity ? spread : null,
            planned ? "cost-eligibility.1" : null, planned ? "cost-fill.1" : null, planned ? 1.6m : null,
            rejections ?? [], []);

    public static ObservationRow Row(string observationId, ObservationCandidateRow[] candidates, int day = 0,
        int minute = 41, string symbol = Symbol, string engine = Engine, string policyHash = Policy,
        string mode = "active", string detail = "full", string? trend = "UP", string[]? warnings = null,
        DateTimeOffset? observedAt = null) =>
        new(observationId, "v5-observation.1", symbol, observedAt ?? At(day, minute), SessionOn(day),
            At(day, minute), observedAt ?? At(day, minute), At(day, minute - 1), engine, policyHash, mode, detail,
            "available", trend, warnings ?? [], candidates);

    public static FrozenPlanSnapshot Plan(bool missingLiquidity = false, string kind = "PULLBACK",
        string engine = Engine, string policyHash = Policy, decimal netR = 1.6m) =>
        new("plan-1", kind, 100m, 99.6m, 99.4m, 101.5m, "z-support", 99.4m, 99.7m, "z-resist", 101.5m, 101.9m,
            .05m, "session-atr", .02m, 1.3m, .8m, netR, .6, .1m, .02m, missingLiquidity ? null : .04m,
            missingLiquidity, "cost-eligibility.1", "cost-fill.1", At(0, 40), At(0, 45), engine, policyHash,
            missingLiquidity ? ["MISSING_LIQUIDITY_COST"] : [], "지지 반응 후 저항 하단 앞 계획");

    /// <summary>v5 시뮬 거래. 동결 컨텍스트의 EventId가 관측 후보와의 유일한 연결 고리다.</summary>
    public static SimTrade Trade(string id, string eventId, double? pnl = 1.2, string status = "TARGET",
        int day = 0, int entryMinute = 42, int exitMinute = 60, bool missingLiquidity = false, double? quality = 60,
        string trend = "UP", bool? exitEstimated = false, string symbol = Symbol, string engine = Engine,
        string policyHash = Policy, DateTimeOffset? exitAt = null, string kind = "PULLBACK")
    {
        var open = status == "OPEN";
        return new SimTrade(id, symbol, kind, At(day, entryMinute), 100, 101.5, 99.4, null, null, status,
            open ? null : 101.5, open ? null : exitAt ?? At(day, exitMinute), open ? null : pnl, 101.5,
            Logic: engine, ExitEstimated: open ? null : exitEstimated,
            Structure: new FrozenStructureContext(eventId, Plan(missingLiquidity, kind, engine, policyHash), trend,
                40.0, quality, At(day, entryMinute), At(day, entryMinute), StructuralSimulation.ExitPolicyVersion));
    }

    /// <summary>진입까지 간 이벤트 한 건(관측 + 거래)을 한 번에 만든다.</summary>
    public static (ObservationRow Row, SimTrade Trade) Entered(string eventId, double pnl, int day = 0,
        double? quality = 60, string symbol = Symbol, bool missingLiquidity = false, string kind = "PULLBACK",
        string trend = "UP", bool exitEstimated = false) =>
        (Row($"obs-{eventId}", [Candidate(eventId, "ENTERED", quality, kind, missingLiquidity: missingLiquidity,
                day: day)], day, symbol: symbol, trend: trend),
            Trade($"t-{eventId}", eventId, pnl, day: day, missingLiquidity: missingLiquidity, quality: quality,
                trend: trend, exitEstimated: exitEstimated, symbol: symbol, kind: kind));

    public static LinkResult Link(IReadOnlyList<ObservationRow> rows, IReadOnlyList<SimTrade> trades,
        DateTimeOffset? asOf = null) =>
        CandidateTradeLinker.Link(rows, trades, new LinkOptions(asOf ?? AsOf));

    /// <summary>표본 기준을 낮춘 평가. 기본 임계값은 운영 계약이므로 테스트에서만 명시적으로 완화한다.</summary>
    public static readonly EvaluationThresholds Small = new(3, 1, 1, 100d);
}
