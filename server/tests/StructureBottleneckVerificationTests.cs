using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #30 — 5분봉 구조 필수 조건(MISSING_5M_STRUCTURE) 병목 검증 및 도달 가능성 회귀 테스트.
///
/// 관측 배경: active 23:46~00:05 표본에서 후보 32건 중 24건이 MISSING_5M_STRUCTURE로 거절됐다.
/// 감독자 검증 결과 이것은 코드 버그가 아니라 설계 §16B("5m 구조 결측은 READY 차단")의 의도된 보수 정책이며,
/// 이 파일은 그 현재 행동을 결정적 fixture로 고정한다(도달 가능성 + 미래 누출 방지 + 최소 관측 한계).
///
/// 최소 관측 한계 (§5.3 + §7에서 유도, 아래 테스트로 고정):
/// - 5m 피벗 1개 = 좌우 각 2버킷 창(25분) + 우측 확인 지연 10분.
/// - 확정 HIGH 2 + LOW 2가 가장 빨리 갖춰지는 지그재그(H@b2, L@b4, H@b6, L@b8)도
///   마지막 피벗의 우측 확인이 bucket 10 종료 = 세션 시작 후 55분이다.
/// - 단방향(단조) 세션은 반대쪽 로컬 극값이 생기지 않아 피벗 0개가 수 시간 지속될 수 있고,
///   그동안 추세 정렬이 필수인 유형(PULLBACK/BREAKOUT)의 READY가 차단된다(#33 이후 REBOUND는 스코프 밖).
///
/// #30 시점에는 이 차단이 전 유형(REBOUND 포함)에 일괄 적용됐다. 이슈 #33(D7)이 §8(REBOUND는 높은
/// 추세 점수를 요구하지 않음, CounterTrend=true)·§9.4(REBOUND는 alignmentQuality 제외)와의 모순을 해소해
/// 차단을 PULLBACK/BREAKOUT으로 한정했고, 이 파일의 REBOUND 기대값은 그에 맞게 갱신됐다(각 테스트 주석 참조).
/// </summary>
public sealed class StructureBottleneckVerificationTests
{
    static readonly StructurePolicy P = D3.P with
    {
        ReboundMaxTrendAlignment = null,
        WindowBlockStartMinutesFromOpen = null,
        WindowBlockEndMinutesFromOpen = null
    };

    static StructureBar Bar(int minute, decimal open, decimal high, decimal low, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    /// <summary>bucket별 중심값을 5개 1분봉으로 펼친다(D2 추세 테스트와 같은 형태).</summary>
    static ImmutableArray<StructureBar> FromBuckets(IReadOnlyList<decimal> centers, decimal half = .10m,
        double volume = 1000)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var k = 0; k < centers.Count; k++)
            for (var i = 0; i < 5; i++)
            {
                var minute = k * 5 + i;
                bars.Add(Bar(minute, centers[k], centers[k] + half, centers[k] - half, centers[k], volume));
            }
        return bars.ToImmutable();
    }

    static TrendAssessment Trend(ImmutableArray<StructureBar> bars, DateTimeOffset cutoff)
    {
        // 운영 경로(StructureAnalysisService)와 동일: 5분봉은 로컬 집계, cutoff는 analysisAsOf.
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, P);
        return TrendEvaluator.Evaluate(TrendRequest.Create(Fx.Symbol, Fx.SessionStart, cutoff, bars, five), P);
    }

    static ImmutableArray<ConfirmedPivot> FiveMinutePivots(ImmutableArray<StructureBar> bars, DateTimeOffset cutoff) =>
        PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.FiveMinute,
            BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, P), cutoff, P);

    /// <summary>
    /// 확정 5m 피벗 2H+2L이 최소 봉 수로 갖춰지는 상승 지그재그(H@b2, L@b4, H@b6, L@b8).
    /// 마지막 피벗(L@b8)의 우측 확인은 bucket 10 종료 = 55분이다.
    /// </summary>
    static readonly decimal[] EarliestTwoHighsTwoLows =
        [100.0m, 100.6m, 101.4m, 101.0m, 100.8m, 101.6m, 102.4m, 102.0m, 101.8m, 102.6m, 103.4m];

    // ══ ① 도달 가능성 — 구조가 갖춰지면 READY까지 실제로 도달한다 ══

    /// <summary>
    /// 최소 관측 한계 문서화: 가장 빠른 지그재그도 2H+2L은 세션 시작 55분에야 확정된다.
    /// 54분에는 structureDirection=null + MISSING_5M_STRUCTURE, 55분부터 방향이 계산되고 차단이 풀린다.
    /// 즉 현재 정책에서 세션 최초 ~55분은 구조적으로 모든 유형의 신규 READY가 불가능하다.
    /// </summary>
    [Fact]
    public void StructureDirectionAppearsNoEarlierThanMinuteFiftyFiveInTheTightestZigzag()
    {
        var bars = FromBuckets(EarliestTwoHighsTwoLows);

        var justBefore = Trend(bars.Where(x => x.End <= Fx.At(54)).ToImmutableArray(), Fx.At(54));
        Assert.Null(justBefore.StructureDirection);
        Assert.True(justBefore.StructureEvidenceMissing);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, justBefore.BlockersForReady);

        var atEarliest = Trend(bars, Fx.At(55));
        Assert.NotNull(atEarliest.StructureDirection);
        Assert.False(atEarliest.StructureEvidenceMissing);
        Assert.DoesNotContain(TrendEvaluator.BlockerMissing5mStructure, atEarliest.BlockersForReady);
        Assert.Empty(atEarliest.BlockersForReady);

        // 확정 근거: 5m 피벗이 정확히 HIGH 2 + LOW 2다.
        var pivots = FiveMinutePivots(bars, Fx.At(55));
        Assert.Equal(2, pivots.Count(x => x.Kind == PivotKind.High));
        Assert.Equal(2, pivots.Count(x => x.Kind == PivotKind.Low));
    }

    /// <summary>
    /// 도달 가능성 E2E: 지그재그로 2H+2L 확정(55분) 후 지지 접촉 눌림 → 회복 트리거(60분).
    /// 실제 집계·피벗·추세 경로가 만든 TrendAssessment로 SetupDetector가 READY 후보를 산출한다.
    /// (구조 병목이 없으면 후보 게이트 전체가 실제로 열린다는 사실을 고정한다.)
    /// </summary>
    [Fact]
    public void ReadyIsReachableOnTheRealTrendPathOnceTwoHighsAndTwoLowsConfirm()
    {
        var bars = FromBuckets(EarliestTwoHighsTwoLows).ToBuilder();
        // 55~58분: support [102.90,103.10] 접촉 눌림(저점 102.95 — 하단을 깨지 않은 눌림) + 회복
        bars.Add(Bar(55, 103.30m, 103.40m, 103.05m, 103.10m));
        bars.Add(Bar(56, 103.10m, 103.15m, 102.95m, 103.00m));
        bars.Add(Bar(57, 103.00m, 103.20m, 103.00m, 103.15m));
        bars.Add(Bar(58, 103.15m, 103.25m, 103.10m, 103.20m));
        // 59분 트리거: 직전 High(103.25)와 support Upper(103.10) 위 마감, 상대 거래량 2배
        bars.Add(Bar(59, 103.20m, 103.45m, 103.15m, 103.35m, 2000));
        var all = bars.ToImmutable();

        var analysisAsOf = Fx.At(60);
        var trend = Trend(all, analysisAsOf);
        Assert.NotNull(trend.StructureDirection);
        Assert.Empty(trend.BlockersForReady);
        Assert.True(trend.State is TrendState.Up or TrendState.Transition, $"state={trend.State}");

        var atr = SessionAtr.At(all, SessionAtr.Series(all, P), Fx.At(59));   // structureCutoff 기준
        var zones = ImmutableArray.Create(D2.Support(102.90m, 103.10m), D2.Resistance(104.60m, 104.90m));
        var episodes = ImmutableArray.Create(D2.Episode("support-zone", 55, 58));
        var result = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, all, zones, episodes, trend, atr, 103.35m, analysisAsOf,
            D2.Quote(103.34m, 103.36m, 60)), P);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(SetupKind.Pullback, candidate.Kind);
        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.Empty(candidate.RejectionCodes);
        Assert.NotNull(candidate.Plan);
        Assert.Empty(result.ReadyBlockers);
        Assert.Equal(candidate.EventId, result.PreferredCandidateId);
    }

    /// <summary>
    /// 병목의 구조적 원인 재현: 단방향(단조 상승) 세션은 반대쪽 로컬 극값이 없어
    /// HIGH/LOW 피벗이 하나도 확정되지 않는다 → structureDirection=null → 추세 자체는 강한 UP인데도
    /// MISSING_5M_STRUCTURE가 남는다. 90분(5m bucket 18개)에도 마찬가지다.
    /// </summary>
    [Fact]
    public void AMonotonicRisingSessionNeverConfirmsAnyPivotDespiteAStrongUptrend()
    {
        var bars = MonotonicRise(90);
        var trend = Trend(bars, Fx.At(90));

        Assert.Empty(FiveMinutePivots(bars, Fx.At(90)));                      // HIGH 0 + LOW 0
        Assert.Null(trend.StructureDirection);
        Assert.True(trend.StructureEvidenceMissing);
        Assert.NotNull(trend.SignedTrend);                                    // 추세 자체는 계산된다(가격 family만)
        Assert.True(trend.SignedTrend > 0, $"signedTrend={trend.SignedTrend}");
        Assert.Equal(new[] { "price" }, trend.UsedFamilies.ToArray());
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);
        Assert.DoesNotContain(TrendEvaluator.BlockerTrendUnavailable, trend.BlockersForReady);
    }

    /// <summary>
    /// 현재 정책 고정(관측 표본의 "이 사유 단독 거절" 재현): 단조 상승 세션의 유효한 BREAKOUT 트리거가
    /// 다른 모든 게이트(계획·비용·netR·품질·신선도)를 통과하고도 MISSING_5M_STRUCTURE 하나로 거절된다.
    /// BREAKOUT은 alignmentQuality(추세 정렬)가 필수인 유형이라 이 차단은 설계 §16B와 일관된다.
    /// </summary>
    [Fact]
    public void AValidBreakoutIsRejectedSolelyForMissingFiveMinuteStructure()
    {
        var bars = MonotonicRise(90);
        var analysisAsOf = Fx.At(90);
        var trend = Trend(bars, analysisAsOf);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);

        var atr = SessionAtr.At(bars, SessionAtr.Series(bars, P), Fx.At(89));
        // [#43에서 fixture 갱신] 돌파 구간 Lower 104.30은 손절(104.27)을 진입 104.47의 왕복 수수료
        // ($0.2089) 안쪽인 $0.20에 두고 있었다 — #43이 막으려는 바로 그 초근접 손절이라 이 fixture로는
        // "MISSING_5M_STRUCTURE 단독 거절"을 더 이상 재현할 수 없다. Upper(=돌파 판정 기준선)는 그대로 두고
        // Lower만 내려 무효화 구조를 비용 밖에 둔다. 이 테스트의 관심사는 계획 비용이 아니라 5m 구조 차단이다.
        var zones = ImmutableArray.Create(
            D2.Resistance(104.15m, 104.45m, id: "breakout-zone"),
            D2.Resistance(105.50m, 105.80m, id: "target-zone"));
        var result = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, bars, zones, ImmutableArray<TouchEpisode>.Empty, trend, atr,
            104.47m, analysisAsOf, D2.Quote(104.46m, 104.48m, 90)), P);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(SetupKind.Breakout, candidate.Kind);
        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Equal(new[] { TrendEvaluator.BlockerMissing5mStructure }, candidate.RejectionCodes.ToArray());
        Assert.True(candidate.Planning.Viable);                               // 계획 자체는 성립했다
        Assert.True(candidate.Quality.ReadyAllowed);                          // 품질도 통과했다
        Assert.Null(result.PreferredCandidateId);
    }

    /// <summary>
    /// [#33(D7)에서 기대값 갱신] #30 시점에는 이 REBOUND가 MISSING_5M_STRUCTURE 단독으로 거절됐다
    /// (INTC 23:51 사례 재현). §8은 REBOUND에 높은 추세 점수를 요구하지 않고(CounterTrend=true) §9.4는
    /// alignmentQuality를 제외하므로 그 일괄 차단은 설계 모순이었고, #33이 차단을 PULLBACK/BREAKOUT으로
    /// 한정했다. 이제 같은 fixture가 READY에 도달하고, 코호트 분리 집계용 관측 note가 남는다.
    /// </summary>
    [Fact]
    public void AReboundReachesReadyDespiteMissingFiveMinuteStructureAndCarriesTheCohortNote()
    {
        var (bars, zones, episodes, analysisAsOf) = ReboundScenario();
        var computed = Trend(bars, analysisAsOf);
        Assert.Null(computed.StructureDirection);                             // 31분 세션 — 5m 피벗이 아직 없다
        Assert.Equal(new[] { TrendEvaluator.BlockerMissing5mStructure }, computed.BlockersForReady.ToArray());
        Assert.True(computed.SignedTrend < -P.TrendStateThreshold);           // #208 하한 아래인 fixture다
        var trend = computed with { SignedTrend = -P.TrendStateThreshold };

        var atr = SessionAtr.At(bars, SessionAtr.Series(bars, P), bars[^1].Start);
        var result = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, bars, zones, episodes, trend, atr, 100.00m, analysisAsOf,
            D2.Quote(99.99m, 100.01m, 31)), P);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(SetupKind.Rebound, candidate.Kind);
        Assert.True(candidate.CounterTrend);
        Assert.DoesNotContain("alignmentQuality", candidate.Quality.UsedComponents);   // §9.4: 추세 정렬 비필수
        Assert.Equal(CandidateDisposition.Ready, candidate.Disposition);
        Assert.Empty(candidate.RejectionCodes);
        Assert.Contains(SetupDetector.NoteReadyWithout5mStructure, candidate.Notes);
        Assert.NotNull(candidate.Plan);
        Assert.Equal(candidate.EventId, result.PreferredCandidateId);
        // TrendAssessment 산출과 표시용 ReadyBlockers는 그대로다 — 소비 지점(후보 거절)만 유형별 스코프다.
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, result.ReadyBlockers);
    }

    /// <summary>
    /// [#33(D7)에서 기대값 갱신] #30 시점에는 전 유형이 차단됐다. 조정 후: MISSING_5M_STRUCTURE는
    /// alignmentQuality(추세 정렬)가 필수인 PULLBACK/BREAKOUT만 거절하고, REBOUND는 스코프 밖이다
    /// (구조 결측 note를 달고 나머지 게이트 통과 시 READY).
    /// </summary>
    [Fact]
    public void TheStructureBlockerRejectsOnlyTheAlignmentRequiredKinds()
    {
        var (bars, zones, episodes, analysisAsOf) = ReboundScenario();
        var atr = SessionAtr.At(bars, SessionAtr.Series(bars, P), bars[^1].Start);

        // 눌림 저점(99.10)이 지지 하단 아래라 이 fixture는 UP에서 PULLBACK을, RANGE에서 REBOUND를 만든다(#208 C).
        SetupDetectionResult Detect(TrendState state) => SetupDetector.Detect(SetupDetectionRequest.Create(
            Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, analysisAsOf, analysisAsOf, bars, zones, episodes,
            D2.Trend(state, 40, structureDirection: null, readyBlockers: TrendEvaluator.BlockerMissing5mStructure),
            atr, 100.00m, analysisAsOf, D2.Quote(99.99m, 100.01m, 31)), P);

        var pullback = Detect(TrendState.Up).Candidates.Single(x => x.Kind == SetupKind.Pullback);
        Assert.Equal(CandidateDisposition.Rejected, pullback.Disposition);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, pullback.RejectionCodes);
        Assert.DoesNotContain(SetupDetector.NoteReadyWithout5mStructure, pullback.Notes);

        var reboundResult = Detect(TrendState.Range);
        var rebound = reboundResult.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
        Assert.DoesNotContain(TrendEvaluator.BlockerMissing5mStructure, rebound.RejectionCodes);
        Assert.Contains(SetupDetector.NoteReadyWithout5mStructure, rebound.Notes);
        Assert.Equal(rebound.EventId, reboundResult.PreferredCandidateId);
    }

    /// <summary>
    /// #33(D7) 경계 고정: REBOUND 면제는 MISSING_5M_STRUCTURE 하나뿐이다. trend 자체가 계산 불가한
    /// TREND_UNAVAILABLE은 REBOUND도 그대로 차단한다(가격 family 유효·trend not null이 전제 조건).
    /// </summary>
    [Fact]
    public void TrendUnavailableStillBlocksAReboundEvenAfterTheScopeChange()
    {
        var (bars, zones, episodes, analysisAsOf) = ReboundScenario();
        var atr = SessionAtr.At(bars, SessionAtr.Series(bars, P), bars[^1].Start);

        // TrendEvaluator가 계산 불가일 때 내보내는 조합 그대로: UNKNOWN + 두 blocker.
        var trend = D2.Trend(TrendState.Unknown, null, structureDirection: null,
            readyBlockers: [TrendEvaluator.BlockerTrendUnavailable, TrendEvaluator.BlockerMissing5mStructure]);
        var result = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, bars, zones, episodes, trend, atr, 100.00m, analysisAsOf,
            D2.Quote(99.99m, 100.01m, 31)), P);

        var rebound = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Contains(TrendEvaluator.BlockerTrendUnavailable, rebound.RejectionCodes);
        Assert.DoesNotContain(TrendEvaluator.BlockerMissing5mStructure, rebound.RejectionCodes);
        Assert.Null(result.PreferredCandidateId);
    }

    /// <summary>
    /// #33(D7) 경계 고정: 구조 결측이어도 나머지 게이트는 REBOUND에 전부 유지된다.
    /// 목표 구조가 없으면(§9.2) 여전히 거절이다. 코호트 note는 READY에 도달한 후보에만 붙는다(#65).
    /// </summary>
    [Fact]
    public void AReboundWithoutStructureStillKeepsEveryOtherGate()
    {
        var (bars, zones, episodes, analysisAsOf) = ReboundScenario();
        var trend = Trend(bars, analysisAsOf);
        var atr = SessionAtr.At(bars, SessionAtr.Series(bars, P), bars[^1].Start);

        // 목표 저항을 빼면 NO_TARGET_STRUCTURE로 거절된다 — 구조 결측 면제가 다른 게이트를 열지 않는다.
        var withoutTarget = zones.Where(x => x.Role == ZoneRole.Support).ToImmutableArray();
        var result = SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, bars, withoutTarget, episodes, trend, atr, 100.00m, analysisAsOf,
            D2.Quote(99.99m, 100.01m, 31)), P);

        var rebound = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Contains(StructuralPlanner.NoTargetStructure, rebound.RejectionCodes);
        Assert.DoesNotContain(TrendEvaluator.BlockerMissing5mStructure, rebound.RejectionCodes);
        Assert.Null(rebound.Plan);
        Assert.Null(result.PreferredCandidateId);
    }

    // ══ ① 피벗 규칙 — plateau 제외와 확인 지연(미래 누출 방지) ══

    /// <summary>§5.3 엄격 비교: 같은 고점이 이어지는 5m plateau는 피벗이 아니며 구조 방향을 만들지 못한다.</summary>
    [Fact]
    public void AFiveMinutePlateauIsNotAPivotAndLeavesStructureDirectionNull()
    {
        // 대조군: bucket 2가 유일한 고점 → HIGH 피벗 1개.
        var strict = FromBuckets([100.0m, 100.4m, 100.9m, 100.5m, 100.1m, 100.2m]);
        Assert.Single(FiveMinutePivots(strict, Fx.At(30)).Where(x => x.Kind == PivotKind.High));

        // plateau: bucket 2·3이 같은 고점 → 어느 쪽도 피벗이 아니다.
        var plateau = FromBuckets([100.0m, 100.4m, 100.9m, 100.9m, 100.5m, 100.1m]);
        Assert.Empty(FiveMinutePivots(plateau, Fx.At(30)).Where(x => x.Kind == PivotKind.High));

        var trend = Trend(plateau, Fx.At(30));
        Assert.Null(trend.StructureDirection);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);
    }

    /// <summary>
    /// §5.3/§14 E 미래 누출 방지: 5m 피벗은 우측 2버킷(10분) 확인 전에는 존재하지 않고,
    /// cutoff 이후의 봉을 입력에 덧붙여도 이전 cutoff의 평가가 바뀌지 않는다(피벗을 소급 확정하지 않는다).
    /// </summary>
    [Fact]
    public void PivotConfirmationIsNeverVisibleBeforeItsRightBucketsClose()
    {
        var bars = FromBuckets(EarliestTwoHighsTwoLows);

        // L@b8(40~45분 bucket)의 우측 확인은 bucket 10 종료(55분)다. 발생(40분) 후에도 확인 전엔 없다.
        var occurredButUnconfirmed = FiveMinutePivots(bars, Fx.At(50));
        Assert.DoesNotContain(occurredButUnconfirmed, x => x.Kind == PivotKind.Low && x.OccurredAt == Fx.At(40));

        // 전체 봉 배열을 든 채 이전 cutoff로 평가해도 잘린 입력과 동일하다(소급 없음).
        var withFuture = Trend(bars, Fx.At(50));
        var truncated = Trend(bars.Where(x => x.End <= Fx.At(50)).ToImmutableArray(), Fx.At(50));
        Assert.Contains(TrendEvaluator.WarningFutureBars, withFuture.Warnings);
        Assert.Null(withFuture.StructureDirection);
        Assert.Equal(truncated.StructureDirection, withFuture.StructureDirection);
        Assert.Equal(truncated.SignedTrend, withFuture.SignedTrend);
    }

    // ══ ② 세션 입력·5분 집계·피벗 경로 검증 ══

    /// <summary>
    /// §5.1/§5.2: 5m bucket은 세션 시작(09:30 ET = 22:30 KST)에 정렬되며, KST 자정을 넘는 세션에서도
    /// 타임스탬프의 offset 표기(KST/ET)와 무관하게 같은 bucket·피벗·추세가 나온다.
    /// </summary>
    [Fact]
    public void KstOffsetTimestampsAndTheMidnightBoundaryDoNotChangeTheStructurePath()
    {
        // 100분 = KST 자정(세션 시작 +90분)을 넘는 지그재그.
        var centers = new decimal[20];
        for (var k = 0; k < centers.Length; k++)
            centers[k] = 100m + (k % 4) switch { 0 => 0m, 1 => .4m, 2 => .9m, _ => .5m } + .05m * k;

        var kst = TimeSpan.FromHours(9);
        Candle Candle(int minute, decimal center, DateTimeOffset at) =>
            new(at, (double)center, (double)(center + .10m), (double)(center - .10m), (double)center, 1000);
        var etCandles = new List<Candle>();
        var kstCandles = new List<Candle>();
        for (var k = 0; k < centers.Length; k++)
            for (var i = 0; i < 5; i++)
            {
                var minute = k * 5 + i;
                etCandles.Add(Candle(minute, centers[k], Fx.At(minute)));
                kstCandles.Add(Candle(minute, centers[k], Fx.At(minute).ToOffset(kst)));
            }
        Assert.Contains(kstCandles, x => x.Timestamp.ToOffset(kst).Day != Fx.SessionStart.ToOffset(kst).Day);
        Assert.Equal(new TimeSpan(22, 30, 0), Fx.SessionStart.ToOffset(kst).TimeOfDay);   // 09:30 ET = 22:30 KST

        TrendAssessment Evaluate(IEnumerable<Candle> candles)
        {
            var bars = BarAggregator.Normalize(candles, Fx.SessionStart, Fx.SessionEnd, Fx.At(100)).Bars;
            var five = BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(100), P);
            Assert.All(five, bucket => Assert.Equal(0, (bucket.Start - Fx.SessionStart).TotalMinutes % 5));
            return TrendEvaluator.Evaluate(TrendRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.At(100), bars, five), P);
        }

        var et = Evaluate(etCandles);
        var viaKst = Evaluate(kstCandles);
        Assert.NotNull(et.StructureDirection);
        Assert.Equal(et.Fingerprint(), viaKst.Fingerprint());
    }

    /// <summary>
    /// §5.2/§5.3 결측 비인접 처리: 1분봉 하나가 빠지면 그 5m bucket은 집계되지 않고(IsContiguous),
    /// 그 bucket을 창으로 쓰는 피벗도 만들어지지 않는다 — 빈 구간을 인접 봉으로 위장해 구조를 만들지 않는다.
    /// </summary>
    [Fact]
    public void AMissingMinuteRemovesItsBucketAndEveryPivotWindowAcrossTheGap()
    {
        var full = FromBuckets(EarliestTwoHighsTwoLows);
        var complete = Trend(full, Fx.At(55));
        Assert.NotNull(complete.StructureDirection);

        // bucket 4(첫 LOW 피벗, 20~25분)의 1분봉 하나(21분)를 결측시킨다.
        var gapped = full.Where(x => x.Start != Fx.At(21)).ToImmutableArray();
        var fiveGapped = BarAggregator.Aggregate(gapped, Fx.SessionStart, Fx.At(55), P);
        Assert.DoesNotContain(fiveGapped, x => x.Start == Fx.At(20));         // bucket 자체가 없다

        var pivots = PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.FiveMinute, fiveGapped, Fx.At(55), P);
        Assert.DoesNotContain(pivots, x => x.OccurredAt <= Fx.At(30));        // 결측 구간을 걸치는 창은 전부 죽는다

        var trend = Trend(gapped, Fx.At(55));
        Assert.Null(trend.StructureDirection);                                // 2H+2L 미달로 되돌아간다
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);
    }

    /// <summary>
    /// cutoff 초과 봉 거절: 세션 입력 정규화는 미완성 봉을 세지 않고, 추세 평가는 1m·5m 양쪽에서
    /// cutoff 초과 봉을 잘라내며 경고를 남긴다(잘린 입력과 결과가 동일하다).
    /// </summary>
    [Fact]
    public void BarsBeyondTheCutoffAreRejectedOnBothTimeframes()
    {
        var bars = FromBuckets(EarliestTwoHighsTwoLows);

        // 입력 정규화: asOf 기준 미완성 봉은 유지되지 않는다.
        var candles = bars.Select(x => new Candle(x.Start, (double)x.Open, (double)x.High, (double)x.Low,
            (double)x.Close, x.Volume)).ToArray();
        var normalized = BarAggregator.Normalize(candles, Fx.SessionStart, Fx.SessionEnd, Fx.At(40));
        Assert.Equal(40, normalized.Bars.Length);
        Assert.Equal(15, normalized.DroppedIncomplete);

        // 추세 평가: cutoff 40에 전체 1m + 전체 5m(55분 집계본)를 그대로 넘겨도 잘라낸 입력과 같다.
        var fullFive = BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(55), P);
        var withFuture = TrendEvaluator.Evaluate(
            TrendRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.At(40), bars, fullFive), P);
        var clean = Trend(bars.Where(x => x.End <= Fx.At(40)).ToImmutableArray(), Fx.At(40));

        Assert.Contains(TrendEvaluator.WarningFutureBars, withFuture.Warnings);
        Assert.Equal(40, withFuture.BarCount);
        Assert.Equal(clean.StructureDirection, withFuture.StructureDirection);
        Assert.Equal(clean.SignedTrend, withFuture.SignedTrend);
        Assert.Equal(clean.BlockersForReady.ToArray(), withFuture.BlockersForReady.ToArray());
    }

    // ══ fixture ══

    /// <summary>단조 상승 세션: 매 분 +0.05, 약한 양봉. 피벗이 하나도 생기지 않는 경로.</summary>
    static ImmutableArray<StructureBar> MonotonicRise(int minutes)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var i = 0; i < minutes; i++)
        {
            var center = 100m + .05m * i;
            bars.Add(Bar(i, center - .02m, center + .10m, center - .10m, center + .02m,
                i == minutes - 1 ? 2000 : 1000));
        }
        return bars.ToImmutable();
    }

    /// <summary>
    /// 31분 세션의 실패한 하향 이탈 + 회복(REBOUND 트리거). 5m 피벗은 아직 없다(최소 관측 한계 이전).
    /// D2 후보 테스트의 예시 A 경로와 같은 형태이며, 눌림 저점 99.10이 지지 하단(99.20) 아래로
    /// 꼬리만 이탈했다가 종가는 하단 위에서 마감해 REBOUND 근거가 된다.
    /// </summary>
    static (ImmutableArray<StructureBar> Bars, ImmutableArray<PriceZone> Zones,
        ImmutableArray<TouchEpisode> Episodes, DateTimeOffset AnalysisAsOf) ReboundScenario()
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 25; m++) bars.Add(Bar(m, 100m, 100.10m, 99.90m, 100m));
        bars.Add(Bar(25, 99.85m, 99.90m, 99.30m, 99.35m));                    // 지지 접촉 시작
        bars.Add(Bar(26, 99.35m, 99.45m, 99.10m, 99.25m));                    // 꼬리 이탈(실패한 하향 이탈)
        bars.Add(Bar(27, 99.30m, 99.55m, 99.25m, 99.50m));
        bars.Add(Bar(28, 99.50m, 99.70m, 99.50m, 99.60m));
        bars.Add(Bar(29, 99.60m, 99.65m, 99.55m, 99.60m));
        bars.Add(Bar(30, 99.60m, 99.85m, 99.58m, 99.80m, 2000));              // 트리거
        var zones = ImmutableArray.Create(D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m));
        var episodes = ImmutableArray.Create(D2.Episode("support-zone", 25, 28));
        return (bars.ToImmutable(), zones, episodes, Fx.At(31));
    }
}
