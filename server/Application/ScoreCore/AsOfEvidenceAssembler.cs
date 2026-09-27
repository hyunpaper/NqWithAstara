using System.Collections.Immutable;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

public sealed class AsOfEvidenceAssembler(IEnumerable<IScoreEvidenceSource> sources)
{
    readonly IScoreEvidenceSource[] _sources = sources.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();

    public async Task<ScoreEvidenceAssembly> AssembleAsync(ScoreEvidenceRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetId);
        if (request.Purpose == ScoreCapturePurpose.HistoricalReplay)
            return Unavailable(request, "historical_point_in_time_unavailable");

        var accepted = ImmutableArray.CreateBuilder<EventEvidence>();
        var coverage = ImmutableArray.CreateBuilder<ScoreEvidenceCoverage>();
        var exclusions = ImmutableArray.CreateBuilder<ScoreEvidenceExclusion>();

        foreach (var source in _sources)
        {
            var batch = await source.ReadAsync(request, ct);
            var sourceAccepted = 0;
            if (!string.Equals(batch.Source, source.Name, StringComparison.Ordinal))
            {
                coverage.Add(new(source.Name, "invalid", batch.Evidence.Length, 0, "source_identity_mismatch"));
                continue;
            }
            if (batch.AsOf > request.AsOf)
            {
                coverage.Add(new(source.Name, "excluded", batch.Evidence.Length, 0, "future_source_snapshot"));
                continue;
            }

            foreach (var evidence in batch.Evidence.OrderBy(x => x.EventGroupId, StringComparer.Ordinal)
                         .ThenBy(x => x.EvidenceId, StringComparer.Ordinal))
            {
                var reason = ExclusionReason(evidence, request);
                if (reason is not null)
                {
                    exclusions.Add(new(source.Name, evidence.EvidenceId, reason));
                    continue;
                }
                accepted.Add(evidence);
                sourceAccepted++;
            }
            coverage.Add(new(source.Name, batch.Status, batch.Evidence.Length, sourceAccepted, batch.Reason));
        }

        var orderedEvidence = accepted.OrderBy(x => x.EventGroupId, StringComparer.Ordinal)
            .ThenBy(x => x.EvidenceId, StringComparer.Ordinal).ToImmutableArray();
        var orderedCoverage = coverage.OrderBy(x => x.Source, StringComparer.Ordinal).ToImmutableArray();
        var orderedExclusions = exclusions.OrderBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.EvidenceId, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal)
            .ToImmutableArray();
        var status = orderedEvidence.Length > 0 ? "shadow" : "insufficient_data";
        return new(request, orderedEvidence, orderedCoverage, orderedExclusions, status);
    }

    static string? ExclusionReason(EventEvidence evidence, ScoreEvidenceRequest request)
    {
        if (evidence.Target.Kind != request.TargetKind ||
            !string.Equals(evidence.Target.Id, request.TargetId, StringComparison.OrdinalIgnoreCase))
            return "target_mismatch";
        if (evidence.PublishedAt is null) return "published_at_missing";
        if (evidence.PublishedAt > request.AsOf || evidence.CollectedAt > request.AsOf ||
            evidence.ObservedAt > request.AsOf)
            return "future_evidence";
        if (evidence.ImpactDirection == ImpactDirection.Unknown) return "impact_direction_unknown";
        return null;
    }

    static ScoreEvidenceAssembly Unavailable(ScoreEvidenceRequest request, string reason) => new(
        request, [], [], [new("capture", "", reason)], "unavailable");
}
