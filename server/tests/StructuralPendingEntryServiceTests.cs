using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Xunit;

public sealed class StructuralPendingEntryServiceTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-20T13:30:00Z");

    static StoredPendingStructuralEntry Entry(string id = "event-1") => new(
        new PendingEntry(id, "SOXL", TradeSide.Long, Start, Start.AddMinutes(1), Start.AddMinutes(5), 90, 110, 100, "plan", "policy"),
        null!, Start);

    [Fact]
    public async Task QueueSurvivesNewServiceInstanceAndClaimIsSingleUse()
    {
        var store = new MemoryStore();
        await new StructuralPendingEntryService(store).QueueAsync(Entry());
        var restarted = new StructuralPendingEntryService(store);
        Assert.NotNull(await restarted.GetAsync("SOXL"));
        Assert.NotNull(await restarted.ClaimAsync("SOXL", Start.AddMinutes(1)));
        Assert.Null(await restarted.ClaimAsync("SOXL", Start.AddMinutes(1)));
    }

    [Fact]
    public async Task ExpiredClaimRemovesPendingWithoutReturningIt()
    {
        var store = new MemoryStore();
        var service = new StructuralPendingEntryService(store);
        await service.QueueAsync(Entry());
        Assert.Null(await service.ClaimAsync("SOXL", Start.AddMinutes(6)));
        Assert.Null(await service.GetAsync("SOXL"));
    }

    sealed class MemoryStore : ILocalStore
    {
        readonly Dictionary<string, string> data = [];
        public Task<T> Read<T>(string file, T fallback) => Task.FromResult(data.TryGetValue(file, out var json)
            ? JsonSerializer.Deserialize<T>(json)! : fallback);
        public Task Write<T>(string file, T value) { data[file] = JsonSerializer.Serialize(value); return Task.CompletedTask; }
        public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
        {
            var current = await Read(file, fallback); var changed = change(current);
            await Write(file, changed.Data); return changed.Result;
        }
    }
}
