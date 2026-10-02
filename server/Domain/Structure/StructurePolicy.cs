using System.Collections;
using System.Collections.Immutable;
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
    public string ReboundLongVwapGateVersion { get; init; } = "reject-positive-distance.1";
    public string BreakoutConfirmationGateVersion { get; init; } = "hold-breakout-boundary.1";

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

    /// <summary>
    /// §9.3 비용 반영 손익비 상한(#209). netR이 이 값을 넘으면 계획을 거절한다. 목표·손절을 조작하지 않는다(§19-5).
    /// 손익비가 지나치게 좋은 계획은 구조가 좋은 것이 아니라 진입가가 무효화 지점에 붙어 있다는 뜻이다.
    /// 단일 국면 표본(122건)에서 고른 잠정값이며 검증된 최적값이 아니다(§16A).
    /// </summary>
    public double MaxNetR { get; init; } = 2.0;
    public int EntryCutoffBeforeCloseMinutes { get; init; } = 40;
    public int CandidateTtlMinutes { get; init; } = 5;
    public int BreakoutCooldownMinutes { get; init; } = 30;
    /// <summary>
    /// PULLBACK/REBOUND가 접촉 episode를 근거로 삼을 수 있는 최대 경과 시간. 30분은 기존 BREAKOUT
    /// 재무장 단위와 같은 세션 내 구조 관찰 단위이며, 성과를 통해 탐색한 값이 아니다.
    /// </summary>
    public int TriggerEpisodeMaxAgeMinutes { get; init; } = 30;

    /// <summary>
    /// §10 손절 후 재진입 제한. 같은 심볼의 최신 STOP 청산이 일어난 완료 봉을 0번째로 세어, 이후 완료 봉이
    /// 이 개수만큼 쌓이기 전에는 다시 진입하지 않는다. 보수적 운영 정책 상수이며 구조에서 도출한 값이 아니다.
    /// 3봉은 실측 122건에서 1건도 차단하지 못했다(관측 최소 재진입 간격 5분). 26거래일 단일 국면 표본으로
    /// 고른 잠정값 20이며 배포 후 차단 건의 사후 결과로 재검증한다(#210, §16A).
    /// </summary>
    public int StopReentryCooldownBars { get; init; } = 20;
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
    public int PriceTickUnknownWarningPolls { get; init; } = 3;
    public double RoundTripFeePercent { get; init; } = TradingCostDefaults.RoundTripFeePercent;
    public double? ShortBorrowCostPercent { get; init; }

    /// <summary>
    /// §16A 종목 유형: v5 신규 진입을 허용하는 securityType 집합(#132). 레버리지 ETF·비보통주의 tick·변동성
    /// 구조가 개별주 구조 문법(§5~§8)과 다르다는 판단이며 성과로 검증한 값이 아니다.
    /// </summary>
    public ImmutableArray<string> AllowedSecurityTypes { get; init; } = DefaultAllowedSecurityTypes;

    static readonly ImmutableArray<string> DefaultAllowedSecurityTypes = ["STOCK", "DEPOSITARY_RECEIPT"];

    /// <summary>
    /// 롱 신규 READY를 막는 셋업 유형 이름 집합(`PULLBACK`·`BREAKOUT`·`REBOUND`, #245 Cycle77).
    /// 기본 빈 값이며, 비어 있으면 canonical JSON에서 빠져 기존 PolicyHash가 유지된다.
    /// </summary>
    [OmitFromPolicyHashWhenEmpty]
    public ImmutableArray<string> DisabledLongKinds { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>롱 <paramref name="kindName"/>이 정책으로 비활성인지 판정한다(#245 Cycle77).</summary>
    public bool IsLongKindDisabled(string kindName) =>
        !DisabledLongKinds.IsDefaultOrEmpty &&
        DisabledLongKinds.Contains(kindName, StringComparer.OrdinalIgnoreCase);

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
    public double ReactionAtrScale { get; init; } = .5;
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

    // ── 진입 regime·기대값 진단 ──
    public int RegimeVolatilityLookbackBars { get; init; } = 20;
    public double LowVolatilityRatio { get; init; } = .75;
    public double HighVolatilityRatio { get; init; } = 1.25;
    /// <summary>시간순 학습으로 고정한 계획 feature 임계값. null이면 학습 프로필이 아직 배선되지 않은 상태다.</summary>
    public double? ExpectedValueFeatureThreshold { get; init; }
    public double MinimumExpectedNetR { get; init; } = 0.0;
    public bool RequireCompleteLiquidityCost { get; init; } = true;
    /// <summary>
    /// TRANSITION은 확정된 추세 정렬이 없는 구간이므로 PULLBACK의 최종 진입 자격을 기본 차단한다.
    /// 후보와 근거는 관측을 위해 생성하되, 명시적으로 허용한 정책에서만 최종 자격 차단을 해제한다.
    /// </summary>
    public bool AllowTransitionPullback { get; init; }
    public bool AllowQualifiedTransitionPullback { get; init; } = true;
    public bool AllowTrendAlignedTransitionPullback { get; init; }
    public double TransitionPullbackMinimumRelativeVolume { get; init; } = .75;
    public double TransitionPullbackMaximumNetR { get; init; } = 1.4;
    public bool AllowTransitionBreakout { get; init; }
    public bool AllowQualifiedTransitionBreakout { get; init; } = true;
    public double TransitionBreakoutMinimumRelativeVolume { get; init; } = .9;
    public double TransitionBreakoutMaximumNetR { get; init; } = 1.65;
    public bool RequirePositiveBenchmarkForRebound { get; init; }
    public bool RequirePullbackNearVwap { get; init; } = true;
    public double PullbackMaximumVwapDistanceAtr { get; init; } = 2.1;
    public bool RequireBreakoutNearVwap { get; init; } = true;
    public double BreakoutMaximumVwapDistanceAtr { get; init; } = 5.0;
    public bool RequireMinimumReboundEntryQuality { get; init; } = true;
    public double MinimumReboundEntryQuality { get; init; } = 50.0;
    public bool RequireBreakoutAboveVwap { get; init; } = true;
    public bool RequireMaximumReboundNetR { get; init; } = true;
    public double MaximumReboundNetR { get; init; } = 1.9;
    /// <summary>
    /// v5 청산 정책. 완료 봉 종가가 최초 구조 위험의 2배 이상 유리해진 뒤 다음 봉부터
    /// 왕복 비용을 회수하는 가격으로 손절을 올린다. Cycle45 운영 기본값 true(§10, #245).
    /// </summary>
    public bool EnableTwoRFeeBreakEvenStop { get; init; } = true;
    /// <summary>기존 regime classifier가 RANGE_HIGH로 확정한 후보만 차단하는 실험 정책. 기본 비활성.</summary>
    public bool RejectHighVolatilityRangeEntries { get; init; }
    /// <summary>최초 구조 위험의 2R보다 먼 목표만 2R로 제한한다. Cycle45 운영 기본값 true(§10, #245).</summary>
    public bool CapStructuralTargetAtTwoR { get; init; } = true;
    public bool EnableHalfRFeeBreakEvenStopForPositiveBenchmark { get; init; } = true;
    /// <summary>양의 벤치마크 0.5R 비용회수 손절에서 BREAKOUT만 제외한다. Cycle45 운영 기본값 true(§10, #245).</summary>
    public bool ExemptBreakoutFromPositiveBenchmarkHalfRStop { get; init; } = true;
    public bool EnableHalfRFeeBreakEvenStopForQualifiedTransition { get; init; }

    /// <summary>§7 UP/DOWN 진입·이탈에 요구하는 연속 완료 봉 수. TRANSITION은 여기서 제외된다(§7, #148).</summary>
    public int TrendStateHoldBars { get; init; } = 2;

    /// <summary>§7 UP/DOWN 이탈용 efficiency 임계값. 진입용 <see cref="TrendEfficiencyThreshold"/>보다 낮다(§7, #148).</summary>
    public double TrendExitEfficiency { get; init; } = .20;

    /// <summary>§7 UP/DOWN 이탈용 |signedTrend| 임계값. 진입용 <see cref="TrendStateThreshold"/>보다 낮다(§7, #148).</summary>
    public double TrendExitSignedTrend { get; init; } = 20;

    /// <summary>§7 structureDirection 분모의 봉 수. 분모는 sqrt(이 값)·ATR1m이다(§7, #148).</summary>
    public int StructureDirectionAtrScaleBars { get; init; } = 5;

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
    public TimeSpan TriggerEpisodeMaxAge() => TimeSpan.FromMinutes(TriggerEpisodeMaxAgeMinutes);

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
                if (OmittedWhenEmpty(property, property.GetValue(this))) continue;
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

    static bool OmittedWhenEmpty(PropertyInfo property, object? value) =>
        property.IsDefined(typeof(OmitFromPolicyHashWhenEmptyAttribute)) &&
        value is null or ImmutableArray<string> { IsDefaultOrEmpty: true };

    /// <summary>뒤늦게 추가한 집합 정책이 비어 있으면 canonical JSON에서 빼 기존 hash를 보존한다(#245).</summary>
    [AttributeUsage(AttributeTargets.Property)]
    sealed class OmitFromPolicyHashWhenEmptyAttribute : Attribute;

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
        IEnumerable items => "[" + string.Join(',', items.Cast<object?>().Select(Canonical)) + "]",
        double d => double.IsFinite(d)
            ? d.ToString("R", CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("Policy numbers must be finite; NaN/Infinity is never serialized."),
        _ => throw new NotSupportedException($"Unsupported policy value type {value.GetType().Name}.")
    };
}




