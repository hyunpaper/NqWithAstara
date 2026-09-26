using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ConditionalReturnModelTrainingTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T14:30:00Z");

    [Fact]
    public void 시간순_훈련과_검증으로_명시적_모델과_성과를_만든다()
    {
        var training = Rows("train", 60, Start, targetOffset: 0);
        var validation = Rows("validation", 20, Start.AddDays(10), targetOffset: 1);

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(training, validation, Start.AddDays(20));

        Assert.Equal(ConditionalReturnWalkForwardTrainer.Validated, result.Status);
        Assert.NotNull(result.Model);
        Assert.NotNull(result.Validation);
        Assert.Equal(60, result.Model.TrainingSamples);
        Assert.Equal(20, result.Model.ValidationSamples);
        Assert.Equal(ConditionalReturnWalkForwardTrainer.FeatureSchemaHash, result.Model.FeatureSchemaHash);
        Assert.Equal(20, result.Validation.TradeCount);
        Assert.True(result.Validation.ExpectedNetReturnPercent > 0);
        Assert.True(result.Validation.ProfitFactor > 1);
        Assert.NotNull(result.Validation.Calibration.Overall.Brier);
        Assert.NotNull(result.Validation.Calibration.Overall.Ece);
        Assert.NotNull(result.Validation.Calibration.Overall.Wilson95);
    }

    [Fact]
    public void 최소_표본이_없으면_계수를_만들지_않는다()
    {
        var result = ConditionalReturnWalkForwardTrainer.Evaluate(
            Rows("train", 10, Start, 0), Rows("validation", 5, Start.AddDays(10), 0), Start.AddDays(20));

        Assert.Equal(ConditionalReturnWalkForwardTrainer.InsufficientData, result.Status);
        Assert.Null(result.Model);
        Assert.Null(result.Validation);
        Assert.Contains("INSUFFICIENT_TRAINING_SAMPLES:10/30", result.Limitations);
        Assert.Contains("INSUFFICIENT_VALIDATION_SAMPLES:5/15", result.Limitations);
    }

    [Fact]
    public void 중복_미래_cutoff_holdout_누수_행은_모두_거부한다()
    {
        var training = Rows("train", 30, Start, 0).ToList();
        var validation = Rows("validation", 15, Start.AddDays(10), 1).ToList();
        var duplicate = Sample("duplicate", Start.AddHours(1), true, .8);
        training.Add(duplicate);
        validation.Add(duplicate with { FeatureAt = Start.AddDays(10),
            EnteredAt = Start.AddDays(10).AddSeconds(30), OutcomeAt = Start.AddDays(10).AddMinutes(1),
            Features = Features(Start.AddDays(10), .8) });
        training.Add(Sample("leak", Start.AddDays(9), true, .8) with
            { OutcomeAt = Start.AddDays(10).AddMinutes(1) });
        validation.Add(Sample("future-feature", Start.AddDays(30), true, .8));
        validation.Add(Sample("future-outcome", Start.AddDays(10).AddHours(1), true, .8) with
            { OutcomeAt = Start.AddDays(30) });
        validation.Add(Sample("invalid", Start.AddDays(10).AddHours(2), true, .8) with
            { Features = Features(Start.AddDays(10).AddHours(3), .8) });
        validation.Add(Sample("post-entry-feature", Start.AddDays(10).AddHours(3), true, .8) with
            { EnteredAt = Start.AddDays(10).AddHours(3).AddSeconds(-1) });

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(training, validation, Start.AddDays(20));

        Assert.Equal(2, result.DuplicateEvents);
        Assert.Equal(2, result.FutureRows);
        Assert.Equal(2, result.InvalidRows);
        Assert.Equal(1, result.LeakageRows);
        Assert.Equal(30, result.TrainingSamples);
        Assert.Equal(15, result.ValidationSamples);
        Assert.Contains("DUPLICATE_EVENT_ROWS:2", result.Limitations);
        Assert.Contains("TRAINING_HOLDOUT_LEAKAGE_ROWS:1", result.Limitations);
    }

    [Fact]
    public void 단일_결과_클래스는_비식별로_보고한다()
    {
        var training = Enumerable.Range(0, 30)
            .Select(i => Sample($"train-{i}", Start.AddMinutes(i * 2), true, .3 + i / 100d)).ToArray();
        var validation = Rows("validation", 15, Start.AddDays(10), 0);

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(training, validation, Start.AddDays(20));

        Assert.Equal(ConditionalReturnWalkForwardTrainer.NonIdentifiable, result.Status);
        Assert.Null(result.Model);
        Assert.Contains("TRAINING_OUTCOME_HAS_SINGLE_CLASS", result.Limitations);
    }

    [Fact]
    public void 반복_한도가_수렴보다_작으면_실패를_명시한다()
    {
        var policy = ConditionalReturnTrainingPolicy.Default with
        {
            MaximumIterations = 1,
            ConvergenceTolerance = 1e-15
        };

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(
            Rows("train", 60, Start, 0), Rows("validation", 20, Start.AddDays(10), 1),
            Start.AddDays(20), policy);

        Assert.Equal(ConditionalReturnWalkForwardTrainer.ConvergenceFailed, result.Status);
        Assert.Null(result.Model);
        Assert.Contains("LOGISTIC_DID_NOT_CONVERGE:1/1", result.Limitations);
    }

    static ConditionalReturnModelSample[] Rows(string prefix, int count, DateTimeOffset start, int targetOffset) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var signal = start.AddMinutes(i * 2);
            var quality = .15 + (i % 10) * .08;
            var target = (i + targetOffset) % 3 != 0;
            return Sample($"{prefix}-{i}", signal, target, quality);
        }).ToArray();

    static ConditionalReturnModelSample Sample(string id, DateTimeOffset signal, bool target, double quality) =>
        new(id, "SOXL", signal, signal.AddSeconds(30), signal.AddMinutes(1), Features(signal, quality), target,
            target ? 1.2 : -.45);

    static ConditionalReturnForecastInput Features(DateTimeOffset signal, double quality) =>
        new(signal, TradeSide.Long, quality, .4 + quality / 4, quality * 2 - 1,
            quality > .7 ? VolatilityBand.High : quality < .35 ? VolatilityBand.Low : VolatilityBand.Normal,
            1.2, .6, .1, .05);
}
