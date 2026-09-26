using Astra.Server.Domain.Confluence;

namespace Astra.Server.Domain.Validation;

public sealed record ProbabilityObservation(string EventId, DateTimeOffset PredictedAt, DateTimeOffset OutcomeAt,
    double Probability, bool Outcome);

public sealed record ProbabilityCalibrationPolicy(int MinimumTotalSamples, int MinimumBaselineSamples,
    int MinimumRecentSamples, int RecentSampleCount, int EceBins)
{
    public static readonly ProbabilityCalibrationPolicy Default = new(30, 15, 10, 10, 10);
}

public sealed record ProbabilityCalibrationMetrics(int Samples, int Successes, double? SuccessRate,
    ProportionInterval? Wilson95, double? Brier, double? Ece);

public sealed record ProbabilityDriftMetrics(string Status, ProbabilityCalibrationMetrics Baseline,
    ProbabilityCalibrationMetrics Recent, double? BrierDelta, double? EceDelta);

public sealed record ProbabilityCalibrationReport(string Status, DateTimeOffset AsOf, int InputRows,
    int UsableRows, int DuplicateEvents, int FutureRows, ProbabilityCalibrationMetrics Overall,
    ProbabilityDriftMetrics Drift, IReadOnlyList<string> Limitations);

public static class ProbabilityCalibrationEvaluator
{
    public const string Ok = "ok";
    public const string InsufficientData = "insufficient_data";

    public static ProbabilityCalibrationReport Evaluate(IReadOnlyList<ProbabilityObservation> observations,
        DateTimeOffset asOf, ProbabilityCalibrationPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var limits = policy ?? ProbabilityCalibrationPolicy.Default;
        Validate(limits);

        var future = 0;
        var invalid = 0;
        var duplicates = 0;
        var byEvent = new Dictionary<string, ProbabilityObservation>(StringComparer.Ordinal);
        foreach (var observation in observations.OrderBy(x => x.PredictedAt).ThenBy(x => x.EventId,
                     StringComparer.Ordinal))
        {
            if (observation is null || string.IsNullOrWhiteSpace(observation.EventId) ||
                !double.IsFinite(observation.Probability) || observation.Probability is < 0 or > 1 ||
                observation.OutcomeAt < observation.PredictedAt)
            {
                invalid++;
                continue;
            }
            if (observation.PredictedAt > asOf || observation.OutcomeAt > asOf)
            {
                future++;
                continue;
            }
            if (!byEvent.TryAdd(observation.EventId, observation)) duplicates++;
        }

        var usable = byEvent.Values.OrderBy(x => x.OutcomeAt).ThenBy(x => x.PredictedAt)
            .ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
        var overall = Measure(usable, limits.EceBins);
        var recentCount = Math.Min(limits.RecentSampleCount, usable.Length);
        var recentRows = usable.TakeLast(recentCount).ToArray();
        var baselineRows = usable.Take(Math.Max(0, usable.Length - recentCount)).ToArray();
        var recent = Measure(recentRows, limits.EceBins);
        var baseline = Measure(baselineRows, limits.EceBins);

        var limitations = new List<string>();
        if (usable.Length < limits.MinimumTotalSamples)
            limitations.Add($"INSUFFICIENT_TOTAL_SAMPLES:{usable.Length}/{limits.MinimumTotalSamples}");
        if (baselineRows.Length < limits.MinimumBaselineSamples)
            limitations.Add($"INSUFFICIENT_BASELINE_SAMPLES:{baselineRows.Length}/{limits.MinimumBaselineSamples}");
        if (recentRows.Length < limits.MinimumRecentSamples)
            limitations.Add($"INSUFFICIENT_RECENT_SAMPLES:{recentRows.Length}/{limits.MinimumRecentSamples}");
        if (invalid > 0) limitations.Add($"INVALID_ROWS:{invalid}");

        var status = limitations.Any(x => x.StartsWith("INSUFFICIENT_", StringComparison.Ordinal))
            ? InsufficientData : Ok;
        var drift = status == Ok
            ? new ProbabilityDriftMetrics(Ok, baseline, recent,
                Round(recent.Brier!.Value - baseline.Brier!.Value),
                Round(recent.Ece!.Value - baseline.Ece!.Value))
            : new ProbabilityDriftMetrics(InsufficientData, baseline, recent, null, null);
        return new(status, asOf, observations.Count, usable.Length, duplicates, future, overall, drift,
            limitations);
    }

    static ProbabilityCalibrationMetrics Measure(IReadOnlyList<ProbabilityObservation> rows, int bins)
    {
        if (rows.Count == 0) return new(0, 0, null, null, null, null);
        var successes = rows.Count(x => x.Outcome);
        var brier = MeasurementStatistics.Brier(rows.Select(x => (x.Probability, x.Outcome)).ToArray());
        return new(rows.Count, successes, Round((double)successes / rows.Count),
            MeasurementStatistics.Wilson(successes, rows.Count), Round(brier), Round(Ece(rows, bins)));
    }

    static double Ece(IReadOnlyList<ProbabilityObservation> rows, int bins)
    {
        double total = 0;
        foreach (var bucket in rows.GroupBy(x => Math.Min((int)(x.Probability * bins), bins - 1)))
        {
            var count = bucket.Count();
            var confidence = bucket.Average(x => x.Probability);
            var observed = bucket.Count(x => x.Outcome) / (double)count;
            total += count / (double)rows.Count * Math.Abs(observed - confidence);
        }
        return total;
    }

    static void Validate(ProbabilityCalibrationPolicy policy)
    {
        if (policy.MinimumTotalSamples < 1 || policy.MinimumBaselineSamples < 1 ||
            policy.MinimumRecentSamples < 1 || policy.RecentSampleCount < policy.MinimumRecentSamples ||
            policy.EceBins < 2)
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    static double Round(double value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);
}
