using Astra.Server.Application;
using Xunit;

public sealed class RejectedPlanResearchTests
{
    sealed class ThrowingStore(bool read) : ILocalStore
    {
        public Task<T> Read<T>(string file, T fallback) => read ? throw new IOException("read") : Task.FromResult(fallback);
        public Task Write<T>(string file, T data) => read ? Task.CompletedTask : throw new IOException("write");
        public Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change) => throw new IOException("update");
    }

    static StructurePlanDto Plan() => new("p", "REBOUND", 99, 98, 98.5m, 101, "i", 98, 99,
        "t", 100, 101, .1m, "test", .05m, 1.5m, 1m, 1.5m, null, .1m, .01m, null, false,
        "cost", "fill", D3.At(1), D3.At(60), "v5", "hash", [], "test");

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
    public async Task EnabledPlanlessRejectionStillDoesNotWrite()
    {
        var store = new MemoryObservationStore();
        var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true));
        var c = new StructureCandidateDto("no-plan", "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 99, null, null, null, null, null, [], ["NO_TARGET"], [], false, false);
        Assert.False(await service.RecordAsync(c, D3.Symbol, "hash", D3.At(10)));
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

    [Fact]
    public async Task EnabledPlanIsIdempotentAcrossAsOfAndParallelCalls()
    {
        var store = new MemoryObservationStore();
        var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true, 1));
        var c = new StructureCandidateDto("e", "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 99, null, 98.5m, 101, 1.5m, Plan(), [], ["COST"], [], false, false);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => service.RecordAsync(c, D3.Symbol, "hash", D3.At(10 + i))));
        Assert.Single(results, x => x);
        Assert.Single(await service.ReadAsync());
    }

    [Fact]
    public async Task EnabledRejectedPlanUsesOnlyResearchFileAndLeavesMainTradeFilesUntouched()
    {
        var store = new MemoryObservationStore();
        var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true));
        var c = new StructureCandidateDto("integration-event", "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 99, null, 98.5m, 101, 1.5m, Plan(), [], ["COST"], [], false, false);
        Assert.True(await service.RecordAsync(c, D3.Symbol, "hash", D3.At(10)));
        Assert.Contains(RejectedPlanResearchService.FileName, store.Files.Keys);
        Assert.DoesNotContain("simtrades.json", store.Files.Keys);
        Assert.DoesNotContain("positions.json", store.Files.Keys);
        var row = Assert.Single(await service.ReadAsync());
        Assert.Null(row.HorizonAt);
        Assert.Null(row.Outcome);
    }

    [Fact]
    public async Task TwoEventsAreOrderedAndEvictOldestAtLimit()
    {
        var store = new MemoryObservationStore();
        var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true, 1));
        foreach (var id in new[] { "old", "new" })
        {
            var c = new StructureCandidateDto(id, "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 99, null, 98.5m, 101, 1.5m, Plan(), [], [], [], false, false);
            Assert.True(await service.RecordAsync(c, D3.Symbol, "hash", id == "old" ? D3.At(10) : D3.At(20)));
        }
        var row = Assert.Single(await service.ReadAsync());
        Assert.Equal("new", row.EventId);
    }

    [Fact]
    public async Task AnyFuturePlanCutoffIsRejected()
    {
        foreach (var future in new[] { "trigger", "confirmed", "structure" })
        {
            var store = new MemoryObservationStore();
            var service = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true));
            var trigger = future == "trigger" ? D3.At(20) : D3.At(1);
            var confirmed = future == "confirmed" ? D3.At(20) : D3.At(2);
            var cutoff = future == "structure" ? D3.At(20) : D3.At(3);
            var c = new StructureCandidateDto(future, "REBOUND", "z", trigger, confirmed, cutoff, D3.At(30), "REJECTED", null, 99, null, 98.5m, 101, 1.5m, Plan(), [], [], [], false, false);
            Assert.False(await service.RecordAsync(c, D3.Symbol, "hash", D3.At(10)));
        }
    }

    [Fact]
    public async Task ReadAndWriteFailuresAreIsolated()
    {
        var c = new StructureCandidateDto("failure", "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 99, null, 98.5m, 101, 1.5m, Plan(), [], [], [], false, false);
        Assert.False(await new RejectedPlanResearchService(new ThrowingStore(true), new RejectedPlanResearchOptions(true)).RecordAsync(c, D3.Symbol, "h", D3.At(10)));
        Assert.False(await new RejectedPlanResearchService(new ThrowingStore(false), new RejectedPlanResearchOptions(true)).RecordAsync(c, D3.Symbol, "h", D3.At(10)));
    }

    [Fact]
    public async Task DisabledReadIsEmptyAndAsOfFilterExcludesFutureRows()
    {
        var store = new MemoryObservationStore();
        var disabled = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(false));
        Assert.Empty(await disabled.ReadAsync(1, D3.At(10)));
        var enabled = new RejectedPlanResearchService(store, new RejectedPlanResearchOptions(true));
        var c = new StructureCandidateDto("api-filter", "REBOUND", "z", D3.At(1), D3.At(2), D3.At(3), D3.At(4), "REJECTED", null, 99, null, 98.5m, 101, 1.5m, Plan(), [], [], [], false, false);
        Assert.True(await enabled.RecordAsync(c, D3.Symbol, "h", D3.At(20)));
        Assert.Empty(await enabled.ReadAsync(10, D3.At(10)));
        Assert.Single(await enabled.ReadAsync(1, D3.At(20)));
    }
}
