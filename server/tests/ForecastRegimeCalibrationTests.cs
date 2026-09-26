using Astra.Server;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;
using Xunit;

public sealed class ConditionalReturnForecasterTests
{
    static ConditionalReturnForecastInput Input(DateTimeOffset? asOf = null) => new(asOf ?? Fx.At(40),
        TradeSide.Long, .8, .4, .5, VolatilityBand.Normal, 2, 1, .2, .1);

    static ConditionalReturnModel Model(DateTimeOffset? trainedThrough = null, int samples = 100,
        string status = ConditionalForecastStatus.Calibrated) => new("conditional-v1",
        trainedThrough ?? Fx.At(20), Fx.At(30), 200, samples, 30, status, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void 학습모델이_없으면_확률을_만들지_않는다()
    {
        var result = ConditionalReturnForecaster.Evaluate(Input(), null);

        Assert.Equal(ConditionalForecastStatus.InsufficientData, result.Status);
        Assert.Null(result.SuccessProbability);
        Assert.Null(result.ExpectedValuePercent);
        Assert.Contains(ConditionalReturnForecaster.NoModel, result.Limitations);
        Assert.Contains(result.Evidence, x => x.StartsWith("AS_OF:", StringComparison.Ordinal));
    }

    [Fact]
    public void 신호시점_이후까지_학습한_모델은_거부한다()
    {
        var result = ConditionalReturnForecaster.Evaluate(Input(), Model(Fx.At(41)));

        Assert.Equal(ConditionalForecastStatus.InsufficientData, result.Status);
        Assert.Contains(ConditionalReturnForecaster.ModelAfterSignal, result.Limitations);
    }

    [Fact]
    public void 신호시점_이후_결과로_검증한_모델은_거부한다()
    {
        var model = Model() with { ValidatedThrough = Fx.At(41) };

        var result = ConditionalReturnForecaster.Evaluate(Input(), model);

        Assert.Equal(ConditionalForecastStatus.InsufficientData, result.Status);
        Assert.Contains(ConditionalReturnForecaster.ValidationAfterSignal, result.Limitations);
    }

    [Fact]
    public void 보정된_모델은_비용후_조건부_기대값을_계산한다()
    {
        var result = ConditionalReturnForecaster.Evaluate(Input(), Model());

        Assert.Equal(ConditionalForecastStatus.Calibrated, result.Status);
        Assert.Equal(.5, result.SuccessProbability);
        Assert.Equal(.5, result.StopProbability);
        Assert.Equal(.5, result.TargetProbability);
        Assert.Equal(.5, result.ExpectedGrossReturnPercent);
        Assert.Equal(.2, result.ExpectedNetReturnPercent);
        Assert.Equal(.2, result.ExpectedValuePercent);
        Assert.Equal(100, result.CalibrationSamples);
        Assert.Equal(Fx.At(20), result.ModelTrainedThrough);
        Assert.Equal(Fx.At(30), result.ModelValidatedThrough);
    }

    [Fact]
    public void 표본이_부족한_모델의_수치는_실험상태로_표시한다()
    {
        var result = ConditionalReturnForecaster.Evaluate(Input(), Model(samples: 10));

        Assert.Equal(ConditionalForecastStatus.ExperimentalUncalibrated, result.Status);
        Assert.Equal(.5, result.SuccessProbability);
        Assert.Contains("MODEL_NOT_CALIBRATED", result.Limitations);
    }
}

public sealed class StrategyRegimeAssessmentTests
{
    [Fact]
    public void 완료봉이_부족하면_regime을_추정하지_않는다()
    {
        var bars = Enumerable.Range(0, 5).Select(i => Fx.Steady(i, 99.8m, 100.2m, 100m)).ToArray();

        var result = StrategyRegimeClassifier.Assess(D2.Trend(TrendState.Up, 40), bars,
            StructurePolicy.Default);

        Assert.Equal(StrategyRegimeAssessment.InsufficientData, result.Status);
        Assert.Null(result.Regime);
        Assert.Null(result.Confidence);
        Assert.Contains(result.Limitations, x => x.StartsWith("INSUFFICIENT_COMPLETED_BARS:",
            StringComparison.Ordinal));
    }

    [Fact]
    public void 충분한_완료봉은_방향과_변동성_근거를_함께_반환한다()
    {
        var bars = Enumerable.Range(0, 30).Select(i => Fx.Steady(i, 99.8m, 100.2m, 100m)).ToArray();
        var trend = D2.Trend(TrendState.Up, 40);

        var classified = StrategyRegimeClassifier.Classify(trend, bars, StructurePolicy.Default);
        var result = StrategyRegimeClassifier.Assess(trend, bars, StructurePolicy.Default);

        Assert.Equal(StrategyRegimeAssessment.Available, result.Status);
        Assert.Equal(classified, result.Regime);
        Assert.Equal(.4, result.Confidence);
        Assert.Contains("CONFIDENCE_BASIS:TREND_MAGNITUDE", result.Evidence);
        Assert.Empty(result.Limitations);
    }
}

public sealed class ProbabilityCalibrationEvaluatorTests
{
    static readonly DateTimeOffset AsOf = Fx.At(200);

    [Fact]
    public void 충분한_표본은_Brier_Ece_Wilson과_최근대비_변화를_계산한다()
    {
        var rows = Enumerable.Range(0, 30).Select(i => new ProbabilityObservation($"event-{i}", Fx.At(i),
            Fx.At(i + 1), .7, i < 14)).ToArray();

        var result = ProbabilityCalibrationEvaluator.Evaluate(rows, AsOf);

        Assert.Equal(ProbabilityCalibrationEvaluator.Ok, result.Status);
        Assert.Equal(30, result.Overall.Samples);
        Assert.Equal(14, result.Overall.Successes);
        Assert.Equal(.30333333, result.Overall.Brier);
        Assert.NotNull(result.Overall.Wilson95);
        Assert.Equal(.28, result.Drift.BrierDelta);
        Assert.Equal(.7, result.Drift.EceDelta);
    }

    [Fact]
    public void 중복이벤트와_평가시점_이후_결과는_표본에서_제외한다()
    {
        var rows = new[]
        {
            new ProbabilityObservation("same", Fx.At(1), Fx.At(2), .6, true),
            new ProbabilityObservation("same", Fx.At(1), Fx.At(3), .8, false),
            new ProbabilityObservation("future", Fx.At(2), Fx.At(201), .6, true)
        };

        var result = ProbabilityCalibrationEvaluator.Evaluate(rows, AsOf);

        Assert.Equal(ProbabilityCalibrationEvaluator.InsufficientData, result.Status);
        Assert.Equal(1, result.UsableRows);
        Assert.Equal(1, result.DuplicateEvents);
        Assert.Equal(1, result.FutureRows);
        Assert.Equal(ProbabilityCalibrationEvaluator.InsufficientData, result.Drift.Status);
        Assert.Null(result.Drift.BrierDelta);
    }
}
