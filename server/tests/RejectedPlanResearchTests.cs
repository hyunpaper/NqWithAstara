using Astra.Server.Application;
using Xunit;

public sealed class RejectedPlanResearchTests
{
    [Fact]
    public async Task FeatureOffAndPlanlessRejectionDoNotWrite()
    {
        var store = new MemoryObservationStore();
        var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(false));
        var candidate = new StructureCandidateDto("e", "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 1, null, null, null, null, null, [], ["NO_TARGET"], [], false, false);
        Assert.False(await service.RecordAsync(candidate, D3.Symbol, "p", D3.At(4)));
        Assert.Empty(store.Files);
    }

    [Fact]
    public async Task FutureCandidateIsIgnoredAndReadIsLimited()
    {
        var store = new MemoryObservationStore();
        var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true, 2));
        var candidate = new StructureCandidateDto("e", "REBOUND", "z", D3.At(10), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 1, null, null, null, null, null, [], [], [], false, false);
        Assert.False(await service.RecordAsync(candidate, D3.Symbol, "p", D3.At(4)));
        Assert.Empty(await service.ReadAsync(1));
    }
}
