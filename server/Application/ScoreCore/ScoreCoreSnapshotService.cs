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
        => (await CaptureWithResultAsync(request, ct)).Snapshot;

    public async Task<(ScoreCoreShadowSnapshot Snapshot, ScoreSnapshotAppendResult Append)> CaptureWithResultAsync(
        ScoreEvidenceRequest request, CancellationToken ct = default)
    {
        var assembly = await assembler.AssembleAsync(request, ct);
        var score = ScoreCoreAggregator.Aggregate(request.TargetId, request.TargetKind, request.AsOf,
            assembly.Evidence, _policy);
        var status = assembly.Status == "unavailable" ? "unavailable" : score.Status;
        var captureId = CaptureId(score, assembly.Evidence, assembly.Coverage, assembly.Exclusions, status);
        var snapshot = new ScoreCoreShadowSnapshot(captureId, score, assembly.Coverage,
            assembly.Exclusions, status, assembly.Evidence.Length,
            assembly.Evidence.Select(x => x.EventGroupId).Distinct(StringComparer.Ordinal).Count(),
            ContentKey(score, assembly.Evidence, assembly.Coverage, assembly.Exclusions, status));
        return (snapshot, await store.AppendAsync(snapshot, ct));
    }

    static string ContentKey(ScoreCoreSnapshot score, ImmutableArray<EventEvidence> evidence,
        ImmutableArray<ScoreEvidenceCoverage> coverage,
        ImmutableArray<ScoreEvidenceExclusion> exclusions, string status)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            score = score with { SnapshotId = "", AsOf = default },
            evidence = evidence.Select(x => x.EvidenceId),
            coverage = coverage.Select(x => new { x.Source, x.Status, x.Reason }),
            exclusions = exclusions.Select(x => new { x.Source, x.EvidenceId, x.Reason }),
            status,
            score.PolicyVersion,
            score.ClassifierVersion,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
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

public sealed record ScoreCoreCaptureOutcome(string TargetId, ImpactTargetKind TargetKind,
    ScoreCoreShadowSnapshot? Snapshot, ScoreSnapshotAppendResult? Append, Exception? Error);

public sealed class ScoreCoreShadowCaptureOrchestrator(ScoreCoreSnapshotService snapshots)
{
    public async Task<ImmutableArray<ScoreCoreShadowSnapshot>> CaptureAsync(
        IEnumerable<(string TargetId, ImpactTargetKind TargetKind)> targets,
        DateTimeOffset asOf,
        CancellationToken ct = default)
        => (await CaptureTargetsAsync(targets, asOf, ct)).Where(x => x.Snapshot is not null)
            .Select(x => x.Snapshot!).ToImmutableArray();

    public async Task<ImmutableArray<ScoreCoreCaptureOutcome>> CaptureTargetsAsync(
        IEnumerable<(string TargetId, ImpactTargetKind TargetKind)> targets,
        DateTimeOffset asOf,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = ImmutableArray.CreateBuilder<ScoreCoreCaptureOutcome>();
        foreach (var target in targets.Distinct().OrderBy(x => x.TargetKind).ThenBy(x => x.TargetId, StringComparer.Ordinal))
        {
            try
            {
                var (snapshot, append) = await snapshots.CaptureWithResultAsync(
                    new(target.TargetId, target.TargetKind, asOf), ct);
                result.Add(new(target.TargetId, target.TargetKind, snapshot, append, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                result.Add(new(target.TargetId, target.TargetKind, null, null, exception));
            }
        }
        return result.ToImmutable();
    }
}
