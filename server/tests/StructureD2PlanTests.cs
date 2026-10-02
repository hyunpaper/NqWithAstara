using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §9.1~§9.3 + §14 예시 A~C. 구조로 정한 가격을 평가하는 것이지 손익비로 목표를 역산하지 않는다.
/// ATR 배수·1.5R 폴백이 호출되지 않음을 함께 고정한다(§19-5).
/// </summary>
public sealed class StructureD2PlanTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    // ── §14 A: 구조 계획 성립 ──

    [Fact]
    public void ExampleAReproducesStopTargetAndNetRExactly()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA(), P);

        Assert.True(result.Viable);
        Assert.Empty(result.ReasonCodes);
        // anchor=min(99.20,99.15)=99.15; buffer=max(0.01,0.03,0.02)=0.03; Stop=99.12
        Assert.Equal(99.15m, result.Anchor);
        Assert.Equal(.03m, result.Buffer);
        Assert.Equal(StructuralPlanner.BasisAtrNoise, result.BufferBasis);
        Assert.Equal(99.12m, result.Stop);
        // 다음 resistance=[101.80,102.10]; frontRun=0.02; Target=101.78
        Assert.Equal(101.78m, result.Target);
        // fee=0.20, extra=0.02; netReward=1.56; netRisk=1.10; netR≈1.418
        Assert.Equal(.20m, result.Plan!.Costs.FeePerShare);
        Assert.Equal(.02m, result.Plan.Costs.ExtraCostPerShare);
        Assert.Equal(1.56m, result.NetReward);
        Assert.Equal(1.10m, result.NetRisk);
        Assert.Equal(1.418m, Math.Round(result.NetR!.Value, 3));
        Assert.Equal(.88, Math.Round(result.RiskPercent!.Value, 10));
        Assert.False(result.MissingLiquidity);
        Assert.Equal(P.PolicyHash, result.Plan.PolicyHash);
        Assert.Equal("v5-structure.2", result.Plan.EngineVersion);
    }

    /// <summary>손익비가 먼저 정해진 뒤 목표가 만들어진 것이 아니다(§14 A 마지막 문장).</summary>
    [Fact]
    public void ExampleATargetComesFromTheResistanceZoneNotFromTheRiskMultiple()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA(), P);
        var risk = result.Plan!.EntryReference - result.Plan.Stop;

        Assert.Equal(101.80m, result.TargetZone!.Lower);
        Assert.Equal(result.Plan.Target, StructureMath.FloorToCent(result.TargetZone.Lower - result.Plan.FrontRunBuffer));
        Assert.NotEqual(StructureMath.FloorToCent(result.Plan.EntryReference + risk * 1.5m), result.Plan.Target);
        Assert.NotEqual(StructureMath.FloorToCent(result.Plan.EntryReference + 3 * .20m), result.Plan.Target);
    }

    // ── §14 B: 가까운 저항으로 진입 거절 ──

    [Fact]
    public void ExampleBRejectsOnRewardToRiskAndNeverSubstitutesTheFartherResistance()
    {
        var near = D2.Resistance(100.45m, 100.70m, id: "near-resistance");
        var far = D2.Resistance(101.80m, 102.10m, id: "far-resistance");
        var result = StructuralPlanner.Evaluate(D2.ExampleA(D2.Support(99.20m, 99.40m), near, far), P);

        Assert.False(result.Viable);
        Assert.Null(result.Plan);
        Assert.Contains(StructuralPlanner.InsufficientRewardToRisk, result.ReasonCodes);
        Assert.Equal("near-resistance", result.TargetZone!.Id);
        Assert.Equal(100.43m, result.Target);
        Assert.Equal(.21m, result.NetReward);
        Assert.Equal(1.10m, result.NetRisk);
        Assert.Equal(.191m, Math.Round(result.NetR!.Value, 3));
        // 먼 저항을 대신 선택하거나 목표를 101.65로 만들어 통과시키면 실패다.
        Assert.NotEqual(101.78m, result.Target);
        Assert.NotEqual(101.65m, result.Target);
    }

    // ── §14 C: 위쪽 저항 없음 ──

    [Fact]
    public void ExampleCReturnsNoTargetStructureEvenWithStrongTrendAndSupport()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA(D2.Support(99.20m, 99.40m)), P);

        Assert.False(result.Viable);
        Assert.Null(result.Target);
        Assert.Null(result.TargetZone);
        Assert.Equal(new[] { StructuralPlanner.NoTargetStructure }, result.ReasonCodes.ToArray());
        Assert.Equal(99.12m, result.Stop);       // 손절은 구조에서 나왔지만 목표가 없으면 진입하지 않는다
    }

    /// <summary>목표 구조가 없을 때 v4 폴백(3 ATR / 손절폭 1.5배)이 만들어내는 가격이 v5에 나타나지 않는다.</summary>
    [Fact]
    public void NoTargetStructureNeverFallsBackToTheV4AtrOrRiskMultipleTarget()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA(D2.Support(99.20m, 99.40m)), P);
        // 같은 입력으로 v4 폴백을 실제로 호출해 어떤 숫자가 나오는지 확인한다.
        var v4 = PriceLevels.Enter(100.00, 10, .20, []);

        Assert.Null(result.Target);
        Assert.Null(result.Plan);
        Assert.Equal(100.60, v4.Target!.Value, 10);                // v4: entry + 3 ATR
        Assert.Equal(99.50, v4.Stop!.Value, 10);                   // v4: entry - 2.5 ATR
        Assert.DoesNotContain(result.ReasonCodes, x => x.Contains("ATR", StringComparison.Ordinal));
        Assert.NotEqual((decimal)v4.Target.Value, result.Target ?? 0m);
        Assert.NotEqual((decimal)v4.Stop.Value, result.Stop ?? 0m);
    }

    // ── 지지 없음 / 비용 초과 / 공간 없음 / 위험 과다 ──

    [Fact]
    public void MissingInvalidationStructureYieldsNullStopAndBlocksReady()
    {
        var request = D2.ExampleA() with { InvalidationAnchor = null, InvalidationZone = null };
        var result = StructuralPlanner.Evaluate(request, P);

        Assert.False(result.Viable);
        Assert.Null(result.Stop);
        Assert.Null(result.Buffer);
        Assert.Contains(StructuralPlanner.NoInvalidationStructure, result.ReasonCodes);
        // Entry의 일정 %나 고정 ATR 배수로 손절을 만들어내지 않는다(§9.1).
        Assert.DoesNotContain(StructuralPlanner.RiskTooWide, result.ReasonCodes);
    }

    [Fact]
    public void CostExceedsRoomWhenTheNearestResistanceIsInsideTheRoundTripCost()
    {
        var result = StructuralPlanner.Evaluate(
            D2.ExampleA(D2.Support(99.20m, 99.40m), D2.Resistance(100.21m, 100.40m, id: "tight")), P);

        Assert.False(result.Viable);
        Assert.Equal(100.19m, result.Target);
        Assert.Equal(-.03m, result.NetReward);
        Assert.Contains(StructuralPlanner.CostExceedsRoom, result.ReasonCodes);
    }

    [Fact]
    public void NoTargetRoomWhenTheFrontRunBufferPullsTheTargetToOrBelowEntry()
    {
        var result = StructuralPlanner.Evaluate(
            D2.ExampleA(D2.Support(99.20m, 99.40m), D2.Resistance(100.02m, 100.30m, id: "touching")), P);

        Assert.False(result.Viable);
        Assert.Equal(100.00m, result.Target);
        Assert.Contains(StructuralPlanner.NoTargetRoom, result.ReasonCodes);
        Assert.DoesNotContain(StructuralPlanner.InsufficientRewardToRisk, result.ReasonCodes);
    }

    [Fact]
    public void RiskTooWideRejectsInsteadOfPullingTheStopInsideTheStructure()
    {
        var support = D2.Support(97.50m, 97.80m);
        var request = D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            InvalidationAnchor = 97.45m,
            InvalidationZone = support
        };
        var result = StructuralPlanner.Evaluate(request, P);

        Assert.False(result.Viable);
        Assert.Contains(StructuralPlanner.RiskTooWide, result.ReasonCodes);
        Assert.Equal(97.42m, result.Stop);                     // 구조 손절을 그대로 보고한다
        Assert.True(result.RiskPercent > P.MaxRiskPercent);
        Assert.NotEqual(98.00m, result.Stop);                  // 2% 안쪽으로 당기지 않는다
    }

    [Fact]
    public void StopMustStayBelowEntryAndAboveZero()
    {
        var support = D2.Support(100.40m, 100.60m);
        var request = D2.ExampleA(support, D2.Resistance(101.80m, 102.10m)) with
        {
            InvalidationAnchor = 100.50m,
            InvalidationZone = support
        };
        var result = StructuralPlanner.Evaluate(request, P);

        Assert.False(result.Viable);
        Assert.Contains(StructuralPlanner.StopNotBelowEntry, result.ReasonCodes);
    }

    // ── 비용 모델과 호가 ──

    [Fact]
    public void MissingSpreadAssumesZeroCostAndExposesTheUncertainty()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA() with { ValidSpread = null }, P);

        Assert.True(result.Viable);
        Assert.True(result.MissingLiquidity);
        Assert.Contains(StructuralPlanner.MissingLiquidityCost, result.Plan!.ReasonCodes);
        Assert.Equal(0m, result.Plan.Costs.ExtraCostPerShare);
        Assert.Equal(99.12m, result.Stop);                     // buffer는 여전히 0.15 ATR
        Assert.Equal(101.79m, result.Target);                  // frontRun=max(0.01,0)=0.01
        // 결측이 더 유리한 netR을 만들 수 있다는 한계를 숫자로 남긴다(§16B).
        Assert.True(result.NetR > 1.418m);
    }

    [Fact]
    public void SpreadWiderThanTheAtrBufferBecomesTheStopBuffer()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA() with { ValidSpread = .07m }, P);

        Assert.Equal(.07m, result.Buffer);
        Assert.Equal(StructuralPlanner.BasisSpread, result.BufferBasis);
        Assert.Equal(99.08m, result.Stop);
        Assert.Equal(101.73m, result.Target);
    }

    [Fact]
    public void MissingAtrLeavesTheTickFloorBufferAndFlagsIt()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA() with { Atr1mAtPlan = null, ValidSpread = null }, P);

        Assert.Equal(.01m, result.Buffer);
        Assert.Equal(StructuralPlanner.BasisTickFloor, result.BufferBasis);
        Assert.Contains(StructuralPlanner.BufferFromTickOnly, result.Warnings);
        Assert.Equal(99.14m, result.Stop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void NonUsableAtrNeverProducesNaNPrices(double atr)
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA() with { Atr1mAtPlan = atr }, P);

        Assert.Equal(.02m, result.Buffer);                     // spread가 tick floor보다 크다
        Assert.Equal(99.13m, result.Stop);
        Assert.True(result.NetR > 0);
    }

    [Fact]
    public void SpreadValidationRejectsStaleCrossedAndMissingQuotesButAllowsLockedZero()
    {
        var now = Fx.At(40);
        var (locked, lockedReasons) = StructureLiquidityRules.Validate(D2.Quote(100m, 100m, 40), now,
            Fx.SessionStart, Fx.SessionEnd, P);
        Assert.Equal(0m, locked);
        Assert.DoesNotContain(StructureLiquidityRules.ReasonCrossed, lockedReasons);

        var (crossed, crossedReasons) = StructureLiquidityRules.Validate(D2.Quote(100.05m, 100m, 40), now,
            Fx.SessionStart, Fx.SessionEnd, P);
        Assert.Null(crossed);
        Assert.Contains(StructureLiquidityRules.ReasonCrossed, crossedReasons);

        var (stale, staleReasons) = StructureLiquidityRules.Validate(D2.Quote(100m, 100.02m, 38), now,
            Fx.SessionStart, Fx.SessionEnd, P);
        Assert.Null(stale);
        Assert.Contains(StructureLiquidityRules.ReasonStale, staleReasons);

        var (missing, missingReasons) = StructureLiquidityRules.Validate(null, now, Fx.SessionStart, Fx.SessionEnd, P);
        Assert.Null(missing);
        Assert.Contains(StructureLiquidityRules.ReasonMissing, missingReasons);

        // §16B는 "양쪽 양수 가격/잔량"을 요구한다. 잔량을 모르면 검증되지 않은 호가이므로 비용으로 쓰지 않는다.
        var (sizeless, sizelessReasons) = StructureLiquidityRules.Validate(
            new StructureLiquidity(100m, 100.02m, Fx.At(40)), now, Fx.SessionStart, Fx.SessionEnd, P);
        Assert.Null(sizeless);
        Assert.Contains(StructureLiquidityRules.ReasonSizeUnknown, sizelessReasons);

        var (zeroSize, zeroSizeReasons) = StructureLiquidityRules.Validate(D2.Quote(100m, 100.02m, 40, 0), now,
            Fx.SessionStart, Fx.SessionEnd, P);
        Assert.Null(zeroSize);
        Assert.Contains(StructureLiquidityRules.ReasonSizeNonPositive, zeroSizeReasons);
    }

    /// <summary>
    /// R2/§16B: 잔량 결측과 비양수는 서로 독립적으로 평가한다. 한쪽이 null이라고 다른 쪽의 0 검사를
    /// 건너뛰지 않으며, 어느 쪽이든 걸리면 spread를 결측으로 넘긴다.
    /// </summary>
    [Theory]
    [InlineData(null, null, true, false)]        // 양쪽 결측
    [InlineData(100d, null, true, false)]        // 한쪽만 결측
    [InlineData(0d, 100d, false, true)]          // 한쪽만 0
    [InlineData(null, 0d, true, true)]           // 한쪽 결측 + 다른 쪽 0 → 두 사유 모두
    [InlineData(-1d, 100d, false, true)]         // 음수 잔량
    public void UnverifiedQuoteSizesNeverBecomeAUsableSpread(double? bidSize, double? askSize,
        bool expectUnknown, bool expectNonPositive)
    {
        var (spread, reasons) = StructureLiquidityRules.Validate(
            new StructureLiquidity(100m, 100.02m, Fx.At(40), bidSize, askSize), Fx.At(40),
            Fx.SessionStart, Fx.SessionEnd, P);

        Assert.Null(spread);
        Assert.Equal(expectUnknown, reasons.Contains(StructureLiquidityRules.ReasonSizeUnknown));
        Assert.Equal(expectNonPositive, reasons.Contains(StructureLiquidityRules.ReasonSizeNonPositive));
    }

    [Fact]
    public void BothSidesPositiveSizeStillProducesTheUsableSpread()
    {
        var (spread, reasons) = StructureLiquidityRules.Validate(D2.Quote(100m, 100.02m, 40), Fx.At(40),
            Fx.SessionStart, Fx.SessionEnd, P);

        Assert.Equal(.02m, spread);
        Assert.Empty(reasons);
    }

    /// <summary>미검증 호가는 기존 결측 경로로 넘어간다. 계획을 막지는 않되 spread를 비용으로 쓰지 않는다.</summary>
    [Fact]
    public void AQuoteRejectedForItsSizesFallsIntoTheMissingLiquidityCostPath()
    {
        var (spread, _) = StructureLiquidityRules.Validate(
            new StructureLiquidity(100m, 100.02m, Fx.At(40)), Fx.At(40), Fx.SessionStart, Fx.SessionEnd, P);
        var result = StructuralPlanner.Evaluate(D2.ExampleA() with { ValidSpread = spread }, P);

        Assert.True(result.Viable);
        Assert.True(result.MissingLiquidity);
        Assert.Contains(StructuralPlanner.MissingLiquidityCost, result.Plan!.ReasonCodes);
        Assert.Equal(0m, result.Plan.Costs.ExtraCostPerShare);
        Assert.Null(result.Plan.Costs.ValidSpread);
    }

    // ── 목표 자격과 결정성 ──

    [Fact]
    public void OnlyEligibleNonProfileResistanceZonesAboveEntryCanBecomeTargets()
    {
        var ineligible = D2.Zone("ineligible", 100.50m, 100.70m, ZoneRole.Resistance, .2, eligible: false);
        var broken = D2.Zone("broken", 100.80m, 100.95m, ZoneRole.Broken, .8);
        var retired = D2.Zone("retired", 101.00m, 101.20m, ZoneRole.Resistance, .8, retired: true);
        var profile = D2.Zone("profile", 101.30m, 101.50m, ZoneRole.Resistance, .8, profileOnly: true);
        var below = D2.Resistance(99.50m, 99.70m, id: "below-entry");
        var good = D2.Resistance(101.80m, 102.10m, id: "good");

        var chosen = StructuralPlanner.NearestQualifiedResistance(
            [ineligible, broken, retired, profile, below, good], 100.00m);

        Assert.Equal("good", chosen!.Id);
    }

    // ── R3/§19-5: 진입가를 감싸는 저항 ──

    /// <summary>
    /// 진입가가 적격 저항 구간 안이면 그 저항을 건너뛰고 위쪽 먼 저항을 목표로 만들지 않는다.
    /// 계획을 <c>ENTRY_INSIDE_RESISTANCE</c>로 거절하고 Target은 null로 둔다.
    /// </summary>
    [Fact]
    public void EntryInsideAQualifiedResistanceRejectsThePlanInsteadOfTargetingTheFartherOne()
    {
        var enclosing = D2.Resistance(99.90m, 100.20m, id: "enclosing");
        var far = D2.Resistance(101.80m, 102.10m, id: "far");
        var result = StructuralPlanner.Evaluate(
            D2.ExampleA(D2.Support(99.20m, 99.40m), enclosing, far), P);

        Assert.False(result.Viable);
        Assert.Null(result.Plan);
        Assert.Contains(StructuralPlanner.EntryInsideResistance, result.ReasonCodes);
        Assert.Null(result.Target);
        Assert.Null(result.TargetZone);
        Assert.DoesNotContain(StructuralPlanner.NoTargetStructure, result.ReasonCodes);
        Assert.NotEqual(101.78m, result.Target ?? 0m);          // 먼 저항으로 대체하지 않는다
        Assert.Equal(99.12m, result.Stop);                      // 손절은 구조 그대로 보고한다
        Assert.Equal("enclosing", StructuralPlanner.EnclosingQualifiedResistance(
            [D2.Support(99.20m, 99.40m), enclosing, far], 100.00m)!.Id);
    }

    /// <summary>경계 규칙은 <c>Lower &lt;= entry &lt; Upper</c>다. Upper에 정확히 도달한 진입은 구간 안이 아니다.</summary>
    [Theory]
    [InlineData(100.00, 100.20, true)]      // entry == Lower → 안
    [InlineData(99.90, 100.20, true)]       // 구간 내부
    [InlineData(99.80, 100.00, false)]      // entry == Upper → 밖 (BREAKOUT 트리거가 Upper 위 마감)
    [InlineData(100.01, 100.20, false)]     // 진입가 위 → 정상 목표 후보
    public void ResistanceBoundaryIsLowerInclusiveAndUpperExclusive(double lower, double upper, bool inside)
    {
        var zone = D2.Resistance((decimal)lower, (decimal)upper, id: "boundary");
        var far = D2.Resistance(101.80m, 102.10m, id: "far");
        var result = StructuralPlanner.Evaluate(D2.ExampleA(D2.Support(99.20m, 99.40m), zone, far), P);

        Assert.Equal(inside, StructuralPlanner.EnclosingQualifiedResistance([zone], 100.00m) is not null);
        Assert.Equal(inside, result.ReasonCodes.Contains(StructuralPlanner.EntryInsideResistance));
        if (inside) Assert.Null(result.Target);
        else Assert.NotNull(result.Target);
    }

    /// <summary>자격 없는·retired·profile-only 구간은 진입가를 감싸도 거절 사유가 아니다. 예시 A도 그대로다.</summary>
    [Fact]
    public void OnlyQualifiedResistancesCanEncloseTheEntryAndExampleAStaysUnaffected()
    {
        var ineligible = D2.Zone("ineligible", 99.90m, 100.20m, ZoneRole.Resistance, .2, eligible: false);
        var retired = D2.Zone("retired", 99.90m, 100.20m, ZoneRole.Resistance, .8, retired: true);
        var profile = D2.Zone("profile", 99.90m, 100.20m, ZoneRole.Resistance, .8, profileOnly: true);
        var broken = D2.Zone("broken", 99.90m, 100.20m, ZoneRole.Broken, .8);
        var result = StructuralPlanner.Evaluate(D2.ExampleA(D2.Support(99.20m, 99.40m), ineligible, retired,
            profile, broken, D2.Resistance(101.80m, 102.10m, id: "far")), P);

        Assert.Null(StructuralPlanner.EnclosingQualifiedResistance(
            [ineligible, retired, profile, broken], 100.00m));
        Assert.True(result.Viable);
        Assert.Equal(101.78m, result.Target);

        var exampleA = StructuralPlanner.Evaluate(D2.ExampleA(), P);
        Assert.True(exampleA.Viable);
        Assert.DoesNotContain(StructuralPlanner.EntryInsideResistance, exampleA.ReasonCodes);
        Assert.Equal(101.78m, exampleA.Target);
    }

    [Fact]
    public void RepeatedEvaluationOfTheSameRequestIsByteIdentical()
    {
        var a = StructuralPlanner.Evaluate(D2.ExampleA(), P);
        var b = StructuralPlanner.Evaluate(D2.ExampleA(), P);

        Assert.Equal(a.Plan!.Fingerprint(), b.Plan!.Fingerprint());
        Assert.Equal(a.Plan.PlanId, b.Plan.PlanId);
    }

    /// <summary>계산 수준의 plan freeze: 이미 만든 계획 객체는 이후 ATR/피벗/프로파일 변화에 영향받지 않는다(§10, §15).</summary>
    [Fact]
    public void AFrozenPlanIsUnchangedWhenLaterAtrAndStructuresMove()
    {
        var frozen = StructuralPlanner.Evaluate(D2.ExampleA(), P).Plan!;
        var frozenFingerprint = frozen.Fingerprint();

        var later = StructuralPlanner.Evaluate(D2.ExampleA(D2.Support(99.05m, 99.35m),
            D2.Resistance(100.90m, 101.10m, id: "new-resistance")) with { Atr1mAtPlan = .55, ValidSpread = .09m }, P);

        Assert.Equal(frozenFingerprint, frozen.Fingerprint());
        Assert.Equal(99.12m, frozen.Stop);
        Assert.Equal(101.78m, frozen.Target);
        Assert.NotEqual(frozen.Stop, later.Stop);
    }

    [Fact]
    public void PlanIsRefusedForUnsupportedTickOrSubDollarPrices()
    {
        var unsupported = StructuralPlanner.Evaluate(D2.ExampleA() with { PriceTickSupported = false }, P);
        Assert.Contains(StructuralPlanner.UnsupportedPriceTick, unsupported.ReasonCodes);
        Assert.Null(unsupported.Plan);

        var penny = StructuralPlanner.Evaluate(D2.ExampleA() with { EntryReference = .80m }, P);
        Assert.Contains(StructuralPlanner.PriceBelowMinimumSupported, penny.ReasonCodes);
        Assert.Null(penny.Plan);
    }

    [Fact]
    public void PricesStayOnTheDecimalPathWithoutBinaryDrift()
    {
        // 99.15-0.03은 double에서 99.11999...가 되어 floor가 99.11로 내려갈 수 있다(§16A).
        var result = StructuralPlanner.Evaluate(D2.ExampleA(), P);
        Assert.Equal(99.12m, result.Stop);
        Assert.Equal(99.12m, decimal.Parse("99.12", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(101.78m, result.Target);
    }

    [Fact]
    public void EmptyZoneListProducesBothMissingStructureReasonsAndNoPlan()
    {
        var request = D2.ExampleA() with
        {
            Zones = ImmutableArray<PriceZone>.Empty,
            InvalidationAnchor = null,
            InvalidationZone = null
        };
        var result = StructuralPlanner.Evaluate(request, P);

        Assert.Null(result.Plan);
        Assert.Equal(
            new[] { StructuralPlanner.NoInvalidationStructure, StructuralPlanner.NoTargetStructure },
            result.ReasonCodes.ToArray());
    }
}
