using Astra.Server.Domain;

namespace Astra.Server.Application;

/// <summary>v5 확인 대기를 재시작 후에도 보존하는 저장 계약. simtrades와 분리해 미체결 계획을 중복 진입으로 만들지 않는다.</summary>
public sealed record StoredPendingStructuralEntry(PendingEntry Pending, FrozenStructureContext Context,
    DateTimeOffset QueuedAt, DateTimeOffset? ProcessingUntil = null);

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

    /// <summary>한 poll만 확인을 소유하도록 원자적으로 꺼낸다. 재시작·중복 poll은 null을 받는다.</summary>
    public Task<StoredPendingStructuralEntry?> ClaimAsync(string symbol, DateTimeOffset now) => store.Update(
        File, new List<StoredPendingStructuralEntry>(), rows =>
    {
        var index = rows.FindLastIndex(x => string.Equals(x.Pending.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (rows, (StoredPendingStructuralEntry?)null);
        var item = rows[index];
        if (item.ProcessingUntil is { } processing && processing > now)
            return (rows, (StoredPendingStructuralEntry?)null);
        if (now > item.Pending.ExpiresAt)
        {
            rows.RemoveAt(index);
            return (rows, (StoredPendingStructuralEntry?)null);
        }
        var claimed = item with { ProcessingUntil = now.AddMinutes(1) };
        rows[index] = claimed;
        return (rows, claimed);
    });
}
