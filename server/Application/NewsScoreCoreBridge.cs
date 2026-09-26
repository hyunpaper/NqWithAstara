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
        return relevance.Targets.Select(target =>
        {
            var kind = target.Kind switch
            {
                "company" => ImpactTargetKind.Company,
                "sector" => ImpactTargetKind.Sector,
                "asset" => ImpactTargetKind.Asset,
                _ => ImpactTargetKind.Market
            };
            var published = record.CreatedAt == DateTimeOffset.MinValue ? (DateTimeOffset?)null : record.CreatedAt;
            var collected = record.CollectedAt ?? record.ClassifiedAt;
            var evidence = new EventEvidence(record.Id, record.Id, record.Source, record.Source, record.Url,
                record.Sentiment, relevance.EventKind,
                new ImpactTarget(target.Id, kind, TargetDirectness.Direct, target.Evidence), ImpactHorizon.Session,
                ImpactDirection.Unknown, relevance.Action, relevance.EvidenceSpan, FactVerification.Unknown,
                MarketExpectationStatus.Unknown, null, null, null, published, collected, record.ClassifiedAt,
                ImmutableArray<string>.Empty, new EvidenceQuality(null, null, null, null),
                ClassifierVersion, SourceVerified: false);
            return ScoreCoreAggregator.Aggregate(target.Id, kind, asOf, [evidence]);
        }).ToArray();
    }
}
