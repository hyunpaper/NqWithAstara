using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureMaxNetRTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static PlanRequest WithResistanceLower(decimal resistanceLower)
    {
        var support = D2.Support(99.20m, 99.40m);
        var resistance = D2.Resistance(resistanceLower, resistanceLower + .30m);
        return D2.ExampleA(support, resistance) with { InvalidationAnchor = 99.25m };
    }

    [Theory]
    [InlineData(101.33, 1.09)]
    [InlineData(101.34, 1.10)]
    [InlineData(102.24, 2.00)]
    [InlineData(102.25, 2.01)]
    public void NetRisOneDollarSoTheTargetPriceSetsNetRExactly(double resistanceLower, double expectedNetR)
    {
        var result = StructuralPlanner.Evaluate(WithResistanceLower((decimal)resistanceLower), P);

        Assert.Equal(99.22m, result.Stop);
        Assert.Equal(1.00m, result.NetRisk);
        Assert.Equal((decimal)expectedNetR, result.NetR);
    }

    [Fact]
    public void NetRJustBelowTheMinimumIsRejectedAsInsufficient()
    {
        var result = StructuralPlanner.Evaluate(WithResistanceLower(101.33m), P);

        Assert.False(result.Viable);
        Assert.Contains(StructuralPlanner.InsufficientRewardToRisk, result.ReasonCodes);
        Assert.DoesNotContain(StructuralPlanner.ExcessiveRewardToRisk, result.ReasonCodes);
    }

    [Fact]
    public void NetRExactlyAtTheMinimumIsAccepted()
    {
        var result = StructuralPlanner.Evaluate(WithResistanceLower(101.34m), P);

        Assert.True(result.Viable);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(1.10m, result.NetR);
    }

    [Fact]
    public void NetRExactlyAtTheMaximumIsAccepted()
    {
        var result = StructuralPlanner.Evaluate(WithResistanceLower(102.24m), P);

        Assert.True(result.Viable);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(2.00m, result.NetR);
        Assert.Equal(2.0, P.MaxNetR);
    }

    [Fact]
    public void NetRJustAboveTheMaximumIsRejectedAsExcessive()
    {
        var result = StructuralPlanner.Evaluate(WithResistanceLower(102.25m), P);

        Assert.False(result.Viable);
        Assert.Null(result.Plan);
        Assert.Equal(new[] { StructuralPlanner.ExcessiveRewardToRisk }, result.ReasonCodes.ToArray());
        Assert.Equal(2.01m, result.NetR);
    }

    [Fact]
    public void TheExcessiveRejectionNeverMovesTheTargetOrTheStop()
    {
        var result = StructuralPlanner.Evaluate(WithResistanceLower(102.25m), P);

        Assert.Equal(99.22m, result.Stop);
        Assert.Equal(102.23m, result.Target);
        Assert.Equal(99.25m, result.Anchor);
    }

    [Fact]
    public void DesignExampleAStillPassesUnderTheNewUpperBound()
    {
        var result = StructuralPlanner.Evaluate(D2.ExampleA(), P);

        Assert.True(result.Viable);
        Assert.Equal(1.4182m, Math.Round(result.NetR!.Value, 4));
        Assert.True(result.NetR <= (decimal)P.MaxNetR);
    }

    [Fact]
    public void MaxNetRIsAPolicyFieldSoChangingItChangesThePolicyHash()
    {
        Assert.NotEqual(P.PolicyHash, (P with { MaxNetR = 2.5 }).PolicyHash);
    }
}
