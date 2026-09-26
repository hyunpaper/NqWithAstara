using Astra.Server;

namespace Astra.Server.Domain.Structure;

public static class ConditionalForecastStatus
{
    public const string Calibrated = "calibrated";
    public const string ExperimentalUncalibrated = "experimental_uncalibrated";
    public const string InsufficientData = "insufficient_data";
}

public sealed record ConditionalReturnForecastInput(DateTimeOffset AsOf, TradeSide Side,
    double? StructureSignal, double? AtrPercent, double? TrendAlignment, VolatilityBand? Volatility,
    double? TargetReturnPercent, double? StopReturnPercent, double? RoundTripCostPercent,
    double? SlippagePercent);

public sealed record ConditionalReturnModel(string Version, DateTimeOffset TrainedThrough,
    DateTimeOffset ValidatedThrough, int TrainingSamples, int ValidationSamples,
    int MinimumCalibrationSamples, string CalibrationStatus, double Intercept,
    double StructureSignalCoefficient, double AtrPercentCoefficient, double TrendAlignmentCoefficient,
    double LowVolatilityCoefficient, double HighVolatilityCoefficient);

public sealed record ConditionalReturnForecast(string Status, string ModelVersion, DateTimeOffset AsOf,
    int CalibrationSamples, DateTimeOffset? ModelTrainedThrough, DateTimeOffset? ModelValidatedThrough,
    double? SuccessProbability, double? ExpectedGrossReturnPercent,
    double? ExpectedNetReturnPercent, double? StopProbability, double? TargetProbability,
    double? ExpectedValuePercent, IReadOnlyList<string> Evidence, IReadOnlyList<string> Limitations);

public static class ConditionalReturnForecaster
{
    public const string NoModel = "MODEL_UNAVAILABLE";
    public const string ModelAfterSignal = "MODEL_TRAINED_AFTER_SIGNAL";
    public const string ValidationAfterSignal = "MODEL_VALIDATED_AFTER_SIGNAL";
    public const string InvalidInput = "INVALID_OR_MISSING_INPUT";

    public static ConditionalReturnForecast Evaluate(ConditionalReturnForecastInput input,
        ConditionalReturnModel? model)
    {
        ArgumentNullException.ThrowIfNull(input);
        var evidence = Evidence(input);
        if (model is null)
            return Missing(input, null, NoModel, evidence);
        if (!Valid(input))
            return Missing(input, model, InvalidInput, evidence);
        if (model.TrainedThrough >= input.AsOf)
            return Missing(input, model, ModelAfterSignal, evidence);
        if (model.ValidatedThrough >= input.AsOf)
            return Missing(input, model, ValidationAfterSignal, evidence);
        if (!Valid(model))
            return Missing(input, model, InvalidInput, evidence);

        var volatility = input.Volatility!.Value;
        var logit = model.Intercept + model.StructureSignalCoefficient * input.StructureSignal!.Value +
                    model.AtrPercentCoefficient * input.AtrPercent!.Value +
                    model.TrendAlignmentCoefficient * input.TrendAlignment!.Value +
                    (volatility == VolatilityBand.Low ? model.LowVolatilityCoefficient : 0) +
                    (volatility == VolatilityBand.High ? model.HighVolatilityCoefficient : 0);
        var targetProbability = Logistic(logit);
        var stopProbability = 1 - targetProbability;
        var expectedGross = targetProbability * input.TargetReturnPercent!.Value -
                            stopProbability * input.StopReturnPercent!.Value;
        var expectedNet = expectedGross - input.RoundTripCostPercent!.Value - input.SlippagePercent!.Value;
        var calibrated = string.Equals(model.CalibrationStatus, ConditionalForecastStatus.Calibrated,
                             StringComparison.Ordinal) &&
                         model.ValidationSamples >= model.MinimumCalibrationSamples;
        return new(calibrated ? ConditionalForecastStatus.Calibrated :
                ConditionalForecastStatus.ExperimentalUncalibrated,
            model.Version, input.AsOf, model.ValidationSamples, model.TrainedThrough, model.ValidatedThrough,
            Round(targetProbability), Round(expectedGross),
            Round(expectedNet), Round(stopProbability), Round(targetProbability), Round(expectedNet), evidence,
            calibrated ? [] : ["MODEL_NOT_CALIBRATED"]);
    }

    static ConditionalReturnForecast Missing(ConditionalReturnForecastInput input, ConditionalReturnModel? model,
        string limitation, IReadOnlyList<string> evidence) =>
        new(ConditionalForecastStatus.InsufficientData, model?.Version ?? "unavailable", input.AsOf,
            model?.ValidationSamples ?? 0, model?.TrainedThrough, model?.ValidatedThrough,
            null, null, null, null, null, null, evidence, [limitation]);

    static bool Valid(ConditionalReturnForecastInput input) =>
        input.AsOf != default && input.Volatility is not null && Finite(input.StructureSignal, 0, 1) &&
        Finite(input.AtrPercent, 0, double.MaxValue) && Finite(input.TrendAlignment, -1, 1) &&
        Finite(input.TargetReturnPercent, 0, double.MaxValue, exclusiveLow: true) &&
        Finite(input.StopReturnPercent, 0, double.MaxValue, exclusiveLow: true) &&
        Finite(input.RoundTripCostPercent, 0, double.MaxValue) &&
        Finite(input.SlippagePercent, 0, double.MaxValue);

    static bool Valid(ConditionalReturnModel model) =>
        !string.IsNullOrWhiteSpace(model.Version) && model.TrainedThrough != default &&
        model.ValidatedThrough != default && model.ValidatedThrough >= model.TrainedThrough &&
        model.TrainingSamples >= 0 && model.ValidationSamples >= 0 && model.MinimumCalibrationSamples > 0 &&
        Coefficients(model).All(double.IsFinite);

    static IEnumerable<double> Coefficients(ConditionalReturnModel model)
    {
        yield return model.Intercept;
        yield return model.StructureSignalCoefficient;
        yield return model.AtrPercentCoefficient;
        yield return model.TrendAlignmentCoefficient;
        yield return model.LowVolatilityCoefficient;
        yield return model.HighVolatilityCoefficient;
    }

    static bool Finite(double? value, double low, double high, bool exclusiveLow = false) =>
        value is { } number && double.IsFinite(number) &&
        (exclusiveLow ? number > low : number >= low) && number <= high;

    static double Logistic(double value)
    {
        if (value >= 0)
        {
            var e = Math.Exp(-Math.Min(value, 700));
            return 1 / (1 + e);
        }
        var negative = Math.Exp(Math.Max(value, -700));
        return negative / (1 + negative);
    }

    static double Round(double value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

    static string[] Evidence(ConditionalReturnForecastInput input) =>
    [
        $"AS_OF:{StructureMath.Iso(input.AsOf)}",
        $"SIDE:{input.Side.ToString().ToUpperInvariant()}",
        $"STRUCTURE_SIGNAL:{StructureMath.Number(input.StructureSignal)}",
        $"ATR_PERCENT:{StructureMath.Number(input.AtrPercent)}",
        $"TREND_ALIGNMENT:{StructureMath.Number(input.TrendAlignment)}",
        $"VOLATILITY:{input.Volatility?.ToString().ToUpperInvariant() ?? "MISSING"}",
        $"TARGET_RETURN_PERCENT:{StructureMath.Number(input.TargetReturnPercent)}",
        $"STOP_RETURN_PERCENT:{StructureMath.Number(input.StopReturnPercent)}",
        $"ROUND_TRIP_COST_PERCENT:{StructureMath.Number(input.RoundTripCostPercent)}",
        $"SLIPPAGE_PERCENT:{StructureMath.Number(input.SlippagePercent)}"
    ];
}
