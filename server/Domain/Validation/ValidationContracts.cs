namespace Astra.Server.Domain.Validation;

// 이슈 #28 — v5 유효성 검증의 데이터 계약(Domain 절반).
// 관측 JSONL과 거래 스냅샷(FrozenStructureContext)을 잇기 위한 "평탄한 입력 행"과 연결 결과만 정의한다.
// 저장 형식(JSON 필드명·파일 이름·보존 기간)은 Application이 알고, 여기에는 계산에 필요한 값만 들어온다(§4).
// 규칙: 미지 값은 null이며 0·false로 대체하지 않는다(§11). 관측되지 않은 것을 "통과"로 바꾸지 않는다.

/// <summary>
/// 관측 레코드 한 줄에 들어 있던 후보 하나. 설계 §11 EntryCandidate / §16 관측 필드의 부분집합이다.
/// <para>
/// <see cref="MissingLiquidityCost"/>·비용 모델 버전·<see cref="PlannedNetR"/>은 계획(Plan)이 성립한 후보에만 존재한다.
/// 계획이 없는 거절·대기 후보에서는 null이며, 이는 "비용 정상"이 아니라 "미수집"이다(#28 데이터 감사 항목).
/// </para>
/// </summary>
public sealed record ObservationCandidateRow(string EventId, string Kind, string ZoneId, string State,
    double? EntryQuality, DateTimeOffset TriggerBarStart, DateTimeOffset TriggerConfirmedAt,
    DateTimeOffset ExpiresAt, bool? MissingLiquidityCost, decimal? ValidSpread,
    string? EligibilityCostModelVersion, string? RealizedFillCostModelVersion, decimal? PlannedNetR,
    IReadOnlyList<string> RejectionCodes, IReadOnlyList<string> Notes);

/// <summary>
/// 관측 파일 한 줄(<c>structure-observations-YYYY-MM-DD.jsonl</c>)의 연결에 필요한 부분.
/// <see cref="ObservedAt"/>은 기록 시각, <see cref="AnalysisAsOf"/>는 계산에 쓰인 마지막 완료 봉의 종료 시각이다.
/// <see cref="Mode"/>(shadow/active)는 실행 성격이 다른 별개 집단이므로 키의 일부다 — 섞어 집계하지 않는다.
/// </summary>
public sealed record ObservationRow(string ObservationId, string RecordVersion, string Symbol,
    DateTimeOffset ObservedAt, DateTimeOffset SessionStart, DateTimeOffset AnalysisAsOf, DateTimeOffset? QuoteAt,
    DateTimeOffset LastCompletedBarStart, string EngineVersion, string PolicyHash, string Mode, string Detail,
    string Status, string? TrendState, IReadOnlyList<string> Warnings,
    IReadOnlyList<ObservationCandidateRow> Candidates);

/// <summary>
/// 후보의 최종 상태. 관측 문자열을 그대로 믿지 않고 알 수 없는 값은 <see cref="Unknown"/>으로 남긴다.
/// JSON에는 숫자가 아니라 이름으로 나간다.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CandidateOutcome>))]
public enum CandidateOutcome { Wait, Ready, Rejected, Invalidated, Expired, Entered, Unknown }

/// <summary>
/// 호가 결측/정상 집단 분리(#28). 현 정책은 호가가 없으면 spread=0으로 자격을 평가하므로
/// "결측"과 "관측된 0 스프레드"를 반드시 구분한다 — 두 집단을 합치면 비용 가정의 효과가 보이지 않는다.
/// </summary>
public static class LiquidityClass
{
    public const string Missing = "LIQUIDITY_MISSING";
    public const string ObservedZeroSpread = "LIQUIDITY_ZERO_SPREAD";
    public const string Observed = "LIQUIDITY_OBSERVED";
    public const string Uncollected = "LIQUIDITY_UNCOLLECTED";

    /// <summary>계획이 없는 후보(=비용 가정 미수집)는 <see cref="Uncollected"/>다. false+null은 모순이므로 결측으로 본다.</summary>
    public static string Of(bool? missingLiquidityCost, decimal? validSpread) => missingLiquidityCost switch
    {
        null => Uncollected,
        true => Missing,
        false when validSpread is null => Missing,
        false when validSpread.Value == 0m => ObservedZeroSpread,
        _ => Observed
    };
}

/// <summary>
/// 후보와 연결된 실제 시뮬 거래. 숫자는 전부 저장된 거래·동결 계획에서 오며 여기서 재계산하지 않는다(§10).
/// <see cref="OutcomeKnown"/>=false는 평가 시각(AsOf)에 아직 청산 결과를 알 수 없는 건이다(미래 누출 차단).
/// </summary>
public sealed record LinkedTrade(string TradeId, string Symbol, string StatusAsOf, string StoredStatus,
    DateTimeOffset EnteredAt, DateTimeOffset? ExitAt, double? PnlPercent, bool OutcomeKnown, bool? ExitEstimated,
    bool MissingLiquidityCost, decimal? ValidSpread, decimal PlannedNetR, string EligibilityCostModelVersion,
    string RealizedFillCostModelVersion, string EngineVersion, string PolicyHash, string Kind, string TrendAtEntry,
    double? EntryQualityAtEntry, string EntryEventId);

/// <summary>
/// 후보 → 결정 → 거래 → 결과 체인 하나. <b>EventId 기준으로 한 건</b>이며 같은 이벤트의 반복 poll은
/// <see cref="ObservationCount"/>/<see cref="PollRowsCollapsed"/>로만 남고 독립 표본이 되지 않는다(#28).
/// </summary>
public sealed record LinkedCandidate(string EventId, string Symbol, DateOnly TradingDate,
    DateTimeOffset SessionStart, string Mode, string EngineVersion, string PolicyHash, string Kind,
    string? TrendState, double? EntryQuality, CandidateOutcome Outcome, string FinalState,
    DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt, int ObservationCount, int PollRowsCollapsed,
    bool SummaryOnly, IReadOnlyList<string> RejectionCodes, string? PrimaryRejectReason,
    bool? MissingLiquidityCost, decimal? ValidSpread, string Liquidity, string? EligibilityCostModelVersion,
    string? RealizedFillCostModelVersion, IReadOnlyList<string> DataQualityWarnings, LinkedTrade? Trade)
{
    /// <summary>평가 시각 기준으로 실현 손익이 확정된 연결 건인지. 청산이 AsOf 이후면 false다.</summary>
    public bool HasRealizedPnl => Trade is { OutcomeKnown: true, PnlPercent: { } pnl } && double.IsFinite(pnl);

    /// <summary>실현 손익(%). 확정되지 않았으면 null이며 0으로 대체하지 않는다.</summary>
    public double? RealizedPnlPercent => HasRealizedPnl ? Trade!.PnlPercent : null;
}

/// <summary>
/// 연결 과정에서 확인한 사실. 여기 수치가 "검증 통과"를 뜻하지 않으며, 연결되지 못한 건이 몇 개인지를 드러내는 용도다.
/// <see cref="Conflicts"/>·<see cref="Limitations"/>는 정렬된 코드 목록이라 회귀 테스트로 고정할 수 있다.
/// </summary>
public sealed record LinkAudit(int ObservationRows, int ObservationRowsAfterCutoff, int DuplicateObservationIds,
    int CandidateRows, int DuplicatePollRows, int DistinctEvents, int DroppedEvents, int SummaryOnlyEvents,
    int Symbols, int Sessions, DateOnly? FirstSession, DateOnly? LastSession, int TradesTotal, int TradesV5,
    int TradesLinked, int TradesUnlinked, int TradesAfterCutoff, int OutcomesCensoredByCutoff,
    IReadOnlyList<string> Conflicts, IReadOnlyList<string> Limitations);

/// <summary>연결 결과. <see cref="UnlinkedTrades"/>는 관측에서 근거 후보를 찾지 못한 v5 거래다(관측 보존 한계의 증거).</summary>
public sealed record LinkResult(IReadOnlyList<LinkedCandidate> Candidates, IReadOnlyList<LinkedTrade> UnlinkedTrades,
    LinkAudit Audit);

/// <summary>
/// 연결 옵션. <see cref="AsOf"/>보다 뒤에 기록된 관측·진입·청산은 사용하지 않는다 —
/// "과거 확정 시각만 사용"(#28 시간 규율)을 입력 단계에서 강제하는 유일한 장치다.
/// </summary>
public sealed record LinkOptions(DateTimeOffset AsOf)
{
    /// <summary>지정하면 해당 엔진 버전의 관측만 사용한다. 버전을 섞어 평가하지 않기 위한 선택적 필터다.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>지정하면 해당 정책 해시의 관측만 사용한다.</summary>
    public string? PolicyHash { get; init; }

    /// <summary>지정하면 해당 실행 모드(shadow/active)의 관측만 사용한다.</summary>
    public string? Mode { get; init; }
}

/// <summary>
/// #111: 후보가 READY인데도 진입이 이뤄지지 않은 사유 코드. Application이 관측 레코드에 남기고(#107 RejectionCodes)
/// 여기서 집계한다 — "1 poll 지연"과 "쿨다운 차단"을 한 숫자로 합치지 않기 위해 코드를 분리해 둔다.
/// </summary>
public static class EntryBlockCodes
{
    /// <summary>#106: 같은 poll에 청산이 있어 이번 poll의 진입만 건너뛴 건. 쿨다운이 아니다.</summary>
    public const string SuppressedBySamePollExit = "V5_ENTRY_SUPPRESSED_BY_SAME_POLL_EXIT";

    /// <summary>#117 §10: 같은 심볼의 직전 손절 이후 완료 봉이 정책 개수만큼 쌓이지 않아 막힌 건.</summary>
    public const string BlockedByStopCooldown = "V5_ENTRY_BLOCKED_BY_STOP_COOLDOWN";

    public static string Label(string code) => code switch
    {
        SuppressedBySamePollExit => "같은 poll 청산으로 1 poll 지연 (#106)",
        BlockedByStopCooldown => "직전 손절 쿨다운 차단 (#117)",
        _ => code
    };
}

/// <summary>연결 단계에서 남기는 코드. 문자열을 흩어 쓰지 않고 한곳에서 정의한다.</summary>
public static class LinkCodes
{
    public const string DuplicateObservationId = "DUPLICATE_OBSERVATION_ID";
    public const string EventVersionMixed = "EVENT_VERSION_MIXED";
    public const string EventModeMixed = "EVENT_MODE_MIXED";
    public const string EventSymbolMixed = "EVENT_SYMBOL_MIXED";
    public const string ObservationTimeOrder = "OBSERVATION_TIME_ORDER";
    public const string TradeVersionMismatch = "TRADE_VERSION_MISMATCH";
    public const string TradeSymbolMismatch = "TRADE_SYMBOL_MISMATCH";
    public const string TradeWithoutObservation = "TRADE_WITHOUT_OBSERVATION";

    /// <summary>계획이 없는 후보에는 비용 가정이 저장되지 않는다 — 비용 코호트 분리가 계획 성립 건에만 가능하다는 한계.</summary>
    public const string CostAssumptionOnlyForPlannedCandidates = "COST_ASSUMPTION_ONLY_FOR_PLANNED_CANDIDATES";

    /// <summary>거절 후보의 사후 가격 경로는 관측 파일에 없다 — 가상 평가는 별도 입력이 있어야 가능하다.</summary>
    public const string RejectedPathNotRetained = "REJECTED_PATH_NOT_RETAINED";

    /// <summary>관측은 완료 봉 단위 요약이라 poll 사이의 중간 상태는 복원할 수 없다.</summary>
    public const string SubBarStateNotRetained = "SUB_BAR_STATE_NOT_RETAINED";

    /// <summary>요약(summary) 관측에는 Zone 배열·품질 상세가 없다. 요약만 남은 이벤트는 근거 재현 범위가 좁다.</summary>
    public const string SummaryObservationOmitsEvidence = "SUMMARY_OBSERVATION_OMITS_EVIDENCE";

    /// <summary>거래 저장소는 최신 500건만 보존한다(SimulationEngine). 거래 부재가 "진입한 적 없음"의 증거가 아니다.</summary>
    public const string TradeRetentionCapped = "TRADE_RETENTION_CAPPED_500";

    /// <summary>관측 파일은 거래일당 20 MiB에서 append가 멈춘다(§16). 상한에 닿은 날은 후보 전수가 아니다.</summary>
    public const string ObservationRetentionCapped = "OBSERVATION_RETENTION_CAPPED";
}
