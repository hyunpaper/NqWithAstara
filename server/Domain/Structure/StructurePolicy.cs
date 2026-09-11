using System.Globalization;
using System.Reflection;
using System.Text;

namespace Astra.Server.Domain.Structure;

/// <summary>
/// 설계 §16A 정책 계약. 모든 초기 수치를 한 곳에 모으고 canonical JSON의 SHA-256으로 <see cref="PolicyHash"/>를 만든다.
/// 표에 없는 수치를 코드 상수로 숨기지 않으며, 값이 바뀌면 hash가 반드시 바뀐다.
/// 이 값들은 재현 가능한 최초 구현을 위한 잠정 정책이며 검증된 최적 매매법이 아니다.
/// </summary>
public sealed record StructurePolicy
{
    // ── 버전 ──
    public string Version { get; init; } = "v5-structure.1";
    public string EligibilityCostModelVersion { get; init; } = "eligibility.fee-plus-spread.1";
    public string RealizedFillCostModelVersion { get; init; } = "realized.v4-fill.1";

    // ── 피벗·최소 관측 (§16A 표) ──
    public int PivotLeft { get; init; } = 2;
    public int PivotRight { get; init; } = 2;
    public int Minimum1mBars { get; init; } = 30;

    // ── 신선도 (§16A 표) ──
    public int NewEntryQuoteMaxAgeSeconds { get; init; } = 15;
    public int QuoteFutureToleranceSeconds { get; init; } = 5;
    public int SpreadMaxAgeSeconds { get; init; } = 30;
    public int LatestBarMaxAgeSeconds { get; init; } = 90;

    // ── 자격·위험·시간 (§16A 표) ──
    public double ZoneEligibilityStrength { get; init; } = .35;
    public double MaxRiskPercent { get; init; } = 2.0;
    public double MinimumNetR { get; init; } = 1.2;
    public int EntryCutoffBeforeCloseMinutes { get; init; } = 40;
    public int CandidateTtlMinutes { get; init; } = 5;
    public int BreakoutCooldownMinutes { get; init; } = 30;
    public long ObservationDailyByteLimit { get; init; } = 20L * 1024 * 1024;

    /// <summary>
    /// §16 관측 저장의 보존 우선순위. 일자 상한(<see cref="ObservationDailyByteLimit"/>)의 이 비율만큼은
    /// 핵심 관측(상태 전이·후보 결정)만 쓸 수 있는 예비 구간이다. 상한 자체를 늘리지 않고 "무엇을 먼저 버릴지"만
    /// 정한다 — 주기 WAIT 요약이 먼저 희생되고 진입·READY·거절 근거가 마지막까지 남는다.
    /// </summary>
    public double ObservationCoreReserveRatio { get; init; } = .20;

    // ── 가격 단위 (§16A 표, §16B 비용·가격 단위) ──
    public decimal PriceTick { get; init; } = .01m;
    public decimal MinimumSupportedPrice { get; init; } = 1.00m;
    public double RoundTripFeePercent { get; init; } = .2;

    // ── 봉 집계 (§5.2) ──
    public int AggregationMinutes { get; init; } = 5;
    public int OpeningRangeMinutes { get; init; } = 15;

    // ── 거래량 프로파일 (§6.2) ──
    public double ProfileBinAtrFactor { get; init; } = .5;
    public int ProfileMaxBins { get; init; } = 400;
    public double ProfileNodeThreshold { get; init; } = .6;
    public int ProfileMaxNodes { get; init; } = 3;
    public double ProfileVolumeTolerance { get; init; } = 1e-6;

    // ── 구간 폭·병합 (§6.3) ──
    public double ZoneHalfWidthAtrFactor { get; init; } = .15;
    public decimal ZoneHalfWidthFloor { get; init; } = .01m;
    public double ZoneMergeGapAtrFactor { get; init; } = .10;
    public decimal ZoneMergeGapFloor { get; init; } = .01m;
    public double ZoneMergeMaxWidthAtrFactor { get; init; } = .75;
    public decimal ZoneMergeMaxWidthFloor { get; init; } = .03m;

    // ── 접촉·반응 (§6.4, §16A 마지막 문단) ──
    public int ReactionWindowBars { get; init; } = 5;
    public int EpisodeExitBars { get; init; } = 2;
    public double ReactionSuccessAtrFactor { get; init; } = .25;
    public decimal ReactionSuccessFloor { get; init; } = .01m;

    // ── 정규화 척도 (§6.5) ──
    public double TouchEvidenceScale { get; init; } = 2;
    public double ReactionAtrScale { get; init; } = 2;
    public double ConfluenceScale { get; init; } = 2;
    public double RecencyTradingMinutes { get; init; } = 390;
    public double RecencySessions { get; init; } = 20;

    // ── 지표 (§16B) ──
    public int AtrPeriod { get; init; } = 14;
    public int EmaFastPeriod { get; init; } = 9;
    public int EmaSlowPeriod { get; init; } = 21;
    public int TrendSlopeLookbackBars { get; init; } = 5;
    public int EfficiencyLookbackBars { get; init; } = 20;
    public int RelativeVolumeLookbackBars { get; init; } = 20;
    public double TrendStateThreshold { get; init; } = 25;
    public double TrendEfficiencyThreshold { get; init; } = .25;

    // ── 일봉 컨텍스트 (§6.1, §16B) ──
    public int DailyLookbackSessions { get; init; } = 20;

    // ── 계획 buffer (§9.1, §9.2) ──
    public double StopBufferAtrFactor { get; init; } = .15;
    public decimal StopBufferFloor { get; init; } = .01m;
    public decimal FrontRunBufferFloor { get; init; } = .01m;

    /// <summary>
    /// §9.1 최소 손절 거리의 변동성 하한 계수(#43). `Entry-Stop &lt; MinStopAtrFactor*Atr1mAtPlan`이면 계획을 거절한다.
    /// 0.5는 "1분 노이즈의 절반"이라는 물리적 의미에서 고른 값이며 성과가 좋아지는 값을 탐색해 얻은 값이 아니다.
    /// 손절을 넓히는 데 쓰지 않는다 — 하한 미달은 거절이지 손절 재배치가 아니다(§9.1 MaxRiskPercent와 대칭).
    /// </summary>
    public double MinStopAtrFactor { get; init; } = .5;

    // ── 품질 점수 척도 (§9.4) ──
    public double RoomQualityScale { get; init; } = 2;
    public double ExtensionQualityAtrScale { get; init; } = 3;

    /// <summary>
    /// §7 vwapDirection과 §9.4 extension/reclaim 품질의 분모 하한(max(...,0.01)). D2 추가.
    /// 정규화 분모가 0이 되어 NaN/Infinity가 생기지 않게 하는 값이며 가격 tick과는 의미가 다르다.
    /// </summary>
    public double IndicatorFloor { get; init; } = .01;

    public static readonly StructurePolicy Default = new();

    public TimeSpan AggregationSpan() => TimeSpan.FromMinutes(AggregationMinutes);
    public TimeSpan OpeningRangeSpan() => TimeSpan.FromMinutes(OpeningRangeMinutes);
    public TimeSpan CandidateTtl() => TimeSpan.FromMinutes(CandidateTtlMinutes);
    public TimeSpan BreakoutCooldown() => TimeSpan.FromMinutes(BreakoutCooldownMinutes);

    /// <summary>
    /// canonical JSON: 정책 입력 속성만, 이름 ordinal 정렬, 고정 수치 표현. 실행 시각/파일 경로는 넣지 않는다(§16A).
    /// get-only 파생 속성과 hash 자체는 입력이 아니므로 제외된다.
    /// </summary>
    public string CanonicalJson
    {
        get
        {
            var builder = new StringBuilder("{");
            var first = true;
            foreach (var property in PolicyInputs)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(Quote(property.Name)).Append(':').Append(Canonical(property.GetValue(this)));
            }
            return builder.Append('}').ToString();
        }
    }

    /// <summary>canonical JSON의 SHA-256 소문자 hex. 같은 정책은 같은 hash, 다른 수치는 다른 hash다.</summary>
    public string PolicyHash => StructureMath.Sha256Hex(CanonicalJson);

    static readonly PropertyInfo[] PolicyInputs = typeof(StructurePolicy)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(x => x.CanRead && x.SetMethod is { IsPublic: true })
        .OrderBy(x => x.Name, StringComparer.Ordinal)
        .ToArray();

    static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c < ' ' ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString()
            });
        return builder.Append('"').ToString();
    }

    static string Canonical(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => Quote(s),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal m => StructureMath.Price(m),
        double d => double.IsFinite(d)
            ? d.ToString("R", CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Policy numbers must be finite; NaN/Infinity is never serialized."),
        _ => throw new NotSupportedException($"Unsupported policy value type {value.GetType().Name}.")
    };
}
