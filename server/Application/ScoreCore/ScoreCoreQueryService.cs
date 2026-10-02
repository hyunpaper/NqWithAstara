using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

public enum ScoreCoreQueryStatus { Disabled, BadRequest, NotFound, Found }

public sealed record ScoreCoreQueryResult(ScoreCoreQueryStatus Status, ScoreCoreSnapshotDto? Snapshot = null);

public sealed record ScoreCoreDisabledDto(bool Enabled);

public sealed record ScoreCoreSnapshotDto(bool Enabled, string CaptureId, string TargetId, string TargetKind,
    DateTimeOffset AsOf, string Status, string CalibrationStatus, string SchemaVersion, string PolicyVersion,
    string ClassifierVersion, int EvidenceCount, int UniqueEventCount, int InputCount, int IncludedCount,
    int UnknownCount, IReadOnlyList<ScoreCoreHorizonDto> Horizons, IReadOnlyList<ScoreCoreCoverageDto> Coverage,
    IReadOnlyList<ScoreCoreExclusionSummaryDto> ExclusionSummary,
    IReadOnlyList<ScoreCoreExcludedEvidenceDto>? ExcludedEvidence);

public sealed record ScoreCoreHorizonDto(string Horizon, double? SignedEvidence, double PositiveMass,
    double NegativeMass, double GrossEvidence, double? DirectionalLean, double? Conflict, int UniqueEventCount,
    int EvidenceCount, int SourceCount, IReadOnlyList<ScoreCoreContributionDto>? TopContributions);

public sealed record ScoreCoreContributionDto(string EvidenceId, string EventGroupId, string Source,
    string EventKind, string Mechanism, string Direction, double PositiveUnit, double NegativeUnit,
    double Freshness, string Reason);

public sealed record ScoreCoreCoverageDto(string Source, string Status, int ReceivedCount, int AcceptedCount,
    string? Reason);

public sealed record ScoreCoreExclusionSummaryDto(string Stage, string Reason, int Count);

public sealed record ScoreCoreExcludedEvidenceDto(string Stage, string Source, string EvidenceId,
    string? EventGroupId, string Reason);

/// <summary>저장된 shadow snapshot을 API DTO로 변환한다. Domain 타입을 직접 노출하지 않는다(#309).</summary>
public sealed partial class ScoreCoreQueryService(ScoreCoreOptions options, IScoreCoreSnapshotStore store)
{
    public async Task<ScoreCoreQueryResult> LatestAsync(string kind, string targetId, CancellationToken ct)
    {
        if (!options.Enabled) return new(ScoreCoreQueryStatus.Disabled);
        if (NewsScoreEvidenceSource.TargetKind(kind ?? "") is not { } targetKind || !TargetPattern().IsMatch(targetId ?? ""))
            return new(ScoreCoreQueryStatus.BadRequest);
        var snapshot = await store.FindLatestAsync(targetKind, targetId!.Trim(), ct);
        return snapshot is null ? new(ScoreCoreQueryStatus.NotFound) : new(ScoreCoreQueryStatus.Found, Map(snapshot, false));
    }

    public async Task<ScoreCoreQueryResult> SnapshotAsync(string captureId, CancellationToken ct)
    {
        if (!options.Enabled) return new(ScoreCoreQueryStatus.Disabled);
        if (!CapturePattern().IsMatch(captureId ?? "")) return new(ScoreCoreQueryStatus.NotFound);
        var snapshot = await store.FindAsync(captureId!, ct);
        return snapshot is null ? new(ScoreCoreQueryStatus.NotFound) : new(ScoreCoreQueryStatus.Found, Map(snapshot, true));
    }

    static ScoreCoreSnapshotDto Map(ScoreCoreShadowSnapshot snapshot, bool detail)
    {
        var score = snapshot.Score;
        var excluded = snapshot.AssemblyExclusions
            .Select(x => new ScoreCoreExcludedEvidenceDto("assembly", x.Source, x.EvidenceId, null, x.Reason))
            .Concat(score.ExcludedEvidence.Select(x =>
                new ScoreCoreExcludedEvidenceDto("score", "", x.EvidenceId, x.EventGroupId, x.Reason)))
            .ToArray();
        var summary = excluded.GroupBy(x => (x.Stage, x.Reason))
            .Select(x => new ScoreCoreExclusionSummaryDto(x.Key.Stage, x.Key.Reason, x.Count()))
            .OrderBy(x => x.Stage, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray();
        return new(true, snapshot.CaptureId, score.TargetId, Name(score.TargetKind), score.AsOf, snapshot.Status,
            Name(score.CalibrationStatus), score.ScoreSchemaVersion, score.PolicyVersion, score.ClassifierVersion,
            snapshot.EvidenceCount, snapshot.UniqueEventCount, score.InputCount, score.IncludedCount, score.UnknownCount,
            score.Horizons.Select(x => new ScoreCoreHorizonDto(Name(x.Horizon), x.SignedEvidence, x.PositiveMass,
                x.NegativeMass, x.GrossEvidence, x.DirectionalLean, x.Conflict, x.UniqueEventCount, x.EvidenceCount,
                x.SourceCount, detail
                    ? x.TopContributions.Select(c => new ScoreCoreContributionDto(c.EvidenceId, c.EventGroupId,
                        c.Source, c.EventKind, c.Mechanism, c.Direction, c.PositiveUnit, c.NegativeUnit, c.Freshness,
                        c.Reason)).ToArray()
                    : null)).ToArray(),
            snapshot.Coverage.Select(x => new ScoreCoreCoverageDto(x.Source, x.Status, x.ReceivedCount,
                x.AcceptedCount, x.Reason)).ToArray(),
            summary, detail ? excluded : null);
    }

    static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    [GeneratedRegex("^[A-Za-z0-9._-]{1,32}$")]
    private static partial Regex TargetPattern();

    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex CapturePattern();
}
