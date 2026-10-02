namespace Astra.Server.Domain.News;

public static class NewsRelevanceDecisions
{
    public const string Include = "include";
    public const string Exclude = "exclude";
    public const string Review = "review";
}

public sealed record NewsEventTarget(string Id, string Kind, string Relation, string Evidence);

public sealed record NewsRelevanceAssessment(string PolicyVersion, string Decision, string EventKind,
    string Actor, string Action, IReadOnlyList<NewsEventTarget> Targets, string EvidenceSpan,
    string Reason, string Classifier = "deterministic", long LatencyMs = 0)
{
    public bool Included => Decision == NewsRelevanceDecisions.Include;
}
