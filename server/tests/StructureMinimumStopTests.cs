using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #43 — 최소 손절 거리 하한. netR(§9.3)은 비율만 보므로 손절폭이 0에 가까워지면 netRisk가 비용에
/// 수렴해 오히려 netR이 커진다. 비용·노이즈 안쪽 손절은 구조 무효화 지점이 아니라 체결 잡음에 걸리는 선이므로
/// 계획을 **거절**한다(§9.1 MaxRiskPercent 상한과 대칭). 손절을 넓히거나 옮기는 규칙이 아니다.
/// 여기 숫자는 관측 표본(#43 본문)의 재현이며 실제 종목 추천이 아니다.
/// </summary>
public sealed class StructureMinimumStopTests
{
    static readonly StructurePolicy P = D2.WideNetR;

    // ── #43 관측 재현: TSLA 12f42a9e ──

    /// <summary>
    /// 동결 계획 12f42a9e의 입력 재현: 진입 364.42 / 손절 364.34(손절폭 $0.08) / 목표 367.62, spread 결측.
    /// 왕복 수수료 $0.72884는 손절폭의 9.11배이고 netR은 3.055로 기존 게이트를 통과했다. 13.69초 뒤 STOP.
    /// ATR은 동결 필드에 없었으므로 buffer/0.15 역산값(≈0.385)을 입력으로 쓴다(#43 본문 구분과 동일).
    /// </summary>
    static PlanRequest Tsla12F42A9E() => new(
        "TSLA", "event-12f42a9e", "PULLBACK", 364.42m, 364.40m,
        D2.Support(364.30m, 364.50m, id: "tsla-support"),
        [D2.Support(364.30m, 364.50m, id: "tsla-support"), D2.Resistance(367.63m, 368.00m, id: "tsla-resistance")],
        .385, null, Fx.At(40), Fx.At(45));

    [Fact]
    public void TheTslaPlanThatPassedWithNetR3PointZeroFiveIsNowRejectedForACostSizedStop()
    {
        var result = StructuralPlanner.Evaluate(Tsla12F42A9E(), P);

        // 입력 재현이 관측치와 같은 가격을 만든다.
        Assert.Equal(364.34m, result.Stop);
        Assert.Equal(367.62m, result.Target);
        Assert.Equal(StructuralPlanner.BasisAtrNoise, result.BufferBasis);

        // 비용 공식·netR 임계값은 그대로다 — netR은 여전히 3.055로 계산되고, 그래도 거절된다.
        Assert.Equal(3.055m, Math.Round(result.NetR!.Value, 3));
        Assert.True(result.NetR > (decimal)P.MinimumNetR);
        Assert.False(result.Viable);
        Assert.Null(result.Plan);
        Assert.Contains(StructuralPlanner.StopInsideCost, result.ReasonCodes);

        // 손절폭 $0.08 < 왕복 수수료 $0.72884 (관측 배수 9.11).
        var stopDistance = 364.42m - result.Stop!.Value;
        var fee = 364.42m * (decimal)P.RoundTripFeePercent / 100m;
        Assert.Equal(.08m, stopDistance);
        Assert.Equal(.72884m, fee);
        Assert.True(stopDistance < fee);

        // 손절폭 0.208 ATR < 0.5 ATR이라 노이즈 하한에도 걸리지만 사유 코드는 통합돼 하나다(#209).
        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, result.ReasonCodes);
        Assert.Equal(new[] { StructuralPlanner.StopInsideCost }, result.ReasonCodes.ToArray());
    }

    /// <summary>거절은 손절을 옮기지 않는다. 보고되는 Stop은 구조에서 나온 364.34 그대로다(§9.1).</summary>
    [Fact]
    public void RejectingTheTslaPlanNeverMovesTheStructuralStop()
    {
        var result = StructuralPlanner.Evaluate(Tsla12F42A9E(), P);
        var fee = 364.42m * (decimal)P.RoundTripFeePercent / 100m;

        Assert.Equal(364.34m, result.Stop);                        // 구조 anchor - 0.15 ATR buffer 그대로
        Assert.Equal(364.40m - .05775m, 364.40m - (decimal)(.385 * P.StopBufferAtrFactor));
        Assert.NotEqual(StructureMath.FloorToCent(364.42m - fee), result.Stop);                    // 비용만큼 넓히지 않는다
        Assert.NotEqual(StructureMath.FloorToCent(364.42m - (decimal)(.385 * P.MinStopAtrFactor)), result.Stop);
        Assert.Equal(364.40m, result.Anchor);
    }

    // ── 변동성 대비 하한 ──

    /// <summary>
    /// 비용 하한은 만족하지만 손절폭이 1분 ATR의 0.5배 안쪽이면 노이즈 하한으로 거절한다.
    /// ATR=1.00, 손절폭 $0.30 → 하한 max($0.20, $0.50)=$0.50 미달. #209 통합 후 사유는 STOP_INSIDE_COST 하나다.
    /// </summary>
    [Fact]
    public void AStopInsideHalfOfTheOneMinuteAtrIsRejectedAsNoise()
    {
        var result = StructuralPlanner.Evaluate(NoiseCase(), P);

        Assert.False(result.Viable);
        Assert.Null(result.Plan);
        Assert.Equal(new[] { StructuralPlanner.StopInsideCost }, result.ReasonCodes.ToArray());
        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, result.ReasonCodes);
        Assert.Equal(99.70m, result.Stop);
        Assert.Equal(.30m, 100.00m - result.Stop!.Value);
        Assert.True(result.NetR > (decimal)P.MinimumNetR);          // netR은 통과했지만 위험이 실재하지 않는다
    }

    /// <summary>노이즈 하한 미달도 거절일 뿐 손절을 ATR 배수로 다시 만들지 않는다(v5 폴백 금지, §19-5).</summary>
    [Fact]
    public void TheNoiseFloorNeverBecomesAStopPrice()
    {
        var result = StructuralPlanner.Evaluate(NoiseCase(), P);

        Assert.Equal(99.70m, result.Stop);                          // anchor 99.85 - 0.15 ATR
        Assert.NotEqual(99.50m, result.Stop);                       // entry - 0.5 ATR(노이즈 하한)로 옮기지 않는다
        Assert.NotEqual(StructureMath.FloorToCent(100.00m - 1.00m), result.Stop);   // 1 ATR도 아니다
        Assert.Equal(.15m, result.Buffer);                          // buffer도 그대로다
    }

    /// <summary>ATR=1.00, anchor 99.85 → 손절 99.70(손절폭 $0.30). 비용 $0.20은 넘고 노이즈 하한 $0.50은 못 넘는다.</summary>
    static PlanRequest NoiseCase()
    {
        var support = D2.Support(99.70m, 99.90m, id: "shallow-support");
        return D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            Atr1mAtPlan = 1.00,
            InvalidationAnchor = 99.85m,
            InvalidationZone = support
        };
    }

    // ── §16A: ATR 결측·비양수는 0으로 대체하지 않는다 ──

    /// <summary>
    /// ATR이 null/0/음수/NaN/Infinity면 노이즈 하한을 **적용하지 않는다**. 결측을 0으로 바꿔
    /// "0 이상"이라는 무의미한 검사를 통과시키지도, 결측을 이유로 거절하지도 않는다(§16A).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AMissingOrNonPositiveAtrSkipsTheNoiseFloorEntirely(double? atr)
    {
        var support = D2.Support(99.70m, 99.90m, id: "shallow-support");
        var request = D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            InvalidationAnchor = 99.80m,
            InvalidationZone = support
        };

        var result = StructuralPlanner.Evaluate(request with { Atr1mAtPlan = atr }, P);

        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, result.ReasonCodes);
        Assert.DoesNotContain(StructuralPlanner.StopInsideCost, result.ReasonCodes);
        Assert.True(result.Viable);
        Assert.Equal(99.78m, result.Stop);                          // buffer는 spread 0.02에서 나온다
        Assert.Equal(.22m, 100.00m - result.Stop!.Value);           // 비용 하한($0.20)은 넘는다

        // 대조군: 같은 구조에 사용 가능한 ATR이 있으면 노이즈 하한이 적용돼 거절된다.
        var withAtr = StructuralPlanner.Evaluate(request with { Atr1mAtPlan = 1.00 }, P);
        Assert.Contains(StructuralPlanner.StopInsideCost, withAtr.ReasonCodes);
        Assert.Equal(99.65m, withAtr.Stop);
    }

    /// <summary>ATR이 없어도 비용 하한은 그대로 적용된다 — 결측이 면제가 되지 않는다.</summary>
    [Fact]
    public void TheCostFloorStillAppliesWhenTheAtrIsMissing()
    {
        var support = D2.Support(99.80m, 100.00m, id: "very-shallow-support");
        var request = D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            Atr1mAtPlan = null,
            InvalidationAnchor = 99.90m,
            InvalidationZone = support
        };
        var result = StructuralPlanner.Evaluate(request, P);

        Assert.False(result.Viable);
        Assert.Equal(99.88m, result.Stop);                          // anchor 99.90 - spread buffer 0.02
        Assert.Equal(.12m, 100.00m - result.Stop!.Value);           // 손절폭 $0.12 < 수수료 $0.20
        Assert.Contains(StructuralPlanner.StopInsideCost, result.ReasonCodes);
        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, result.ReasonCodes);
        Assert.Contains(StructuralPlanner.BufferFromTickOnly, result.Warnings);
    }

    // ── 회귀: 하한을 만족하는 계획은 종전 그대로다 ──

    /// <summary>§14 예시 A는 손절폭 $0.88로 수수료($0.20)와 노이즈 하한($0.10)을 모두 넘는다. 결과 불변.</summary>
    [Fact]
    public void APlanThatClearsBothFloorsIsUnchanged()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA(), P);

        Assert.True(result.Viable);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(99.12m, result.Stop);
        Assert.Equal(101.78m, result.Target);
        Assert.Equal(1.418m, Math.Round(result.NetR!.Value, 3));
        Assert.Equal(.88m, 100.00m - result.Stop!.Value);
        Assert.True(.88m >= 100.00m * (decimal)P.RoundTripFeePercent / 100m);
        Assert.True(.88m >= (decimal)(.20 * P.MinStopAtrFactor));
    }

    /// <summary>경계: 손절폭이 하한과 정확히 같으면 통과한다(하한은 `&gt;=`, §43 설계안 1·2).</summary>
    [Fact]
    public void AStopExactlyOnTheCostFloorIsAccepted()
    {
        // entry 100.00 → fee 0.20. anchor 100.21은 buffer 0.01(tick)로 손절 100.20을 만든다 — 진입 위라 무효.
        // 대신 손절폭이 정확히 0.20이 되도록 anchor를 잡는다: 99.82 - 0.02(spread) = 99.80.
        var support = D2.Support(99.70m, 99.90m, id: "exact-support");
        var request = D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            Atr1mAtPlan = .40,                                      // 노이즈 하한 = 0.20, 손절폭과 정확히 같다
            InvalidationAnchor = 99.86m,
            InvalidationZone = support
        };
        var result = StructuralPlanner.Evaluate(request, P);

        Assert.Equal(99.80m, result.Stop);                          // anchor 99.86 - 0.15*0.40 = 99.80
        Assert.Equal(.20m, 100.00m - result.Stop!.Value);
        Assert.True(result.Viable);
        Assert.DoesNotContain(StructuralPlanner.StopInsideCost, result.ReasonCodes);
        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, result.ReasonCodes);
    }

    /// <summary>#209: 두 하한은 max()로 합쳐진 하나의 규칙이고 사유 코드도 하나다. 값·거래 집합은 불변이다.</summary>
    [Fact]
    public void TheCostAndNoiseFloorsAreOneRuleWithOneReasonCode()
    {
        var support = D2.Support(99.70m, 99.90m, id: "merged-support");
        var request = D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            InvalidationAnchor = 99.85m,
            InvalidationZone = support
        };

        var noiseBinds = StructuralPlanner.Evaluate(request with { Atr1mAtPlan = 1.00 }, P);
        var costBinds = StructuralPlanner.Evaluate(request with { Atr1mAtPlan = .10, InvalidationAnchor = 99.92m }, P);

        Assert.Equal(new[] { StructuralPlanner.StopInsideCost }, noiseBinds.ReasonCodes.ToArray());
        Assert.Equal(new[] { StructuralPlanner.StopInsideCost }, costBinds.ReasonCodes.ToArray());
        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, noiseBinds.ReasonCodes);
        Assert.DoesNotContain(StructuralPlanner.StopInsideNoise, costBinds.ReasonCodes);
    }

    // ── 사유 코드가 관측·거절 목록에 실린다(#27/#28 분리 집계) ──

    /// <summary>
    /// 후보 경로에서도 같은 사유가 <c>RejectionCodes</c>로 노출된다. 지지 구간이 진입가 바로 아래 있는
    /// PULLBACK은 netR이 4를 넘어도 손절폭이 수수료 안쪽이라 REJECTED다.
    /// </summary>
    [Fact]
    public void TheCostFloorReasonReachesTheCandidateRejectionCodes()
    {
        var zones = ImmutableArray.Create(
            D2.Support(99.86m, 99.94m, id: "support-zone"),
            D2.Resistance(101.80m, 102.10m, id: "resistance-zone"));
        var request = SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, Fx.At(31), Fx.At(31),
            ShallowPullbackBars(), zones, [D2.Episode("support-zone", 25, 28)], D2.Trend(), .20, 100.00m,
            Fx.At(31), D2.Quote(99.99m, 100.01m, 31));

        var candidate = Assert.Single(SetupDetector.Detect(request, P).Candidates, x => x.Kind == SetupKind.Pullback);

        Assert.Equal(CandidateDisposition.Rejected, candidate.Disposition);
        Assert.Contains(StructuralPlanner.StopInsideCost, candidate.RejectionCodes);
        Assert.Null(candidate.Plan);
        Assert.Equal(99.83m, candidate.Planning.Stop);              // 거절해도 구조 손절은 그대로 보고한다
        Assert.True(candidate.Planning.NetR > (decimal)P.MinimumNetR);
    }

    /// <summary>진입가(100.00) 바로 아래 지지 [99.86,99.94]를 접촉했다가 회복한 얕은 눌림. 임의 값이다.</summary>
    static ImmutableArray<StructureBar> ShallowPullbackBars()
    {
        StructureBar Bar(int m, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
            => new(Fx.At(m), Fx.At(m + 1), open, high, low, close, volume);

        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 25; m++) bars.Add(Bar(m, 100.05m, 100.25m, 100.15m, 100.15m));
        bars.Add(Bar(25, 99.92m, 100.20m, 100.15m, 99.95m));        // 지지 접촉 시작
        bars.Add(Bar(26, 99.90m, 100.00m, 99.95m, 99.93m));         // episode 저점(구간 안)
        bars.Add(Bar(27, 99.91m, 99.99m, 99.93m, 99.96m));
        bars.Add(Bar(28, 99.95m, 100.06m, 99.96m, 100.02m));        // 구간 위로 회복
        bars.Add(Bar(29, 99.99m, 100.08m, 100.02m, 100.04m));
        bars.Add(Bar(30, 100.00m, 100.20m, 100.04m, 100.15m, 2000));// 트리거
        return bars.ToImmutable();
    }

    // ── 정책 ──

    [Fact]
    public void MinStopAtrFactorIsPolicyOwnedAndChangesThePolicyHash()
    {
        Assert.Equal(.5, P.MinStopAtrFactor);
        Assert.Contains("\"MinStopAtrFactor\":0.5", P.CanonicalJson, StringComparison.Ordinal);
        Assert.NotEqual(P.PolicyHash, (P with { MinStopAtrFactor = .6 }).PolicyHash);
    }

    // ── §11 FrozenPlanSnapshot additive 필드 ──

    /// <summary>
    /// #43-4: 동결 스냅샷에 Atr1mAtPlan을 함께 저장한다. 이 값이 없으면 거래 기록만으로
    /// "손절폭이 ATR 대비 얼마였나"를 재구성할 수 없다(#28 분석 입력). JSON 왕복에서도 보존된다.
    /// </summary>
    [Fact]
    public void TheFrozenSnapshotCarriesTheAtrUsedByThePlanAndSurvivesJsonRoundTrip()
    {
        var plan = StructuralPlanner.Evaluate(D2.ExampleA(), P).Plan!;
        var context = StructuralSimulation.Freeze(plan, "TEST|event-A", "UP", 40, 55.5, Fx.At(41), Fx.At(41));

        Assert.Equal(.20, context.PlanSnapshot.Atr1mAtPlan);
        Assert.Equal(plan.Atr1mAtPlan, context.PlanSnapshot.Atr1mAtPlan);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(context, options);
        var restored = JsonSerializer.Deserialize<FrozenStructureContext>(json, options)!;

        Assert.Equal(.20, restored.PlanSnapshot.Atr1mAtPlan);
        Assert.Contains("atr1mAtPlan", json, StringComparison.Ordinal);
        // 기존 필드도 그대로 왕복한다(additive 변경이므로 순서·의미가 바뀌지 않았다).
        Assert.Equal(context.PlanSnapshot.PlanId, restored.PlanSnapshot.PlanId);
        Assert.Equal(context.PlanSnapshot.Stop, restored.PlanSnapshot.Stop);
        Assert.Equal(context.PlanSnapshot.Target, restored.PlanSnapshot.Target);
        Assert.Equal(context.PlanSnapshot.NetR, restored.PlanSnapshot.NetR);
        Assert.Equal(context.PlanSnapshot.PolicyHash, restored.PlanSnapshot.PolicyHash);
        Assert.Equal(context.PlanSnapshot.ReasonCodes, restored.PlanSnapshot.ReasonCodes);
        Assert.Equal(context.PlanSnapshot.Explanation, restored.PlanSnapshot.Explanation);
    }

    /// <summary>ATR이 없던 계획은 null로 동결된다 — 결측을 0으로 대체하지 않는다(§16A).</summary>
    [Fact]
    public void APlanWithoutAtrFreezesTheFieldAsNullRatherThanZero()
    {
        var plan = StructuralPlanner.Evaluate(D2.ExampleA() with { Atr1mAtPlan = null }, P).Plan!;
        var context = StructuralSimulation.Freeze(plan, "TEST|event-A", "UP", 40, 55.5, Fx.At(41), Fx.At(41));

        Assert.Null(context.PlanSnapshot.Atr1mAtPlan);
        Assert.NotEqual(0d, context.PlanSnapshot.Atr1mAtPlan ?? -1);
    }

    /// <summary>
    /// additive 계약: 이 필드가 없던 기존 저장 행도 그대로 복원된다(기존 필드 순서·의미는 바뀌지 않았다).
    /// 복원값은 null이며 0으로 채우지 않는다.
    /// </summary>
    [Fact]
    public void SnapshotsStoredBeforeThisFieldStillDeserializeWithNull()
    {
        var plan = StructuralPlanner.Evaluate(D2.ExampleA(), P).Plan!;
        var context = StructuralSimulation.Freeze(plan, "TEST|event-A", "UP", 40, 55.5, Fx.At(41), Fx.At(41));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var legacy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(context.PlanSnapshot, options), options)!;
        Assert.True(legacy.Remove("atr1mAtPlan"));

        var restored = JsonSerializer.Deserialize<FrozenPlanSnapshot>(
            JsonSerializer.Serialize(legacy, options), options)!;

        Assert.Null(restored.Atr1mAtPlan);
        Assert.Equal(context.PlanSnapshot.PlanId, restored.PlanId);
        Assert.Equal(context.PlanSnapshot.Stop, restored.Stop);
        Assert.Equal(context.PlanSnapshot.Target, restored.Target);
        Assert.Equal(context.PlanSnapshot.NetRisk, restored.NetRisk);
        Assert.Equal(context.PlanSnapshot.MissingLiquidity, restored.MissingLiquidity);
        Assert.Equal(context.PlanSnapshot.EngineVersion, restored.EngineVersion);
        Assert.Equal(context.PlanSnapshot.ReasonCodes, restored.ReasonCodes);
    }
}
