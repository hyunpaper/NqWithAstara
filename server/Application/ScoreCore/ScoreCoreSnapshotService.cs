using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

public sealed class ScoreCoreSnapshotService(
    AsOfEvidenceAssembler assembler,
    IScoreCoreSnapshotStore store,
    ScoreCorePolicy? policy = null)
{
    readonly ScoreCorePolicy _policy = policy ?? ScoreCorePolicy.ShadowV1;

    public async Task<ScoreCoreShadowSnapshot> CaptureAsync(
        ScoreEvidenceRequest request, CancellationToken ct = default)
    {
        var assembly = await assembler.AssembleAsync(request, ct);
        var score = ScoreCoreAggregator.Aggregate(request.TargetId, request.TargetKind, request.AsOf,
            assembly.Evidence, _policy);
        var status = assembly.Status == "unavailable" ? "unavailable" : score.Status;
        var captureId = CaptureId(score, assembly.Evidence, assembly.Coverage, assembly.Exclusions, status);
        var snapshot = new ScoreCoreShadowSnapshot(captureId, score, assembly.Coverage,
            assembly.Exclusions, status, assembly.Evidence.Length,
            assembly.Evidence.Select(x => x.EventGroupId).Distinct(StringComparer.Ordinal).Count());
        await store.AppendAsync(snapshot, ct);
        return snapshot;
    }

    static string CaptureId(ScoreCoreSnapshot score, ImmutableArray<EventEvidence> evidence,
        ImmutableArray<ScoreEvidenceCoverage> coverage,
        ImmutableArray<ScoreEvidenceExclusion> exclusions, string status)
    {
        var canonical = JsonSerializer.Serialize(new { score, evidence, coverage, exclusions, status });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..24];
    }
}

public sealed class ScoreCoreShadowCaptureOrchestrator(ScoreCoreSnapshotService snapshots)
{
    public async Task<ImmutableArray<ScoreCoreShadowSnapshot>> CaptureAsync(
        IEnumerable<(string TargetId, ImpactTargetKind TargetKind)> targets,
        DateTimeOffset asOf,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = ImmutableArray.CreateBuilder<ScoreCoreShadowSnapshot>();
        foreach (var target in targets.Distinct().OrderBy(x => x.TargetKind).ThenBy(x => x.TargetId, StringComparer.Ordinal))
            result.Add(await snapshots.CaptureAsync(new(target.TargetId, target.TargetKind, asOf), ct));
        return result.ToImmutable();
    }
}
