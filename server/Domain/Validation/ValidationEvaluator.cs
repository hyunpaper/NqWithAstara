namespace Astra.Server.Domain.Validation;

// 이슈 #28 — 사전 정의된 성과 평가 체계(Domain 순수 함수).
//
// 원칙:
//  1. 구간·차원·임계값은 여기 고정되어 있다. 수익이 좋아 보이는 임계값을 탐색하지 않는다(#28 금지 사항).
//  2. 표본이 기준에 못 미치면 "검증 불가"(InsufficientSample)이며 0%나 통과로 바꾸지 않는다.
//  3. 승률은 관측값이고 보장이 아니다. 불확실성(표준오차·95% 구간)을 항상 함께 낸다.
//  4. 진입 품질 구간 경계는 이슈 #27 SimulationCohorts와 같다 — 두 보고서가 다른 구간을 쓰면 비교가 불가능해진다.

/// <summary>
/// 표본 충분성 판정 기준. <b>사전 정의 값이며 데이터에 맞춰 조정하지 않는다.</b>
/// 조정이 필요하면 검증 근거와 별도 승인으로 바꾸고 문서(docs/v5-validation.md)를 함께 고친다.
/// </summary>
public sealed record EvaluationThresholds(int MinRealizedTrades, int MinSessions, int MinSymbols,
    double MaxSymbolSharePercent)
{
    public static readonly EvaluationThresholds Default = new(20, 3, 3, 60d);
}

/// <summary>
/// 평가 결론. <see cref="NotCollected"/>는 값 자체가 수집되지 않은 코호트다(0건과 구분한다).
/// JSON에는 숫자가 아니라 이름으로 나간다 — 소비자가 결론을 정수로 오해하지 않게 한다.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<EvaluationVerdict>))]
public enum EvaluationVerdict { NotCollected, InsufficientSample, Observed }

/// <summary>표본의 크기·기간·집중도. 거래 수만으로 판단하지 않기 위해 항상 함께 보고한다(#28).</summary>
public sealed record SampleScope(int Candidates, int Entered, int Closed, int RealizedPnl, int MissingPnl,
    int CensoredByCutoff, int EstimatedExits, int Symbols, int Sessions, DateOnly? FirstSession,
    DateOnly? LastSession, string? TopSymbol, double? TopSymbolSharePercent);

/// <summary>
/// 비용 차감 후 실현 손익(%)의 분포. 표본이 없으면 전부 null이며 0으로 대체하지 않는다.
/// <see cref="Ci95Lower"/>/<see cref="Ci95Upper"/>는 정규근사 구간이고 표본 2건 미만이면 null이다.
/// </summary>
public sealed record PnlSummary(double? MeanPercent, double? MedianPercent, double? StdDevPercent,
    double? StandardError, double? Ci95Lower, double? Ci95Upper, double? MinPercent, double? MaxPercent,
    double? SumPercent, int Wins, int Losses);

public sealed record RejectReasonCount(string Code, int Count);

/// <summary>한 코호트의 사전 정의 평가 결과.</summary>
public sealed record CohortEvaluation(string Key, string Label, bool Collected, EvaluationVerdict Verdict,
    IReadOnlyList<string> VerdictReasons, SampleScope Scope, PnlSummary Pnl, double? ObservedWinRatePercent,
    double? AvgPlannedNetR, int PlannedNetRSamples, IReadOnlyList<RejectReasonCount> TopRejectReasons);

public sealed record EvaluationGroup(string Dimension, string Title, IReadOnlyList<CohortEvaluation> Cohorts);

/// <summary>
/// 거절 후보의 사후 경로 평가. <b>가상 평가이며 실제 체결 성과와 절대 합산하지 않는다</b>(#28).
/// 관측 파일은 결정 이후의 가격 경로를 보존하지 않으므로 기본값은 <see cref="Collected"/>=false다.
/// </summary>
public sealed record VirtualRejectedEvaluation(bool Collected, string Basis, int Samples,
    double? HypotheticalMeanPercent, double? HypotheticalMedianPercent, IReadOnlyList<string> Limitations)
{
    public const string BasisNotRetained = "NOT_RETAINED";
    public const string BasisCallerSupplied = "CALLER_SUPPLIED_REPLAY";

    public static readonly VirtualRejectedEvaluation NotCollected = new(false, BasisNotRetained, 0, null, null,
        [LinkCodes.RejectedPathNotRetained]);
}

/// <summary>거절 후보가 제공하는 유일한 직접 증거는 "무엇 때문에 걸렀는가"의 분포다(선택 편향 가시화).</summary>
public sealed record RejectedCandidateReport(int Candidates, int Wait, int Ready, int Rejected, int Invalidated,
    int Expired, int Entered, int Unknown, IReadOnlyList<RejectReasonCount> TopReasons,
    VirtualRejectedEvaluation Hypothetical);

/// <summary>가상 평가 입력. 호출자가 과거 확정 봉으로 재생한 결과만 넣는다(여기서 만들지 않는다).</summary>
public sealed record VirtualPathSample(string EventId, double HypotheticalPnlPercent);

/// <summary>진입 품질 구간이 성과를 구분하는지에 대한 판정. 구분된다고 단정하지 않고 근거와 한계를 함께 낸다.</summary>
public sealed record QualityBandPoint(string Key, string Label, int RealizedPnl, double? MeanPercent,
    double? Ci95Lower, double? Ci95Upper);

public sealed record QualityDiscrimination(EvaluationVerdict Verdict, IReadOnlyList<string> Reasons,
    IReadOnlyList<QualityBandPoint> Bands, int ComparableBands, double? MeanSpreadPercent, bool? Monotonic,
    bool? IntervalsSeparated, string Caveat);

/// <summary>
/// #111: READY 후보가 진입으로 이어지지 않은 사유별 건수. <see cref="Events"/>는 이벤트 기준(같은 이벤트의
/// 반복 poll은 한 건)이고 <see cref="Symbols"/>는 그 이벤트가 걸친 종목 수다. 0건도 그대로 보고한다.
/// </summary>
public sealed record EntryBlockCount(string Code, string Label, int Events, int Symbols);

public sealed record ValidationEvaluation(DateTimeOffset AsOf, EvaluationThresholds Thresholds,
    CohortEvaluation Overall, IReadOnlyList<EvaluationGroup> Groups, RejectedCandidateReport Rejected,
    QualityDiscrimination QualityBands, IReadOnlyList<EntryBlockCount> EntryBlocks,
    IReadOnlyList<string> Caveats);

public static class ValidationEvaluator
{
    // 진입 품질 구간 — 이슈 #27 SimulationCohorts와 동일한 경계·키를 쓴다(두 보고서의 비교 가능성).
    public const string QualityBand0 = "Q0_25";
    public const string QualityBand25 = "Q25_50";
    public const string QualityBand50 = "Q50_75";
    public const string QualityBand75 = "Q75_100";

    public const string ExitEstimatedKey = "EXIT_ESTIMATED";
    public const string ExitObservedKey = "EXIT_OBSERVED";
    public const string ExitUncollectedKey = "EXIT_UNCOLLECTED";

    public static readonly string[] StandardCaveats =
    [
        "관측 결과이며 승률·수익을 보장하지 않는다. 같은 값이 다음 세션에서 재현된다는 근거가 아니다.",
        "표본 기준에 못 미치는 코호트는 검증 불가(InsufficientSample)이며 0%나 통과가 아니다.",
        "거절 후보의 사후 가격 경로는 수집되지 않아 거절 필터의 효과를 직접 측정할 수 없다(선택 편향 잔존).",
        "비용 민감도는 별도 시나리오 필드이며 원본 실현 손익을 대체하지 않는다.",
        "표본은 종목·기간 구성에 의존한다. 집중도와 세션 수를 함께 확인하지 않은 수치는 해석할 수 없다."
    ];

    public static ValidationEvaluation Evaluate(IReadOnlyList<LinkedCandidate> candidates, DateTimeOffset asOf,
        EvaluationThresholds? thresholds = null, IReadOnlyList<VirtualPathSample>? virtualPaths = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var limits = thresholds ?? EvaluationThresholds.Default;
        var rows = candidates.Where(x => x is not null).ToArray();

        var groups = new[]
        {
            Group(rows, limits, "decision", "결정 상태", x => x.Outcome.ToString().ToUpperInvariant(), DecisionLabel,
                DecisionOrder),
            Group(rows, limits, "entryQuality", "진입 품질 구간 (EntryQuality)", QualityKey, QualityLabel, QualityOrder),
            Group(rows, limits, "setup", "셋업 종류", x => Blank(x.Kind), SetupLabel),
            Group(rows, limits, "trend", "관측 시점 추세", x => Blank(x.TrendState), TrendLabel, TrendOrder),
            Group(rows, limits, "liquidity", "호가 결측 / 정상", x => x.Liquidity, LiquidityLabel, LiquidityOrder),
            Group(rows, limits, "exitEstimation", "청산 확정 / 추정", ExitKey, ExitLabel, ExitOrder),
            Group(rows, limits, "costModel", "비용 모델 (자격 / 실현 체결)", CostKey, key => key),
            Group(rows, limits, "policyVersion", "엔진 버전 / 정책 해시",
                x => x.EngineVersion + " / " + x.PolicyHash, key => key),
            Group(rows, limits, "mode", "실행 모드", x => Blank(x.Mode), ModeLabel)
        };

        return new ValidationEvaluation(asOf, limits, Cohort("ALL", "전체", true, rows, limits), groups,
            Rejected(rows, virtualPaths), Discrimination(rows, limits), EntryBlocks(rows), StandardCaveats);
    }

    // ── 코호트 ────────────────────────────────────────────────────────────────────────────────

    static EvaluationGroup Group(IReadOnlyList<LinkedCandidate> rows, EvaluationThresholds limits, string dimension,
        string title, Func<LinkedCandidate, string> keyOf, Func<string, string> labelOf,
        Func<string, int>? orderOf = null)
    {
        var cohorts = rows.GroupBy(keyOf, StringComparer.Ordinal)
            .Select(g => Cohort(g.Key, labelOf(g.Key), Collected(g.Key), g.ToArray(), limits))
            .OrderBy(c => c.Collected ? 0 : 1)
            .ThenBy(c => orderOf?.Invoke(c.Key) ?? 0)
            .ThenByDescending(c => c.Scope.Candidates)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();
        return new EvaluationGroup(dimension, title, cohorts);
    }

    /// <summary>수집되지 않은 값을 모은 코호트인지. 미수집은 "정상"이 아니라 별도 결론(NotCollected)이다.</summary>
    static bool Collected(string key) => key is not (SimulationCohorts.QualityUncollectedKey
        or LiquidityClass.Uncollected or ExitUncollectedKey or "" or "UNKNOWN");

    static CohortEvaluation Cohort(string key, string label, bool collected, IReadOnlyList<LinkedCandidate> rows,
        EvaluationThresholds limits)
    {
        var scope = Scope(rows);
        var pnl = Pnl(rows);
        var (verdict, reasons) = Verdict(collected, scope, limits);
        var netR = rows.Where(x => x.Trade is not null).Select(x => (double)x.Trade!.PlannedNetR)
            .Where(double.IsFinite).ToArray();
        return new CohortEvaluation(key, label, collected, verdict, reasons, scope, pnl,
            scope.RealizedPnl == 0 ? null : Math.Round(pnl.Wins * 100d / scope.RealizedPnl, 1),
            netR.Length == 0 ? null : Math.Round(netR.Average(), 2), netR.Length, Reasons(rows, 5));
    }

    static SampleScope Scope(IReadOnlyList<LinkedCandidate> rows)
    {
        var entered = rows.Where(x => x.Trade is not null).ToArray();
        var closed = entered.Where(x => x.Trade!.StatusAsOf != "OPEN").ToArray();
        var realized = rows.Count(x => x.HasRealizedPnl);
        var symbols = rows.GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var sessions = rows.Select(x => x.TradingDate).Distinct().Order().ToArray();
        return new SampleScope(rows.Count, entered.Length, closed.Length, realized, closed.Length - realized,
            entered.Count(x => x.Trade!.StoredStatus != "OPEN" && x.Trade!.StatusAsOf == "OPEN"),
            closed.Count(x => x.Trade!.ExitEstimated == true), symbols.Length, sessions.Length,
            sessions.Length == 0 ? null : sessions[0], sessions.Length == 0 ? null : sessions[^1],
            symbols.Length == 0 ? null : symbols[0].Key,
            rows.Count == 0 ? null : Math.Round(symbols[0].Count() * 100d / rows.Count, 1));
    }

    static PnlSummary Pnl(IReadOnlyList<LinkedCandidate> rows)
    {
        var values = rows.Where(x => x.HasRealizedPnl).Select(x => x.RealizedPnlPercent!.Value).Order().ToArray();
        if (values.Length == 0) return new PnlSummary(null, null, null, null, null, null, null, null, null, 0, 0);
        var mean = values.Average();
        var median = values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d;
        double? stdDev = null;
        double? standardError = null;
        double? lower = null;
        double? upper = null;
        if (values.Length >= 2)
        {
            var variance = values.Sum(x => (x - mean) * (x - mean)) / (values.Length - 1);
            stdDev = Math.Sqrt(variance);
            standardError = stdDev.Value / Math.Sqrt(values.Length);
            // 정규근사 95% 구간. 소표본에서는 넓게 봐야 하며 구간이 0을 포함하면 방향을 단정할 수 없다.
            lower = mean - 1.96 * standardError.Value;
            upper = mean + 1.96 * standardError.Value;
        }
        return new PnlSummary(Round(mean), Round(median), Round(stdDev), Round(standardError), Round(lower),
            Round(upper), Round(values[0]), Round(values[^1]), Round(values.Sum()),
            values.Count(x => x > 0), values.Count(x => x <= 0));
    }

    static (EvaluationVerdict Verdict, IReadOnlyList<string> Reasons) Verdict(bool collected, SampleScope scope,
        EvaluationThresholds limits)
    {
        if (!collected) return (EvaluationVerdict.NotCollected, ["VALUE_NOT_COLLECTED"]);
        var reasons = new List<string>();
        if (scope.RealizedPnl < limits.MinRealizedTrades)
            reasons.Add($"REALIZED_TRADES_BELOW_MIN:{scope.RealizedPnl}/{limits.MinRealizedTrades}");
        if (scope.Sessions < limits.MinSessions)
            reasons.Add($"SESSIONS_BELOW_MIN:{scope.Sessions}/{limits.MinSessions}");
        if (scope.Symbols < limits.MinSymbols)
            reasons.Add($"SYMBOLS_BELOW_MIN:{scope.Symbols}/{limits.MinSymbols}");
        if (scope.TopSymbolSharePercent is { } share && share > limits.MaxSymbolSharePercent)
            reasons.Add($"SYMBOL_CONCENTRATION_ABOVE_MAX:{share}/{limits.MaxSymbolSharePercent}");
        return reasons.Count == 0
            ? (EvaluationVerdict.Observed, Array.Empty<string>())
            : (EvaluationVerdict.InsufficientSample, reasons);
    }

    /// <summary>#111: 진입 차단 사유를 코드별로 따로 센다 — 두 코드를 합치면 지연과 차단을 구분할 수 없다.</summary>
    static IReadOnlyList<EntryBlockCount> EntryBlocks(IReadOnlyList<LinkedCandidate> rows) =>
        new[] { EntryBlockCodes.SuppressedBySamePollExit, EntryBlockCodes.BlockedByStopCooldown }
            .Select(code =>
            {
                var hits = rows.Where(x => x.RejectionCodes.Contains(code, StringComparer.Ordinal)).ToArray();
                return new EntryBlockCount(code, EntryBlockCodes.Label(code), hits.Length,
                    hits.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            })
            .ToArray();

    static IReadOnlyList<RejectReasonCount> Reasons(IReadOnlyList<LinkedCandidate> rows, int take) =>
        rows.SelectMany(x => x.RejectionCodes).GroupBy(x => x, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(take).Select(g => new RejectReasonCount(g.Key, g.Count())).ToArray();

    // ── 거절 후보 ──────────────────────────────────────────────────────────────────────────────

    static RejectedCandidateReport Rejected(IReadOnlyList<LinkedCandidate> rows,
        IReadOnlyList<VirtualPathSample>? virtualPaths)
    {
        var blocked = rows.Where(x => x.Outcome is CandidateOutcome.Rejected or CandidateOutcome.Invalidated
            or CandidateOutcome.Expired).ToArray();
        var hypothetical = VirtualRejectedEvaluation.NotCollected;
        if (virtualPaths is { Count: > 0 })
        {
            var ids = blocked.Select(x => x.EventId).ToHashSet(StringComparer.Ordinal);
            var values = virtualPaths.Where(x => x is not null && ids.Contains(x.EventId))
                .Where(x => double.IsFinite(x.HypotheticalPnlPercent))
                .Select(x => x.HypotheticalPnlPercent).Order().ToArray();
            if (values.Length > 0)
                hypothetical = new VirtualRejectedEvaluation(true, VirtualRejectedEvaluation.BasisCallerSupplied,
                    values.Length, Round(values.Average()),
                    Round(values.Length % 2 == 1
                        ? values[values.Length / 2]
                        : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2d),
                    ["가상 경로이며 실제 체결이 아니다. 실현 손익과 합산하지 않는다."]);
        }
        return new RejectedCandidateReport(rows.Count,
            rows.Count(x => x.Outcome == CandidateOutcome.Wait), rows.Count(x => x.Outcome == CandidateOutcome.Ready),
            rows.Count(x => x.Outcome == CandidateOutcome.Rejected),
            rows.Count(x => x.Outcome == CandidateOutcome.Invalidated),
            rows.Count(x => x.Outcome == CandidateOutcome.Expired),
            rows.Count(x => x.Outcome == CandidateOutcome.Entered),
            rows.Count(x => x.Outcome == CandidateOutcome.Unknown), Reasons(blocked, 10), hypothetical);
    }

    // ── 진입 품질 구간의 구분력 ─────────────────────────────────────────────────────────────────

    static QualityDiscrimination Discrimination(IReadOnlyList<LinkedCandidate> rows, EvaluationThresholds limits)
    {
        var ordered = new[] { QualityBand0, QualityBand25, QualityBand50, QualityBand75 };
        var points = new List<QualityBandPoint>(ordered.Length);
        foreach (var band in ordered)
        {
            var subset = rows.Where(x => QualityKey(x) == band).ToArray();
            var pnl = Pnl(subset);
            points.Add(new QualityBandPoint(band, QualityLabel(band), subset.Count(x => x.HasRealizedPnl),
                pnl.MeanPercent, pnl.Ci95Lower, pnl.Ci95Upper));
        }

        var comparable = points.Where(x => x.RealizedPnl >= limits.MinRealizedTrades).ToArray();
        var reasons = new List<string>();
        if (comparable.Length < 2)
            reasons.Add($"COMPARABLE_BANDS_BELOW_MIN:{comparable.Length}/2");

        bool? monotonic = null;
        bool? separated = null;
        double? spread = null;
        if (comparable.Length >= 2)
        {
            var means = comparable.Select(x => x.MeanPercent!.Value).ToArray();
            spread = Round(means.Max() - means.Min());
            monotonic = means.Zip(means.Skip(1), (a, b) => b >= a).All(x => x);
            // 두 끝 구간의 95% 구간이 겹치지 않을 때만 "구분됐다"고 말할 수 있다. 겹치면 단정하지 않는다.
            var low = comparable[0];
            var high = comparable[^1];
            separated = low.Ci95Upper is { } lu && high.Ci95Lower is { } hl && (hl > lu || low.Ci95Lower > high.Ci95Upper);
        }

        var verdict = comparable.Length >= 2 ? EvaluationVerdict.Observed : EvaluationVerdict.InsufficientSample;
        return new QualityDiscrimination(verdict, reasons, points, comparable.Length, spread, monotonic, separated,
            "구간 간 차이는 관측값이다. 구간이 성과를 구분한다는 결론은 95% 구간이 겹치지 않고 독립 기간에서 재현될 때만 가능하다.");
    }

    // ── 키/라벨 ───────────────────────────────────────────────────────────────────────────────

    /// <summary>이슈 #27과 같은 경계. null은 진입 당시에도 수집되지 않은 값이므로 구간에 넣지 않는다.</summary>
    public static string QualityKey(LinkedCandidate row) => row.EntryQuality switch
    {
        null => SimulationCohorts.QualityUncollectedKey,
        < 25 => QualityBand0,
        < 50 => QualityBand25,
        < 75 => QualityBand50,
        _ => QualityBand75
    };

    static string QualityLabel(string key) => key switch
    {
        QualityBand0 => "0 ~ 25",
        QualityBand25 => "25 ~ 50",
        QualityBand50 => "50 ~ 75",
        QualityBand75 => "75 ~ 100",
        _ => "EntryQuality 미수집"
    };

    static int QualityOrder(string key) => key switch
    {
        QualityBand0 => 0, QualityBand25 => 1, QualityBand50 => 2, QualityBand75 => 3, _ => 4
    };

    static string ExitKey(LinkedCandidate row) => row.Trade?.ExitEstimated switch
    {
        true => ExitEstimatedKey,
        false => ExitObservedKey,
        _ => ExitUncollectedKey
    };

    static string ExitLabel(string key) => key switch
    {
        ExitEstimatedKey => "추정 청산",
        ExitObservedKey => "확정 청산",
        _ => "청산 없음 · 미수집"
    };

    static int ExitOrder(string key) => key switch { ExitObservedKey => 0, ExitEstimatedKey => 1, _ => 2 };

    static string CostKey(LinkedCandidate row) =>
        Blank(row.EligibilityCostModelVersion) + " / " + Blank(row.RealizedFillCostModelVersion);

    static string LiquidityLabel(string key) => key switch
    {
        LiquidityClass.Missing => "호가 결측 — 스프레드 0 가정",
        LiquidityClass.ObservedZeroSpread => "호가 관측 — 스프레드 0",
        LiquidityClass.Observed => "호가 관측 — 스프레드 > 0",
        _ => "비용 가정 미수집 (계획 없음)"
    };

    static int LiquidityOrder(string key) => key switch
    {
        LiquidityClass.Observed => 0, LiquidityClass.ObservedZeroSpread => 1, LiquidityClass.Missing => 2, _ => 3
    };

    static string DecisionLabel(string key) => key switch
    {
        "ENTERED" => "진입", "READY" => "READY (미진입)", "WAIT" => "대기", "REJECTED" => "거절",
        "INVALIDATED" => "무효화", "EXPIRED" => "만료", _ => "알 수 없음"
    };

    static int DecisionOrder(string key) => key switch
    {
        "ENTERED" => 0, "READY" => 1, "WAIT" => 2, "REJECTED" => 3, "INVALIDATED" => 4, "EXPIRED" => 5, _ => 6
    };

    static string SetupLabel(string key) => key switch
    {
        "PULLBACK" => "눌림목 (PULLBACK)", "BREAKOUT" => "돌파 (BREAKOUT)", "REBOUND" => "과매도 반등 (REBOUND)",
        "" => "종류 미수집", _ => key
    };

    static string TrendLabel(string key) => key switch
    {
        "UP" => "상승", "DOWN" => "하락", "RANGE" => "횡보", "TRANSITION" => "전환", "UNKNOWN" => "판정 불가",
        "" => "추세 미수집", _ => key
    };

    static int TrendOrder(string key) => key switch
    {
        "UP" => 0, "TRANSITION" => 1, "RANGE" => 2, "DOWN" => 3, _ => 4
    };

    static string ModeLabel(string key) => key switch
    {
        "active" => "active (v5 진입 소유)", "shadow" => "shadow (관측 전용)", "off" => "off", "" => "모드 미수집",
        _ => key
    };

    static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    internal static double? Round(double? value) =>
        value is { } x && double.IsFinite(x) ? Math.Round(x, 4) : null;
}
