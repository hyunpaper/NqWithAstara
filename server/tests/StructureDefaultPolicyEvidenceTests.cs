using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureDefaultPolicyEvidenceTests
{
    [Fact]
    public void DefaultPolicyIsNotReplacedByTheWideNetWiringFixture()
    {
        var policy = StructurePolicy.Default;
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), policy);

        Assert.Equal(StructurePolicy.Default.PolicyHash, policy.PolicyHash);
        Assert.NotEqual(D2.WideNetR.PolicyHash, policy.PolicyHash);
        Assert.True(evaluation.Viable);
        Assert.NotNull(evaluation.Plan);
        Assert.Equal(policy.PolicyHash, evaluation.Plan!.PolicyHash);
    }
}
