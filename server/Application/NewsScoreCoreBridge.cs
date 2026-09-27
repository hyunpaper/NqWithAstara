using System.Collections.Immutable;
using Astra.Server.Domain.News;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application;

public static class NewsScoreCoreBridge
{
    public const string ClassifierVersion = "news-relevance-shadow-v1";

    public static IReadOnlyList<ScoreCoreSnapshot> Snapshots(NewsRecord record, DateTimeOffset asOf)
    {
        if (record.Relevance is not { Included: true } relevance || relevance.Targets.Count == 0) return [];
        return relevance.Targets.SelectMany<NewsEventTarget, ScoreCoreSnapshot>(target =>
        {
            var kind = target.Kind.ToLowerInvariant() switch
            {
                "company" => ImpactTargetKind.Company,
                "sector" => ImpactTargetKind.Sector,
                "asset" => ImpactTargetKind.Asset,
                "market" => ImpactTargetKind.Market,
                _ => (ImpactTargetKind?)null
            };
            if (kind is null) return [];
            var directness = target.Relation.ToLowerInvariant() switch
            {
                "direct" => TargetDirectness.Direct,
                "indirect" => TargetDirectness.Indirect,
                _ => TargetDirectness.Unresolved
            };
            var published = record.CreatedAt == DateTimeOffset.MinValue ? (DateTimeOffset?)null : record.CreatedAt;
            var collected = record.CollectedAt ?? record.ClassifiedAt;
            var evidence = new EventEvidence(record.Id, record.Id, record.Source, record.Source, record.Url,
                record.Sentiment, relevance.EventKind,
                new ImpactTarget(target.Id, kind.Value, directness, target.Evidence), ImpactHorizon.Session,
                ImpactDirection.Unknown, relevance.Action, relevance.EvidenceSpan, FactVerification.Unknown,
                MarketExpectationStatus.Unknown, null, null, null, published, collected, record.ClassifiedAt,
                ImmutableArray<string>.Empty, new EvidenceQuality(null, null, null, null),
                ClassifierVersion, SourceVerified: false);
            return [ScoreCoreAggregator.Aggregate(target.Id, kind.Value, asOf, [evidence])];
        }).ToArray();
    }
}
