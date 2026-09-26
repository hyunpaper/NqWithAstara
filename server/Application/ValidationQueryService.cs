using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;

namespace Astra.Server.Application;

// 이슈 #28 — v5 유효성 검증 조회 서비스(Application 절반).
//
// 이 서비스는 읽기 전용이다. 관측 파일과 simtrades를 읽어 Domain 링커·평가기에 넘길 뿐이며
// 운영 진입/청산·점수 공식·호가 결측 정책을 바꾸지 않고 어떤 파일에도 쓰지 않는다.
// 저장 형식(파일명·JSON 필드·보존 한계)을 아는 유일한 계층이며, Domain에는 평탄한 행만 넘긴다(§4).

/// <summary>관측 파일 하루치의 감사 결과. 파일이 없다는 사실도 결과의 일부다(보존 한계의 증거).</summary>
public sealed record ValidationFileAudit(string File, DateOnly TradingDate, bool Found, int Lines,
    int ParseFailures, long Bytes, bool NearDailyLimit);

/// <summary>필드 결측 감사. <paramref name="Missing"/>/<paramref name="Total"/>은 후보 행 기준이다.</summary>
public sealed record ValidationFieldGap(string Field, int Missing, int Total, string Note);

/// <summary>
/// 데이터 계약·보존 감사. "무엇이 있었는가"가 아니라 "무엇이 없거나 잘렸는가"를 드러내는 것이 목적이다.
/// </summary>
public sealed record ValidationDataAudit(DateOnly From, DateOnly To, int DaysScanned, int FilesFound,
    int Lines, int ParseFailures, long Bytes, long DailyByteLimit, IReadOnlyList<string> DaysWithoutFile,
    IReadOnlyList<ValidationFileAudit> Files, int TradeRecords, int TradeRecordLimit, bool TradeStoreAtLimit,
    IReadOnlyList<ValidationFieldGap> FieldGaps, IReadOnlyList<string> Contract);

/// <summary>
/// 검증 보고서. 모든 수치는 관측이며 승률·수익의 보장이 아니다.
/// <see cref="CostScenarios"/>는 별도 오프라인 시나리오로, 원본 실현 손익을 덮어쓰지 않는다.
/// </summary>
public sealed record ValidationReport(DateTimeOffset GeneratedAt, DateTimeOffset AsOf, int WindowDays,
    string EngineVersion, string PolicyHash, ValidationDataAudit Data, LinkAudit Link,
    ValidationEvaluation Evaluation, WalkForwardReport WalkForward,
    RiskFrequencyReport RiskFrequency, IReadOnlyList<CostScenarioResult> CostScenarios,
    IReadOnlyList<string> Limitations, ProbabilityCalibrationReport? ProbabilityCalibration = null);

public sealed class ValidationQueryService(ILocalStore store, IStructureObservationStore observations,
    TimeProvider clock, StructurePolicy? policy = null)
{
    public const int DefaultWindowDays = 30;
    public const int MaxWindowDays = 180;

    /// <summary>거래 저장소 보존 상한(<see cref="SimulationEngine"/>). 거래 부재가 "진입한 적 없음"의 증거가 아니다.</summary>
    public const int TradeRecordLimit = 500;

    readonly StructurePolicy _policy = policy ?? StructurePolicy.Default;

    /// <summary>관측 jsonl 해석은 #131과 공유하는 리더 하나만 쓴다(파싱 중복 금지).</summary>
    readonly ObservationLogReader _reader = new(observations);

    /// <summary>이 보고서가 의존하는 저장 계약. 문서(docs/v5-validation.md)와 같은 문장을 응답에도 남긴다.</summary>
    public static readonly string[] Contract =
    [
        "관측: App_Data/structure/structure-observations-YYYY-MM-DD.jsonl (New York 거래일 기준, 한 줄 = 한 관측 레코드).",
        "연결 키: (Symbol, SessionStart, EventId, EngineVersion, PolicyHash, Mode). SessionStart는 관측에서만 온다 — SimTrade는 세션 시작을 저장하지 않는다.",
        "거래: simtrades.json의 FrozenStructureContext(EntryEventId·동결 계획). 진입 이후 재계산 값으로 채우지 않는다.",
        "중복: 같은 EventId의 반복 poll은 최종 관측 한 건으로 접힌다. 같은 ObservationId의 재append는 첫 줄만 사용한다.",
        "시각: AsOf 이후 관측·진입은 제외하고, AsOf 이후 청산은 결과 미확정으로 절단한다."
    ];

    public async Task<(int HttpStatus, ValidationReport? Report)> GetAsync(int? days, CancellationToken ct)
    {
        var window = days ?? DefaultWindowDays;
        if (window < 1 || window > MaxWindowDays) return (400, null);

        var now = clock.GetLocalNow();
        var to = MarketRules.TradingDate(now);
        var from = to.AddDays(-(window - 1));

        var (rows, audit) = await ReadObservationsAsync(from, to, ct);
        var trades = await store.Read("simtrades.json", new List<SimTrade>());

        var link = CandidateTradeLinker.Link(rows, trades, new LinkOptions(now));
        var evaluation = ValidationEvaluator.Evaluate(link.Candidates, now);
        var walkForward = WalkForwardEvaluator.Evaluate(link.Candidates);
        var riskFrequency = RiskFrequencyEvaluator.Evaluate(link.Candidates);
        var scenarios = CostSensitivity.Evaluate(link.Candidates);
        var probabilityCalibration = ProbabilityCalibrationEvaluator.Evaluate(link.Candidates
            .Where(x => x.Trade is { OutcomeKnown: true, ExitAt: not null,
                Forecast: { SuccessProbability: not null } })
            .Select(x => new ProbabilityObservation(x.EventId, x.Trade!.Forecast!.AsOf,
                x.Trade.ExitAt!.Value, x.Trade.Forecast.SuccessProbability!.Value,
                string.Equals(x.Trade.StatusAsOf, "TARGET", StringComparison.Ordinal)))
            .ToArray(), now);

        var data = audit with
        {
            TradeRecords = trades.Count,
            TradeRecordLimit = TradeRecordLimit,
            TradeStoreAtLimit = trades.Count >= TradeRecordLimit,
            FieldGaps = FieldGaps(rows),
            Contract = Contract
        };

        var extra = new List<string>();
        if (data.TradeStoreAtLimit) extra.Add(LinkCodes.TradeRetentionCapped);
        if (audit.Files.Any(x => x.NearDailyLimit)) extra.Add(LinkCodes.ObservationRetentionCapped);
        var limitations = link.Audit.Limitations
            .Concat(walkForward.Limitations)
            .Concat(riskFrequency.Limitations)
            .Concat(probabilityCalibration.Limitations)
            .Concat(extra)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        return (200, new ValidationReport(now, now, window, _policy.Version, _policy.PolicyHash, data, link.Audit,
            evaluation, walkForward, riskFrequency, scenarios, limitations, probabilityCalibration));
    }

    // ── 관측 파일 읽기 ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 거래일 하루당 파일 하나를 순서대로 읽는다. 파일이 없는 날은 "없음"으로 기록하고 건너뛴다 —
    /// 조회가 관측을 만들어내지 않고, 없는 날을 0건 성과로 바꾸지도 않는다.
    /// </summary>
    async Task<(IReadOnlyList<ObservationRow> Rows, ValidationDataAudit Audit)> ReadObservationsAsync(DateOnly from,
        DateOnly to, CancellationToken ct)
    {
        var rows = new List<ObservationRow>();
        var files = new List<ValidationFileAudit>();
        var missing = new List<string>();
        var lines = 0;
        var failures = 0;
        long bytes = 0;
        var days = 0;

        foreach (var day in await _reader.ReadRangeAsync(from, to, ct))
        {
            days++;
            if (!day.Found)
            {
                missing.Add(day.TradingDate.ToString("yyyy-MM-dd"));
                files.Add(new ValidationFileAudit(day.File, day.TradingDate, false, 0, 0, 0, false));
                continue;
            }

            var fileLines = day.Lines.Count;
            var fileFailures = 0;
            long fileBytes = 0;
            foreach (var line in day.Lines)
            {
                fileBytes += line.Bytes;
                var row = line.Record is null ? null : Row(line.Record);
                if (row is null) fileFailures++;
                else rows.Add(row);
            }

            lines += fileLines;
            failures += fileFailures;
            bytes += fileBytes;
            // 상한에 닿은 날은 그날의 후보 전수가 아니다(§16: 상한 초과 시 더 저장하지 않는다).
            files.Add(new ValidationFileAudit(day.File, day.TradingDate, true, fileLines, fileFailures, fileBytes,
                fileBytes >= _policy.ObservationDailyByteLimit * 95 / 100));
        }

        var audit = new ValidationDataAudit(from, to, days, files.Count(x => x.Found), lines, failures, bytes,
            _policy.ObservationDailyByteLimit, missing, files, 0, TradeRecordLimit, false, [], Contract);
        return (rows, audit);
    }

    /// <summary>
    /// 저장된 관측 레코드를 Domain 입력 행으로 옮긴다. 손상된 줄은 버리되 삭제하지 않고 실패 건수로 남긴다(§16).
    /// 계획이 없는 후보의 비용 필드는 null로 둔다 — false(=정상)로 바꾸면 결측이 사라진다.
    /// </summary>
    static ObservationRow Row(StructureObservationRecord record)
    {
        var candidates = (record.Candidates ?? [])
            .Where(x => x is not null && !string.IsNullOrEmpty(x.EventId))
            .Select(x => new ObservationCandidateRow(x.EventId, x.Kind ?? string.Empty, x.ZoneId ?? string.Empty,
                x.State ?? string.Empty, x.EntryQuality, x.TriggerBarStart, x.TriggerConfirmedAt, x.ExpiresAt,
                x.Plan?.MissingLiquidity, x.Plan?.ValidSpread, x.Plan?.EligibilityCostModelVersion,
                x.Plan?.RealizedFillCostModelVersion, x.Plan?.NetR ?? x.NetR,
                (IReadOnlyList<string>)(x.RejectionCodes ?? []), x.Notes ?? []))
            .ToArray();
        return new ObservationRow(record.ObservationId, record.RecordVersion ?? string.Empty, record.Symbol,
            record.ObservedAt, record.SessionStart, record.AnalysisAsOf, record.QuoteAt,
            record.LastCompletedBarStart, record.EngineVersion ?? string.Empty, record.PolicyHash ?? string.Empty,
            record.Mode ?? string.Empty, record.Detail ?? string.Empty, record.Status ?? string.Empty,
            record.Trend?.State, record.Warnings ?? [], candidates);
    }

    /// <summary>후보 행 기준 필드 결측 감사. 결측을 0으로 바꾸지 않고 비율을 그대로 보고한다.</summary>
    static IReadOnlyList<ValidationFieldGap> FieldGaps(IReadOnlyList<ObservationRow> rows)
    {
        var candidates = rows.SelectMany(x => x.Candidates).ToArray();
        var total = candidates.Length;
        return
        [
            new ValidationFieldGap("entryQuality", candidates.Count(x => x.EntryQuality is null), total,
                "필수 구성요소가 없으면 점수도 null이다(§16A). READY가 될 수 없었던 후보를 포함한다."),
            new ValidationFieldGap("plan.costAssumptions",
                candidates.Count(x => x.MissingLiquidityCost is null), total,
                "계획이 성립한 후보에만 비용 가정이 저장된다. 거절·대기 후보의 비용 코호트 분리는 불가능하다."),
            new ValidationFieldGap("plan.validSpread", candidates.Count(x => x.ValidSpread is null), total,
                "호가 결측 시 spread=0 가정으로 자격을 평가한 건이다(운영 정책, 이 이슈에서 바꾸지 않는다)."),
            new ValidationFieldGap("trendState", rows.Count(x => string.IsNullOrEmpty(x.TrendState)),
                rows.Count, "추세 판정 불가/미수집 관측. 추세 코호트에서 미수집으로 분리된다."),
            new ValidationFieldGap("detail.full",
                rows.Count(x => !string.Equals(x.Detail, "full", StringComparison.Ordinal)), rows.Count,
                "요약 관측에는 Zone 배열·품질 상세가 없다(§16). 근거 재현 범위가 좁다.")
        ];
    }
}
