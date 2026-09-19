namespace Astra.Server.Application;

public sealed record RejectedPlanResearchRow(string EventId, string Symbol, string PolicyHash,
    DateTimeOffset AsOf, StructurePlanDto Plan, DateTimeOffset? HorizonAt, string? Outcome);

/// <summary>REJECTED 후보 중 실제 plan이 있는 항목만 별도 연구 파일에 보존한다.</summary>
public sealed record RejectedPlanResearchOptions(bool Enabled = false, int Limit = 500);
public sealed class RejectedPlanResearchService(ILocalStore store, RejectedPlanResearchOptions? options = null)
{
    readonly RejectedPlanResearchOptions options = options ?? new();
    readonly SemaphoreSlim gate = new(1, 1);
    public const string FileName = "research/rejected-plans.json";

    public async Task<IReadOnlyList<RejectedPlanResearchRow>> ReadAsync(int limit = 100, DateTimeOffset? asOf = null, CancellationToken ct = default)
    {
        var rows = await store.Read(FileName, new List<RejectedPlanResearchRow>());
        return rows.Where(x => asOf is null || x.AsOf <= asOf).OrderByDescending(x => x.AsOf).Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public async Task<bool> RecordAsync(StructureCandidateDto candidate, string symbol, string policyHash,
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        if (!options.Enabled || candidate.State != "REJECTED" || candidate.Plan is null || candidate.TriggerBarStart > asOf || candidate.TriggerConfirmedAt > asOf || candidate.StructureCutoff > asOf)
            return false;
        await gate.WaitAsync(ct);
        try {
            var rows = await store.Read(FileName, new List<RejectedPlanResearchRow>());
            if (rows.Any(x => x.EventId == candidate.EventId && x.Symbol == symbol && x.PolicyHash == policyHash)) return false;
            rows.Add(new(candidate.EventId, symbol, policyHash, asOf, candidate.Plan, null, null));
            if (rows.Count > options.Limit) rows = rows.OrderByDescending(x => x.AsOf).Take(options.Limit).ToList();
            await store.Write(FileName, rows); return true;
        } catch { return false; } finally { gate.Release(); }
    }
}
