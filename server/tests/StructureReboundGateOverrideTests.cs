using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>REBOUND 전용 손절폭 상한·최소 netR 재정의 계약(§9.1, §9.3, #245).</summary>
public sealed class StructureReboundGateOverrideTests
{
    static PlanRequest WideRisk(string kind)
    {
        var support = D2.Support(97.70m, 97.90m);
        var resistance = D2.Resistance(104.00m, 104.30m);
        return D2.ExampleA(support, resistance) with { Kind = kind, InvalidationAnchor = 97.75m };
    }

    static PlanRequest LowNetR(string kind)
    {
        var support = D2.Support(99.20m, 99.40m);
        var resistance = D2.Resistance(101.25m, 101.55m);
        return D2.ExampleA(support, resistance) with { Kind = kind, InvalidationAnchor = 99.25m };
    }

    [Fact]
    public void NullOverridesKeepTheDefaultHashAndCanonicalJson()
    {
        var json = StructurePolicy.Default.CanonicalJson;
        Assert.Null(StructurePolicy.Default.ReboundMaxRiskPercent);
        Assert.Null(StructurePolicy.Default.ReboundMinimumNetR);
        Assert.DoesNotContain("ReboundMaxRiskPercent", json);
        Assert.DoesNotContain("ReboundMinimumNetR", json);
    }

    [Fact]
    public void NullOverridesKeepTheDefaultAndCycle45Hashes()
    {
        var cycle45 = StructurePolicy.Default with
        {
            RequireCompleteLiquidityCost = false,
            RequirePullbackNearVwap = true,
            RequireBreakoutNearVwap = true,
            RequireMinimumReboundEntryQuality = true,
            RequireBreakoutAboveVwap = true,
            RequireMaximumReboundNetR = true,
            EnableTwoRFeeBreakEvenStop = true,
            CapStructuralTargetAtTwoR = true,
            EnableHalfRFeeBreakEvenStopForPositiveBenchmark = true,
            AllowQualifiedTransitionPullback = true,
            AllowQualifiedTransitionBreakout = true,
            ExemptBreakoutFromPositiveBenchmarkHalfRStop = true
        };
        Assert.Equal("252c300a0c1876d7f25dc1d712879f900367f4f2ab67720856915eb20f8018d5", StructurePolicy.Default.PolicyHash);
        Assert.Equal("469148055b82e8349f4188b5f6a079d20508edb3bac8b5a009d7c5444213880d", cycle45.PolicyHash);
    }

    [Fact]
    public void SettingAnOverrideChangesTheHash()
    {
        var baseline = StructurePolicy.Default.PolicyHash;
        var risk = StructurePolicy.Default with { ReboundMaxRiskPercent = 2.5 };
        var netR = StructurePolicy.Default with { ReboundMinimumNetR = 1.0 };
        Assert.NotEqual(baseline, risk.PolicyHash);
        Assert.NotEqual(baseline, netR.PolicyHash);
        Assert.NotEqual(risk.PolicyHash, netR.PolicyHash);
        Assert.Contains("\"ReboundMaxRiskPercent\":2.5", risk.CanonicalJson);
        Assert.Contains("\"ReboundMinimumNetR\":1", netR.CanonicalJson);
    }

    [Fact]
    public void WideRiskIsRejectedForEveryKindByDefault()
    {
        foreach (var kind in new[] { "REBOUND", "PULLBACK", "BREAKOUT" })
        {
            var result = StructuralPlanner.Evaluate(WideRisk(kind), StructurePolicy.Default);
            Assert.Contains(StructuralPlanner.RiskTooWide, result.ReasonCodes);
            Assert.True(result.RiskPercent > 2.0 && result.RiskPercent < 2.5);
        }
    }

    [Fact]
    public void ReboundRiskOverrideAdmitsOnlyRebound()
    {
        var policy = StructurePolicy.Default with { ReboundMaxRiskPercent = 2.5 };
        Assert.True(StructuralPlanner.Evaluate(WideRisk("REBOUND"), policy).Viable);
        Assert.Contains(StructuralPlanner.RiskTooWide, StructuralPlanner.Evaluate(WideRisk("PULLBACK"), policy).ReasonCodes);
        Assert.Contains(StructuralPlanner.RiskTooWide, StructuralPlanner.Evaluate(WideRisk("BREAKOUT"), policy).ReasonCodes);
    }

    [Fact]
    public void ReboundNetROverrideAdmitsOnlyRebound()
    {
        var policy = StructurePolicy.Default with { ReboundMinimumNetR = 1.0 };
        Assert.Contains(StructuralPlanner.InsufficientRewardToRisk,
            StructuralPlanner.Evaluate(LowNetR("REBOUND"), StructurePolicy.Default).ReasonCodes);
        var rebound = StructuralPlanner.Evaluate(LowNetR("REBOUND"), policy);
        Assert.True(rebound.Viable);
        Assert.Equal(1.01m, rebound.NetR);
        Assert.Contains(StructuralPlanner.InsufficientRewardToRisk,
            StructuralPlanner.Evaluate(LowNetR("PULLBACK"), policy).ReasonCodes);
    }
}
