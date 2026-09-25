namespace Astra.Server.Application;

public sealed record EntryObservationRow(string Symbol, string Status, DateTimeOffset EvaluatedAt,
    DateTimeOffset? AnalysisAsOf, DateTimeOffset? QuoteAt, double? DataDelaySeconds, int CandidateCount,
    int ApprovedCount, int RejectedCount, string FinalDisposition, string? FirstGateReason,
    string[] DuplicateReasons, string[] RejectionReasons);

public sealed record EntryObservabilityReport(DateTimeOffset GeneratedAt, int CandidateCount, int ApprovedCount,
    int RejectedCount, EntryObservationRow[] Symbols);

public sealed class EntryObservabilityQueryService(StructureAnalysisService structure, TimeProvider clock)
{
    static readonly string[] DuplicateCodes =
    [
        "DUPLICATE_TRIGGER_GUARD", "TRIGGER_EPISODE_CONSUMED", "NEW_TRIGGER_SUPPRESSED",
        "BREAKOUT_ZONE_COOLDOWN", "BAR_ALREADY_EVALUATED", "TOMBSTONED_EVENT_NOT_REVIVED",
        "V5_PENDING_ALREADY_CLAIMED"
    ];

    public EntryObservabilityReport Get()
    {
        var now = clock.GetLocalNow();
        var rows = structure.PublishedViews().Select(view => Row(view, now)).ToArray();
        return new(now, rows.Sum(x => x.CandidateCount), rows.Sum(x => x.ApprovedCount),
            rows.Sum(x => x.RejectedCount), rows);
    }

    static EntryObservationRow Row(StructureAnalysisView view, DateTimeOffset now)
    {
        var candidates = view.Candidates ?? [];
        var approved = candidates.Count(x => x.State is "READY" or "ENTERED");
        var rejected = candidates.Count(x => x.State is not ("READY" or "ENTERED"));
        var reasons = candidates.SelectMany(x => x.RejectionCodes ?? [])
            .Concat(view.Quality?.BlockersForCandidate ?? [])
            .Concat(view.Quality?.BlockersForReady ?? [])
            .Concat(view.Warnings ?? [])
            .Distinct(StringComparer.Ordinal).OrderBy(GateOrder).ThenBy(x => x, StringComparer.Ordinal).ToArray();
        var duplicate = candidates.SelectMany(x => (x.RejectionCodes ?? []).Concat(x.Notes ?? []))
            .Concat(view.Notes ?? []).Where(IsDuplicate)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var final = candidates.Length == 0 ? "NO_CANDIDATE"
            : candidates.Any(x => x.State == "ENTERED") ? "ENTERED"
            : candidates.Any(x => x.State == "READY") ? "READY"
            : "REJECTED";
        double? delay = view.QuoteAt is { } quoteAt ? Math.Max(0, (now - quoteAt).TotalSeconds) : null;
        return new(view.Symbol, view.Status, view.EvaluatedAt, view.AnalysisAsOf, view.QuoteAt,
            delay is null ? null : Math.Round(delay.Value, 1), candidates.Length, approved, rejected, final,
            reasons.FirstOrDefault(), duplicate, reasons);
    }

    static bool IsDuplicate(string code) => DuplicateCodes.Any(x =>
        code.Equals(x, StringComparison.Ordinal) || code.StartsWith(x + ":", StringComparison.Ordinal));

    static int GateOrder(string reason) => reason switch
    {
        "INVALID_BARS" or "BAR_GAP" or "BAR_CONFLICT" => 0,
        "STALE_LATEST_BAR" or "MISSING_QUOTE" or "STALE_QUOTE" or "QUOTE_IN_FUTURE" or
            "OUTSIDE_REGULAR_SESSION" or "AFTER_ENTRY_CUTOFF" => 10,
        "DUPLICATE_TRIGGER_GUARD" or "TRIGGER_EPISODE_CONSUMED" or "NEW_TRIGGER_SUPPRESSED" or
            "BREAKOUT_ZONE_COOLDOWN" => 20,
        _ when reason.StartsWith("V5_ENTRY_", StringComparison.Ordinal) => 80,
        _ => 50
    };
}
