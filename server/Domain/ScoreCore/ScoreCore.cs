using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Server.Domain.ScoreCore;

public enum ImpactDirection { Favorable, Unfavorable, Mixed, Neutral, Unknown }
public enum ImpactTargetKind { Market, Asset, Sector, Company }
public enum ImpactHorizon { Intraday, Session, MultiDay }
public enum FactVerification { Verified, Corroborated, Unverified, Unknown }
public enum MarketExpectationStatus { Above, InLine, Below, Unverified, Unknown }
public enum TargetDirectness { Direct, Indirect, Unresolved }
public enum ScoreCalibrationStatus { Uncalibrated, InsufficientData, Calibrated, Stale }

public sealed record ImpactTarget(string Id, ImpactTargetKind Kind, TargetDirectness Directness,
    string? RelationEvidence = null);

public sealed record EvidenceQuality(double? SourceReliability, double? ClassifierConfidence,
    double? TargetLinkConfidence, double? ImpactDirectionConfidence);

public sealed record EventEvidence(
    string EvidenceId,
    string EventGroupId,
    string SourceId,
    string Source,
    string? Url,
    string Sentiment,
    string EventKind,
    ImpactTarget Target,
    ImpactHorizon Horizon,
    ImpactDirection ImpactDirection,
    string Mechanism,
    string EvidenceSpan,
    FactVerification FactVerification,
    MarketExpectationStatus MarketExpectationStatus,
    double? Severity,
    double? PositiveSeverity,
    double? NegativeSeverity,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CollectedAt,
    DateTimeOffset ObservedAt,
    ImmutableArray<string> OpposingChannels,
    EvidenceQuality Quality,
    string ClassifierVersion,
    bool SourceVerified = true);

public sealed record ScoreCorePolicy(string Version)
{
    public static readonly ScoreCorePolicy ShadowV1 = new("score-core-shadow-v1");

    public TimeSpan HalfLife(string eventKind, ImpactHorizon horizon) => (eventKind, horizon) switch
    {
        ("geopolitical", ImpactHorizon.MultiDay) => TimeSpan.FromDays(3),
        ("financing", ImpactHorizon.MultiDay) => TimeSpan.FromDays(5),
        ("company_contract", ImpactHorizon.MultiDay) => TimeSpan.FromDays(5),
        ("macro_release", ImpactHorizon.Intraday) => TimeSpan.FromHours(2),
        (_, ImpactHorizon.Intraday) => TimeSpan.FromHours(1),
        (_, ImpactHorizon.Session) => TimeSpan.FromHours(6),
        _ => TimeSpan.FromDays(2),
    };

    public TimeSpan MaximumAge(string eventKind, ImpactHorizon horizon) => HalfLife(eventKind, horizon) * 8;
}

public sealed record ScoredEvidence(EventEvidence Evidence, double Freshness, double PositiveUnit,
    double NegativeUnit, bool QualityEligible, ImmutableArray<string> ExclusionReasons)
{
    public bool Included => ExclusionReasons.Length == 0;
}

public sealed record ScoreCoreContribution(string EvidenceId, string EventGroupId, string Source,
    string EventKind, string Mechanism, string Direction, double PositiveUnit, double NegativeUnit,
    double Freshness, string Reason);

public sealed record ExcludedScoreEvidence(string EvidenceId, string EventGroupId, string Reason);

public sealed record ScoreCoreHorizonSnapshot(ImpactHorizon Horizon, double? SignedEvidence,
    double PositiveMass, double NegativeMass, double GrossEvidence, double? DirectionalLean,
    double? Conflict, int UniqueEventCount, int EvidenceCount, int SourceCount,
    ImmutableArray<ScoreCoreContribution> TopContributions);

public sealed record ScoreCoreSnapshotReference(string SnapshotId, string SchemaVersion,
    DateTimeOffset AsOf, ScoreCalibrationStatus CalibrationStatus);

public sealed record ScoreCoreSnapshot(string SnapshotId, string TargetId, ImpactTargetKind TargetKind,
    DateTimeOffset AsOf, string ScoreSchemaVersion, string PolicyVersion, string ClassifierVersion,
    ScoreCalibrationStatus CalibrationStatus, ImmutableArray<ScoreCoreHorizonSnapshot> Horizons,
    ImmutableArray<ExcludedScoreEvidence> ExcludedEvidence, int InputCount, int IncludedCount,
    int UnknownCount, string Status)
{
    public ScoreCoreSnapshotReference Reference() => new(SnapshotId, ScoreSchemaVersion, AsOf, CalibrationStatus);
}

public static class EventImpactScorer
{
    public static ScoredEvidence Score(EventEvidence evidence, DateTimeOffset evaluationAsOf,
        ScoreCorePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        policy ??= ScoreCorePolicy.ShadowV1;
        var reasons = ImmutableArray.CreateBuilder<string>();
        ValidateIdentity(evidence, reasons);
        ValidateQuality(evidence.Quality, reasons);
        if (string.IsNullOrWhiteSpace(evidence.Target.Id) || evidence.Target.Directness == TargetDirectness.Unresolved)
            reasons.Add("unresolved_target");
        if (evidence.Target.Directness == TargetDirectness.Indirect &&
            string.IsNullOrWhiteSpace(evidence.Target.RelationEvidence))
            reasons.Add("missing_relation_evidence");
        if (string.IsNullOrWhiteSpace(evidence.Mechanism)) reasons.Add("missing_mechanism");
        if (string.IsNullOrWhiteSpace(evidence.EvidenceSpan) && string.IsNullOrWhiteSpace(evidence.Url))
            reasons.Add("missing_evidence");
        if (evidence.PublishedAt is null) reasons.Add("timestamp_unknown");
        else if (evidence.PublishedAt > evidence.CollectedAt || evidence.CollectedAt > evidence.ObservedAt ||
                 evidence.ObservedAt > evaluationAsOf)
            reasons.Add("future_or_inconsistent_timestamp");
        if (evidence.ImpactDirection == ImpactDirection.Unknown) reasons.Add("impact_direction_unknown");
        if (!evidence.SourceVerified) reasons.Add("source_unverified");

        var age = evidence.PublishedAt is { } published ? evaluationAsOf - published : TimeSpan.Zero;
        var halfLife = policy.HalfLife(evidence.EventKind, evidence.Horizon);
        if (age > policy.MaximumAge(evidence.EventKind, evidence.Horizon)) reasons.Add("expired_evidence");
        var freshness = reasons.Contains("future_or_inconsistent_timestamp") || evidence.PublishedAt is null
            ? 0d : Math.Exp(-Math.Log(2d) * Math.Max(0d, age.TotalSeconds) / halfLife.TotalSeconds);

        var positive = 0d;
        var negative = 0d;
        switch (evidence.ImpactDirection)
        {
            case ImpactDirection.Favorable:
                positive = Unit(evidence.Severity, freshness, reasons);
                break;
            case ImpactDirection.Unfavorable:
                negative = Unit(evidence.Severity, freshness, reasons);
                break;
            case ImpactDirection.Mixed:
                if (evidence.OpposingChannels.Length == 0) reasons.Add("missing_opposing_channel");
                positive = Unit(evidence.PositiveSeverity, freshness, reasons, "positive_materiality_unknown");
                negative = Unit(evidence.NegativeSeverity, freshness, reasons, "negative_materiality_unknown");
                break;
            case ImpactDirection.Neutral:
                if (evidence.FactVerification is not (FactVerification.Verified or FactVerification.Corroborated))
                    reasons.Add("neutral_fact_unverified");
                if (evidence.MarketExpectationStatus != MarketExpectationStatus.InLine)
                    reasons.Add("neutral_expectation_not_inline");
                if (evidence.Severity is null) reasons.Add("neutral_materiality_unknown");
                else if (!double.IsFinite(evidence.Severity.Value) || evidence.Severity.Value is < 0 or > .1)
                    reasons.Add("neutral_materiality_not_low");
                break;
        }

        var exclusions = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (exclusions.Length > 0) { positive = 0; negative = 0; }
        var qualityEligible = exclusions.Length == 0 && evidence.SourceVerified &&
                             evidence.FactVerification is FactVerification.Verified or FactVerification.Corroborated &&
                             evidence.MarketExpectationStatus is not (MarketExpectationStatus.Unverified or MarketExpectationStatus.Unknown);
        return new ScoredEvidence(evidence, Round(freshness), Round(positive), Round(negative), qualityEligible, exclusions);
    }

    static double Unit(double? severity, double freshness, ImmutableArray<string>.Builder reasons,
        string missing = "unknown_materiality")
    {
        if (severity is null) { reasons.Add(missing); return 0; }
        if (!double.IsFinite(severity.Value) || severity.Value is < 0 or > 1)
        {
            reasons.Add("invalid_materiality");
            return 0;
        }
        return severity.Value * freshness;
    }

    static void ValidateQuality(EvidenceQuality quality, ImmutableArray<string>.Builder reasons)
    {
        foreach (var (name, value) in new[]
        {
            ("source_reliability", quality.SourceReliability),
            ("classifier_confidence", quality.ClassifierConfidence),
            ("target_link_confidence", quality.TargetLinkConfidence),
            ("impact_direction_confidence", quality.ImpactDirectionConfidence),
        })
            if (value is null) reasons.Add($"missing_{name}");
            else if (!double.IsFinite(value.Value) || value.Value is < 0 or > 1) reasons.Add($"invalid_{name}");
    }

    static void ValidateIdentity(EventEvidence evidence, ImmutableArray<string>.Builder reasons)
    {
        foreach (var (name, value) in new[]
        {
            ("evidence_id", evidence.EvidenceId), ("event_group_id", evidence.EventGroupId),
            ("source_id", evidence.SourceId), ("source", evidence.Source),
            ("classifier_version", evidence.ClassifierVersion),
        })
            if (string.IsNullOrWhiteSpace(value)) reasons.Add($"missing_{name}");
    }

    static double Round(double value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);
}

public static class ScoreCoreAggregator
{
    public const string SchemaVersion = "score-core-v1";

    public static ScoreCoreSnapshot Aggregate(string targetId, ImpactTargetKind targetKind,
        DateTimeOffset evaluationAsOf, IEnumerable<EventEvidence> evidence, ScoreCorePolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(evidence);
        policy ??= ScoreCorePolicy.ShadowV1;
        var inputs = evidence.Where(x => x.Target.Kind == targetKind &&
                                         string.Equals(x.Target.Id, targetId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.EventGroupId, StringComparer.Ordinal).ThenBy(x => x.EvidenceId, StringComparer.Ordinal).ToArray();
        var conflictingIds = inputs.GroupBy(x => x.EvidenceId, StringComparer.Ordinal)
            .Where(group => group.Select(IdentityFingerprint).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var scored = inputs.Select(x =>
        {
            var row = EventImpactScorer.Score(x, evaluationAsOf, policy);
            return conflictingIds.Contains(x.EvidenceId)
                ? row with { PositiveUnit = 0, NegativeUnit = 0, QualityEligible = false,
                    ExclusionReasons = row.ExclusionReasons.Add("duplicate_identity_conflict")
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray() }
                : row;
        }).ToArray();
        var excluded = scored.Where(x => !x.Included)
            .SelectMany(x => x.ExclusionReasons.Select(reason => new ExcludedScoreEvidence(
                x.Evidence.EvidenceId, x.Evidence.EventGroupId, reason)))
            .OrderBy(x => x.EventGroupId, StringComparer.Ordinal).ThenBy(x => x.EvidenceId, StringComparer.Ordinal)
            .ThenBy(x => x.Reason, StringComparer.Ordinal).ToImmutableArray();
        var horizons = Enum.GetValues<ImpactHorizon>().Select(horizon => Horizon(horizon,
            scored.Where(x => x.Included && x.Evidence.Horizon == horizon))).ToImmutableArray();
        var classifier = string.Join(',', inputs.Select(x => x.ClassifierVersion).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var canonical = string.Join('|', targetId.ToUpperInvariant(), targetKind, evaluationAsOf.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            policy.Version, string.Join(',', scored.Select(x => x.Evidence.EvidenceId + ':' + x.PositiveUnit.ToString("R", CultureInfo.InvariantCulture) + ':' + x.NegativeUnit.ToString("R", CultureInfo.InvariantCulture))));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
        var included = scored.Count(x => x.Included);
        return new ScoreCoreSnapshot(id, targetId.ToUpperInvariant(), targetKind, evaluationAsOf,
            SchemaVersion, policy.Version, classifier, ScoreCalibrationStatus.Uncalibrated, horizons, excluded,
            inputs.Length, included, inputs.Count(x => x.ImpactDirection == ImpactDirection.Unknown),
            included == 0 ? "insufficient_data" : "shadow");
    }

    static ScoreCoreHorizonSnapshot Horizon(ImpactHorizon horizon, IEnumerable<ScoredEvidence> source)
    {
        var rows = source.ToArray();
        var channels = rows.GroupBy(x => string.Join('|', x.Evidence.EventGroupId,
                x.Evidence.Target.Id.ToUpperInvariant(), x.Evidence.Horizon, x.Evidence.Mechanism), StringComparer.Ordinal)
            .Select(group => new
            {
                Representative = group.OrderByDescending(x => x.PositiveUnit + x.NegativeUnit)
                    .ThenBy(x => x.Evidence.EvidenceId, StringComparer.Ordinal).First(),
                Positive = group.Max(x => x.PositiveUnit),
                Negative = group.Max(x => x.NegativeUnit),
                Sources = group.Select(x => x.Evidence.SourceId).Distinct(StringComparer.Ordinal).Count(),
                Evidence = group.Count(),
            }).ToArray();
        var positive = channels.Sum(x => x.Positive);
        var negative = channels.Sum(x => x.Negative);
        var gross = positive + negative;
        var signed = positive - negative;
        var lean = gross == 0 ? (double?)null : (positive - negative) / gross;
        var conflict = gross == 0 ? (double?)null : 2 * Math.Min(positive, negative) / gross;
        var contributions = channels.OrderByDescending(x => x.Positive + x.Negative)
            .ThenBy(x => x.Representative.Evidence.EventGroupId, StringComparer.Ordinal).Take(10)
            .Select(x => new ScoreCoreContribution(x.Representative.Evidence.EvidenceId,
                x.Representative.Evidence.EventGroupId, x.Representative.Evidence.Source,
                x.Representative.Evidence.EventKind, x.Representative.Evidence.Mechanism,
                x.Positive > 0 && x.Negative > 0 ? "mixed" : x.Positive > 0 ? "favorable" : x.Negative > 0 ? "unfavorable" : "neutral",
                Round(x.Positive), Round(x.Negative), x.Representative.Freshness,
                x.Evidence > 1 ? $"중복 {x.Evidence}건을 사건 채널 1개로 제한" : "고유 사건 채널")).ToImmutableArray();
        return new ScoreCoreHorizonSnapshot(horizon, channels.Length == 0 ? null : Round(signed), Round(positive),
            Round(negative), Round(gross), lean is null ? null : Round(lean.Value),
            conflict is null ? null : Round(conflict.Value), channels.Select(x => x.Representative.Evidence.EventGroupId)
                .Distinct(StringComparer.Ordinal).Count(), rows.Length, channels.Sum(x => x.Sources), contributions);
    }

    static double Round(double value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

    static string IdentityFingerprint(EventEvidence value) => string.Join('|', value.EventGroupId,
        value.SourceId, value.Source, value.Url, value.Sentiment, value.EventKind, value.ClassifierVersion,
        value.SourceVerified, value.Target.Id.ToUpperInvariant(), value.Target.Kind,
        value.Target.Directness, value.Target.RelationEvidence, value.Horizon, value.ImpactDirection, value.Mechanism,
        value.EvidenceSpan, value.FactVerification, value.MarketExpectationStatus, Format(value.Severity),
        Format(value.PositiveSeverity), Format(value.NegativeSeverity),
        value.PublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        value.CollectedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        value.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        string.Join(',', value.OpposingChannels.Order(StringComparer.Ordinal)), Format(value.Quality.SourceReliability),
        Format(value.Quality.ClassifierConfidence), Format(value.Quality.TargetLinkConfidence),
        Format(value.Quality.ImpactDirectionConfidence));

    static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
}
