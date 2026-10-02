using System.Collections.Immutable;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

public enum ScoreCapturePurpose { LiveShadow, HistoricalReplay }

public sealed record ScoreEvidenceRequest(
    string TargetId,
    ImpactTargetKind TargetKind,
    DateTimeOffset AsOf,
    ScoreCapturePurpose Purpose = ScoreCapturePurpose.LiveShadow);

public sealed record ScoreEvidenceBatch(
    string Source,
    DateTimeOffset AsOf,
    bool SupportsHistoricalPointInTime,
    ImmutableArray<EventEvidence> Evidence,
    string Status,
    string? Reason = null);

public sealed record ScoreEvidenceCoverage(
    string Source,
    string Status,
    int ReceivedCount,
    int AcceptedCount,
    string? Reason);

public sealed record ScoreEvidenceExclusion(string Source, string EvidenceId, string Reason);

public sealed record ScoreEvidenceAssembly(
    ScoreEvidenceRequest Request,
    ImmutableArray<EventEvidence> Evidence,
    ImmutableArray<ScoreEvidenceCoverage> Coverage,
    ImmutableArray<ScoreEvidenceExclusion> Exclusions,
    string Status);

public sealed record ScoreCoreShadowSnapshot(
    string CaptureId,
    ScoreCoreSnapshot Score,
    ImmutableArray<ScoreEvidenceCoverage> Coverage,
    ImmutableArray<ScoreEvidenceExclusion> AssemblyExclusions,
    string Status,
    int EvidenceCount = 0,
    int UniqueEventCount = 0);

public enum ScoreSnapshotAppendResult { Appended, AlreadyExists }

public interface IScoreEvidenceSource
{
    string Name { get; }
    Task<ScoreEvidenceBatch> ReadAsync(ScoreEvidenceRequest request, CancellationToken ct);
}

public interface IScoreCoreSnapshotStore
{
    Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot, CancellationToken ct);
    Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct);
    Task<ScoreCoreShadowSnapshot?> FindLatestAsync(ImpactTargetKind kind, string targetId, CancellationToken ct);
    Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct);
}
