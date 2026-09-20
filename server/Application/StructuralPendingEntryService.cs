using Astra.Server.Domain;

namespace Astra.Server.Application;

/// <summary>v5 확인 대기를 재시작 후에도 보존하는 저장 계약. simtrades와 분리해 미체결 계획을 중복 진입으로 만들지 않는다.</summary>
public sealed record StoredPendingStructuralEntry(PendingEntry Pending, FrozenStructureContext Context,
    DateTimeOffset QueuedAt);

public sealed class StructuralPendingEntryService(ILocalStore store)
{
    const string File = "structure-pending-entries.json";

    public async Task<StoredPendingStructuralEntry?> GetAsync(string symbol)
    {
        var rows = await store.Read(File, new List<StoredPendingStructuralEntry>());
        return rows.LastOrDefault(x => string.Equals(x.Pending.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }

    public Task QueueAsync(StoredPendingStructuralEntry entry) => store.Update(File,
        new List<StoredPendingStructuralEntry>(), rows =>
    {
        rows.RemoveAll(x => string.Equals(x.Pending.Symbol, entry.Pending.Symbol, StringComparison.OrdinalIgnoreCase));
        rows.Add(entry);
        return (rows, 0);
    });

    public Task RemoveAsync(string symbol) => store.Update(File, new List<StoredPendingStructuralEntry>(), rows =>
    {
        rows.RemoveAll(x => string.Equals(x.Pending.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        return (rows, 0);
    });
}
