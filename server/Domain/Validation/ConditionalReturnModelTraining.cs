using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Domain.Validation;

public sealed record ConditionalReturnModelSample(string EventId, string Symbol,
    DateTimeOffset FeatureAt, DateTimeOffset EnteredAt, DateTimeOffset OutcomeAt,
    ConditionalReturnForecastInput Features, bool TargetHit, double NetReturnPercent);

public sealed record ConditionalReturnTrainingPolicy(int MinimumTrainingSamples, int MinimumValidationSamples,
    double RidgePenalty, int MaximumIterations, double ConvergenceTolerance, int EceBins)
{
    public static readonly ConditionalReturnTrainingPolicy Default = new(30, 15, 1d, 100, 1e-7, 10);
}

public sealed record ConditionalReturnPerformance(int TradeCount, double? ExpectedNetReturnPercent,
    double? ProfitFactor, double? MaximumDrawdownPercent, ProbabilityCalibrationReport Calibration);

public sealed record ConditionalReturnModelEvaluation(string Status, string ModelVersion,
    string FeatureSchemaHash, DateTimeOffset AsOf, int TrainingInputRows, int ValidationInputRows,
    int TrainingSamples, int ValidationSamples, int DuplicateEvents, int InvalidRows, int FutureRows,
    int LeakageRows, int Iterations, ConditionalReturnModel? Model,
    ConditionalReturnPerformance? Validation, IReadOnlyList<string> Limitations);

public static class ConditionalReturnWalkForwardTrainer
{
    public const string Version = "conditional-return-logit-ridge.v1";
    public const string Validated = "validated";
    public const string Rejected = "rejected";
    public const string InsufficientData = "insufficient_data";
    public const string NonIdentifiable = "non_identifiable";
    public const string ConvergenceFailed = "convergence_failed";
    public const string FeatureSchema = ConditionalReturnFeatureSchema.Definition;
    public static string FeatureSchemaHash => ConditionalReturnFeatureSchema.Hash;

    public static ConditionalReturnModelEvaluation Evaluate(
        IReadOnlyList<ConditionalReturnModelSample> training,
        IReadOnlyList<ConditionalReturnModelSample> validation, DateTimeOffset asOf,
        ConditionalReturnTrainingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(validation);
        var limits = policy ?? ConditionalReturnTrainingPolicy.Default;
        ValidatePolicy(limits);

        var all = training.Select(x => (Row: x, Training: true))
            .Concat(validation.Select(x => (Row: x, Training: false))).ToArray();
        var duplicateIds = all.Where(x => !string.IsNullOrWhiteSpace(x.Row?.EventId))
            .GroupBy(x => x.Row.EventId, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var duplicates = all.Count(x => x.Row is not null && duplicateIds.Contains(x.Row.EventId));
        var boundary = validation.Where(x => x is not null).Select(x => x.FeatureAt)
            .DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var invalid = 0;
        var future = 0;
        var leakage = 0;
        var trainRows = new List<ConditionalReturnModelSample>();
        var validationRows = new List<ConditionalReturnModelSample>();

        foreach (var (row, isTraining) in all.OrderBy(x => x.Row?.FeatureAt)
                     .ThenBy(x => x.Row?.EventId, StringComparer.Ordinal))
        {
            if (row is null || !Valid(row))
            {
                invalid++;
                continue;
            }
            if (duplicateIds.Contains(row.EventId)) continue;
            if (row.FeatureAt > asOf || row.OutcomeAt > asOf)
            {
                future++;
                continue;
            }
            if (isTraining && (row.FeatureAt >= boundary || row.OutcomeAt >= boundary))
            {
                leakage++;
                continue;
            }
            (isTraining ? trainRows : validationRows).Add(row);
        }

        var limitations = new List<string>();
        if (trainRows.Count < limits.MinimumTrainingSamples)
            limitations.Add($"INSUFFICIENT_TRAINING_SAMPLES:{trainRows.Count}/{limits.MinimumTrainingSamples}");
        if (validationRows.Count < limits.MinimumValidationSamples)
            limitations.Add($"INSUFFICIENT_VALIDATION_SAMPLES:{validationRows.Count}/{limits.MinimumValidationSamples}");
        AddRejectedCounts(limitations, duplicates, invalid, future, leakage);
        if (limitations.Any(x => x.StartsWith("INSUFFICIENT_", StringComparison.Ordinal)))
            return Report(InsufficientData, asOf, training, validation, trainRows, validationRows,
                duplicates, invalid, future, leakage, 0, null, null, limitations);

        if (trainRows.All(x => x.TargetHit) || trainRows.All(x => !x.TargetHit))
        {
            limitations.Add("TRAINING_OUTCOME_HAS_SINGLE_CLASS");
            return Report(NonIdentifiable, asOf, training, validation, trainRows, validationRows,
                duplicates, invalid, future, leakage, 0, null, null, limitations);
        }

        var matrix = trainRows.Select(Vector).ToArray();
        var means = new double[5];
        var scales = new double[5];
        for (var column = 0; column < 5; column++)
        {
            means[column] = matrix.Average(x => x[column]);
            scales[column] = Math.Sqrt(matrix.Average(x => Math.Pow(x[column] - means[column], 2)));
        }
        if (scales.All(x => x <= 1e-12))
        {
            limitations.Add("TRAINING_FEATURES_HAVE_NO_VARIATION");
            return Report(NonIdentifiable, asOf, training, validation, trainRows, validationRows,
                duplicates, invalid, future, leakage, 0, null, null, limitations);
        }
        for (var i = 0; i < scales.Length; i++)
            if (scales[i] <= 1e-12) scales[i] = 1;

        var fit = Fit(trainRows, means, scales, limits);
        if (fit.Coefficients is null)
        {
            limitations.Add(fit.Status == NonIdentifiable ? "LOGISTIC_HESSIAN_NOT_IDENTIFIABLE" :
                $"LOGISTIC_DID_NOT_CONVERGE:{fit.Iterations}/{limits.MaximumIterations}");
            return Report(fit.Status, asOf, training, validation, trainRows, validationRows,
                duplicates, invalid, future, leakage, fit.Iterations, null, null, limitations);
        }

        var trainedThrough = trainRows.Max(x => x.OutcomeAt);
        var validatedThrough = validationRows.Max(x => x.OutcomeAt);
        var rawModel = Model(fit.Coefficients, means, scales, trainedThrough, validatedThrough,
            trainRows.Count, validationRows.Count, limits.MinimumValidationSamples,
            ConditionalForecastStatus.ExperimentalUncalibrated);
        var performance = Measure(validationRows, rawModel, asOf, limits);
        var accepted = performance.ExpectedNetReturnPercent is > 0 && performance.ProfitFactor is > 1 &&
                       performance.Calibration.Status == ProbabilityCalibrationEvaluator.Ok;
        if (performance.ExpectedNetReturnPercent is not > 0)
            limitations.Add("VALIDATION_EXPECTED_VALUE_NOT_POSITIVE");
        if (performance.ProfitFactor is not > 1)
            limitations.Add("VALIDATION_PROFIT_FACTOR_NOT_ABOVE_ONE");
        if (performance.Calibration.Status != ProbabilityCalibrationEvaluator.Ok)
            limitations.Add("VALIDATION_CALIBRATION_INSUFFICIENT");
        var model = rawModel with
        {
            CalibrationStatus = accepted ? ConditionalForecastStatus.Calibrated :
                ConditionalForecastStatus.ExperimentalUncalibrated
        };
        return Report(accepted ? Validated : Rejected, asOf, training, validation, trainRows, validationRows,
            duplicates, invalid, future, leakage, fit.Iterations, model, performance, limitations);
    }

    static ConditionalReturnPerformance Measure(IReadOnlyList<ConditionalReturnModelSample> rows,
        ConditionalReturnModel model, DateTimeOffset asOf, ConditionalReturnTrainingPolicy policy)
    {
        var ordered = rows.OrderBy(x => x.OutcomeAt).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
        var probabilities = ordered.Select(x => new ProbabilityObservation(x.EventId, x.FeatureAt, x.OutcomeAt,
            Probability(model, x.Features), x.TargetHit)).ToArray();
        var recent = Math.Max(5, Math.Min(10, ordered.Length / 3));
        var baseline = Math.Max(5, ordered.Length - recent);
        var calibration = ProbabilityCalibrationEvaluator.Evaluate(probabilities, asOf,
            new ProbabilityCalibrationPolicy(policy.MinimumValidationSamples, baseline, recent, recent,
                policy.EceBins));
        var returns = ordered.Select(x => x.NetReturnPercent).ToArray();
        var gains = returns.Where(x => x > 0).Sum();
        var losses = -returns.Where(x => x < 0).Sum();
        double equity = 0, peak = 0, drawdown = 0;
        foreach (var value in returns)
        {
            equity += value;
            peak = Math.Max(peak, equity);
            drawdown = Math.Max(drawdown, peak - equity);
        }
        return new(returns.Length, Round(returns.Average()), losses > 0 ? Round(gains / losses) : null,
            Round(drawdown), calibration);
    }

    static (string Status, int Iterations, double[]? Coefficients) Fit(
        IReadOnlyList<ConditionalReturnModelSample> rows, double[] means, double[] scales,
        ConditionalReturnTrainingPolicy policy)
    {
        var beta = new double[6];
        for (var iteration = 1; iteration <= policy.MaximumIterations; iteration++)
        {
            var gradient = new double[6];
            var hessian = new double[6, 6];
            foreach (var row in rows)
            {
                var raw = Vector(row);
                var x = new[] { 1d, (raw[0] - means[0]) / scales[0], (raw[1] - means[1]) / scales[1],
                    (raw[2] - means[2]) / scales[2], (raw[3] - means[3]) / scales[3],
                    (raw[4] - means[4]) / scales[4] };
                var probability = Logistic(Dot(beta, x));
                var weight = Math.Max(1e-9, probability * (1 - probability));
                var outcome = row.TargetHit ? 1d : 0d;
                for (var i = 0; i < beta.Length; i++)
                {
                    gradient[i] += (probability - outcome) * x[i];
                    for (var j = 0; j < beta.Length; j++) hessian[i, j] += weight * x[i] * x[j];
                }
            }
            for (var i = 1; i < beta.Length; i++)
            {
                gradient[i] += policy.RidgePenalty * beta[i];
                hessian[i, i] += policy.RidgePenalty;
            }
            if (!Solve(hessian, gradient, out var delta)) return (NonIdentifiable, iteration, null);
            for (var i = 0; i < beta.Length; i++) beta[i] -= delta[i];
            if (beta.Any(x => !double.IsFinite(x))) return (NonIdentifiable, iteration, null);
            if (delta.Max(Math.Abs) <= policy.ConvergenceTolerance) return (Validated, iteration, beta);
        }
        return (ConvergenceFailed, policy.MaximumIterations, null);
    }

    static ConditionalReturnModel Model(double[] standardized, double[] means, double[] scales,
        DateTimeOffset trainedThrough, DateTimeOffset validatedThrough, int trainingSamples,
        int validationSamples, int minimumValidationSamples, string calibrationStatus)
    {
        var coefficients = new double[5];
        var intercept = standardized[0];
        for (var i = 0; i < coefficients.Length; i++)
        {
            coefficients[i] = standardized[i + 1] / scales[i];
            intercept -= standardized[i + 1] * means[i] / scales[i];
        }
        return new(Version, trainedThrough, validatedThrough, trainingSamples, validationSamples,
            minimumValidationSamples, calibrationStatus, Round(intercept), Round(coefficients[0]),
            Round(coefficients[1]), Round(coefficients[2]), Round(coefficients[3]), Round(coefficients[4]),
            FeatureSchemaHash);
    }

    static ConditionalReturnModelEvaluation Report(string status, DateTimeOffset asOf,
        IReadOnlyList<ConditionalReturnModelSample> training,
        IReadOnlyList<ConditionalReturnModelSample> validation,
        IReadOnlyList<ConditionalReturnModelSample> usableTraining,
        IReadOnlyList<ConditionalReturnModelSample> usableValidation, int duplicates, int invalid, int future,
        int leakage, int iterations, ConditionalReturnModel? model, ConditionalReturnPerformance? performance,
        IReadOnlyList<string> limitations) =>
        new(status, Version, FeatureSchemaHash, asOf, training.Count, validation.Count,
            usableTraining.Count, usableValidation.Count, duplicates, invalid, future, leakage, iterations,
            model, performance, limitations);

    static void AddRejectedCounts(List<string> limitations, int duplicates, int invalid, int future, int leakage)
    {
        if (duplicates > 0) limitations.Add($"DUPLICATE_EVENT_ROWS:{duplicates}");
        if (invalid > 0) limitations.Add($"INVALID_ROWS:{invalid}");
        if (future > 0) limitations.Add($"FUTURE_ROWS:{future}");
        if (leakage > 0) limitations.Add($"TRAINING_HOLDOUT_LEAKAGE_ROWS:{leakage}");
    }

    static bool Valid(ConditionalReturnModelSample row) =>
        !string.IsNullOrWhiteSpace(row.EventId) && !string.IsNullOrWhiteSpace(row.Symbol) &&
        row.FeatureAt != default && row.EnteredAt >= row.FeatureAt && row.OutcomeAt >= row.EnteredAt &&
        row.Features.AsOf == row.FeatureAt &&
        double.IsFinite(row.NetReturnPercent) && Valid(row.Features);

    static bool Valid(ConditionalReturnForecastInput input) => input.Volatility is not null &&
        Finite(input.StructureSignal, 0, 1) && Finite(input.AtrPercent, 0, double.MaxValue) &&
        Finite(input.TrendAlignment, -1, 1) && Finite(input.TargetReturnPercent, 0, double.MaxValue, true) &&
        Finite(input.StopReturnPercent, 0, double.MaxValue, true) &&
        Finite(input.RoundTripCostPercent, 0, double.MaxValue) && Finite(input.SlippagePercent, 0, double.MaxValue);

    static bool Finite(double? value, double low, double high, bool exclusiveLow = false) =>
        value is { } number && double.IsFinite(number) && (exclusiveLow ? number > low : number >= low) &&
        number <= high;

    static double[] Vector(ConditionalReturnModelSample row) =>
    [
        row.Features.StructureSignal!.Value,
        row.Features.AtrPercent!.Value,
        row.Features.TrendAlignment!.Value,
        row.Features.Volatility == VolatilityBand.Low ? 1 : 0,
        row.Features.Volatility == VolatilityBand.High ? 1 : 0
    ];

    static double Probability(ConditionalReturnModel model, ConditionalReturnForecastInput input) => Logistic(
        model.Intercept + model.StructureSignalCoefficient * input.StructureSignal!.Value +
        model.AtrPercentCoefficient * input.AtrPercent!.Value +
        model.TrendAlignmentCoefficient * input.TrendAlignment!.Value +
        (input.Volatility == VolatilityBand.Low ? model.LowVolatilityCoefficient : 0) +
        (input.Volatility == VolatilityBand.High ? model.HighVolatilityCoefficient : 0));

    static double Logistic(double value)
    {
        if (value >= 0)
        {
            var exponential = Math.Exp(-Math.Min(value, 700));
            return 1 / (1 + exponential);
        }
        var negative = Math.Exp(Math.Max(value, -700));
        return negative / (1 + negative);
    }

    static double Dot(double[] left, double[] right)
    {
        double sum = 0;
        for (var i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }

    static bool Solve(double[,] matrix, double[] vector, out double[] solution)
    {
        var n = vector.Length;
        var augmented = new double[n, n + 1];
        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++) augmented[row, column] = matrix[row, column];
            augmented[row, n] = vector[row];
        }
        for (var pivot = 0; pivot < n; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < n; row++)
                if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
            if (Math.Abs(augmented[best, pivot]) < 1e-12)
            {
                solution = [];
                return false;
            }
            if (best != pivot)
                for (var column = pivot; column <= n; column++)
                    (augmented[pivot, column], augmented[best, column]) =
                        (augmented[best, column], augmented[pivot, column]);
            var divisor = augmented[pivot, pivot];
            for (var column = pivot; column <= n; column++) augmented[pivot, column] /= divisor;
            for (var row = 0; row < n; row++)
            {
                if (row == pivot) continue;
                var factor = augmented[row, pivot];
                for (var column = pivot; column <= n; column++)
                    augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        solution = Enumerable.Range(0, n).Select(row => augmented[row, n]).ToArray();
        return solution.All(double.IsFinite);
    }

    static void ValidatePolicy(ConditionalReturnTrainingPolicy policy)
    {
        if (policy.MinimumTrainingSamples < 2 || policy.MinimumValidationSamples < 10 ||
            !double.IsFinite(policy.RidgePenalty) || policy.RidgePenalty <= 0 ||
            policy.MaximumIterations < 1 || !double.IsFinite(policy.ConvergenceTolerance) ||
            policy.ConvergenceTolerance <= 0 || policy.EceBins < 2)
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    static double Round(double value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);
}
