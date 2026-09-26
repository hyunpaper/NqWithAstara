using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;
using System.Text.Json;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ConditionalReturnModelTrainingTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T14:30:00Z");

    [Fact]
    public void 시간순_훈련과_검증으로_명시적_모델과_성과를_만든다()
    {
        var training = SeparableRows("train", 60, Start);
        var validation = SeparableRows("validation", 20, Start.AddDays(10));

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(training, validation, Start.AddDays(20));

        Assert.True(result.Status == ConditionalReturnWalkForwardTrainer.Validated,
            $"{string.Join(',', result.Limitations)}; brier={result.Validation?.Calibration.Overall.Brier}; ece={result.Validation?.Calibration.Overall.Ece}; noSkill={result.Validation?.NoSkillBrier}");
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
    public void 양수_수익이어도_no_skill_Brier보다_나쁘면_모델을_거부한다()
    {
        var training = SeparableRows("train", 60, Start);
        var validation = SeparableRows("validation", 20, Start.AddDays(10))
            .Select(x => x with { TargetHit = false, NetReturnPercent = 1.2 }).ToArray();

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(training, validation, Start.AddDays(20));

        Assert.Equal(ConditionalReturnWalkForwardTrainer.Rejected, result.Status);
        Assert.NotNull(result.Model);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation.ExpectedNetReturnPercent > 0);
        Assert.Contains("VALIDATION_BRIER_NOT_BETTER_THAN_NO_SKILL", result.Limitations);
    }

    [Fact]
    public void 손실이_없는_검증은_JSON_안전한_상태값으로_수익계수를_표현한다()
    {
        var training = SeparableRows("train", 60, Start);
        var validation = SeparableRows("validation", 20, Start.AddDays(10))
            .Select(x => x with { NetReturnPercent = 1.2 }).ToArray();

        var result = ConditionalReturnWalkForwardTrainer.Evaluate(training, validation, Start.AddDays(20));

        Assert.True(result.Status == ConditionalReturnWalkForwardTrainer.Validated,
            $"{string.Join(',', result.Limitations)}; brier={result.Validation?.Calibration.Overall.Brier}; ece={result.Validation?.Calibration.Overall.Ece}; noSkill={result.Validation?.NoSkillBrier}");
        Assert.NotNull(result.Validation);
        Assert.Null(result.Validation.ProfitFactor);
        Assert.Equal("no-losses", result.Validation.ProfitFactorStatus);
        Assert.True(result.Validation.NoSkillBrier > result.Validation.Calibration.Overall.Brier);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Validation,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("profitFactor").ValueKind);
        Assert.Equal("no-losses", json.RootElement.GetProperty("profitFactorStatus").GetString());
        Assert.True(json.RootElement.GetProperty("noSkillBrier").GetDouble() > 0);
        Assert.True(json.RootElement.GetProperty("brierSkill").GetDouble() > 0);
    }

    [Fact]
    public void Brier_skill이_있어도_ECE_사전정책을_넘으면_모델을_거부한다()
    {
        var result = ConditionalReturnWalkForwardTrainer.Evaluate(SeparableRows("train", 60, Start),
            EceFailureRows("validation", 40, Start.AddDays(10)), Start.AddDays(20));

        Assert.Equal(ConditionalReturnWalkForwardTrainer.Rejected, result.Status);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation.BrierSkill > 0);
        Assert.True(result.Validation.Calibration.Overall.Ece > .10);
        Assert.Contains("VALIDATION_ECE_ABOVE_POLICY:0.10", result.Limitations);
        Assert.DoesNotContain("VALIDATION_BRIER_NOT_BETTER_THAN_NO_SKILL", result.Limitations);
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

    static ConditionalReturnModelSample[] SeparableRows(string prefix, int count, DateTimeOffset start) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var signal = start.AddMinutes(i * 2);
            var target = i % 2 == 0;
            return Sample($"{prefix}-{i}", signal, target, target ? .9 : .1);
        }).ToArray();

    static ConditionalReturnModelSample[] EceFailureRows(string prefix, int count, DateTimeOffset start) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var highSignal = i % 2 == 0;
            var target = highSignal ? i % 8 != 6 : i % 8 == 1;
            return Sample($"{prefix}-{i}", start.AddMinutes(i * 2), target, highSignal ? .9 : .1);
        }).ToArray();

    static ConditionalReturnModelSample Sample(string id, DateTimeOffset signal, bool target, double quality) =>
        new(id, "SOXL", signal, signal.AddSeconds(30), signal.AddMinutes(1), Features(signal, quality), target,
            target ? 1.2 : -.45);

    static ConditionalReturnForecastInput Features(DateTimeOffset signal, double quality) =>
        new(signal, TradeSide.Long, quality, .4 + quality / 4, quality * 2 - 1,
            quality > .7 ? VolatilityBand.High : quality < .35 ? VolatilityBand.Low : VolatilityBand.Normal,
            1.2, .6, .1, .05);
}
