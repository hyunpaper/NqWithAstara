using Astra.Server.Domain.Confluence;
using Xunit;

/// <summary>C4 합산 정규화·상관군·결측 제외·가중치 주입 계약 (C4, #167).</summary>
public sealed class ConfluenceAggregatorTests
{
    static readonly ConfluencePolicy Policy = ConfluencePolicy.Default;

    static ConfluenceScore Aggregate(IReadOnlyList<TechniqueSignal> signals,
        IReadOnlyDictionary<string, double>? weights = null, string? version = null) =>
        ConfluenceAggregator.Aggregate(Cf.Symbol, Cf.SessionStart, signals, Policy, weights, version);

    [Fact]
    public void ScoreIsTheConfidenceWeightedMeanOfTheContributingSignals()
    {
        var score = Aggregate([
            Cf.Signal(TechniqueNames.Macd, 1.0, 1.0),
            Cf.Signal(TechniqueNames.VwapDeviation, -.5, .5)
        ]);
        Assert.Equal((1.0 * 1.0 + .5 * -.5) / (1.0 + .5), score.Score!.Value, 6);
        Assert.Equal(0, score.WarmupCount);
        Assert.All(score.Contributing, x => Assert.True(x.Contributing));
    }

    [Fact]
    public void ScoreStaysInsideTheUnitRangeWhenEveryTechniqueAgrees()
    {
        var signals = TechniqueNames.All.Select(x => Cf.Signal(x, 1.0, 1.0)).ToArray();
        Assert.Equal(1.0, Aggregate(signals).Score!.Value, 6);

        var opposite = TechniqueNames.All.Select(x => Cf.Signal(x, -1.0, 1.0)).ToArray();
        Assert.Equal(-1.0, Aggregate(opposite).Score!.Value, 6);
    }

    [Fact]
    public void WarmupAndZeroConfidenceSignalsLeaveTheDenominator()
    {
        var score = Aggregate([
            Cf.Signal(TechniqueNames.Macd, 1.0, 1.0),
            Cf.Signal(TechniqueNames.Rsi, -1.0, 0, warmup: true),
            TechniqueSignal.Missing(TechniqueNames.OrderBookImbalance)
        ]);
        Assert.Equal(1.0, score.Score!.Value, 6);
        Assert.Equal(1, score.WarmupCount);
        Assert.Single(score.Contributing.Where(x => x.Contributing));
    }

    [Fact]
    public void ScoreIsNullWhenNoTechniqueContributes()
    {
        var score = Aggregate([
            Cf.Signal(TechniqueNames.Macd, 1.0, 0, warmup: true),
            TechniqueSignal.Missing(TechniqueNames.RelativeStrength)
        ]);
        Assert.Null(score.Score);
        Assert.Equal(1, score.WarmupCount);
    }

    [Fact]
    public void CorrelatedTechniquesShareOneGroupWeight()
    {
        var grouped = Aggregate([
            Cf.Signal(TechniqueNames.Rsi, 1.0, 1.0),
            Cf.Signal("STOCH", 1.0, 1.0),
            Cf.Signal(TechniqueNames.Macd, -1.0, 1.0)
        ]);
        Assert.Equal(.5, grouped.Contributing.Single(x => x.Name == TechniqueNames.Rsi).Weight);
        Assert.Equal(.5, grouped.Contributing.Single(x => x.Name == "STOCH").Weight);
        Assert.Equal(1.0, grouped.Contributing.Single(x => x.Name == TechniqueNames.Macd).Weight);
        Assert.Equal(0, grouped.Score!.Value, 6);
    }

    [Fact]
    public void TheFirstCohortLeavesEveryCorrelationGroupWithASingleMember()
    {
        var score = Aggregate(TechniqueNames.All.Select(x => Cf.Signal(x, 1.0, 1.0)).ToArray());
        Assert.All(score.Contributing, x => Assert.Equal(1.0, x.Weight));
        Assert.Equal("oscillator", score.Contributing.Single(x => x.Name == TechniqueNames.Rsi).CorrelationGroup);
        Assert.Equal("range", score.Contributing.Single(x => x.Name == TechniqueNames.OpeningRange).CorrelationGroup);
        Assert.Null(score.Contributing.Single(x => x.Name == TechniqueNames.Macd).CorrelationGroup);
    }

    [Fact]
    public void InjectedWeightsChangeTheMeanAndTravelWithTheScore()
    {
        var signals = new[]
        {
            Cf.Signal(TechniqueNames.Macd, 1.0, 1.0),
            Cf.Signal(TechniqueNames.VwapDeviation, -1.0, 1.0)
        };
        Assert.Equal(0, Aggregate(signals).Score!.Value, 6);

        var weighted = Aggregate(signals,
            new Dictionary<string, double> { [TechniqueNames.Macd] = 3.0 }, "measured.2026-09-12");
        Assert.Equal((3.0 - 1.0) / 4.0, weighted.Score!.Value, 6);
        Assert.Equal("measured.2026-09-12", weighted.WeightsVersion);
        Assert.Equal(3.0, weighted.Contributing.Single(x => x.Name == TechniqueNames.Macd).Weight);
    }

    [Fact]
    public void DefaultWeightsAreUniformAndVersioned()
    {
        var score = Aggregate([Cf.Signal(TechniqueNames.Macd, .4, 1.0)]);
        Assert.Equal(ConfluenceAggregator.DefaultWeightsVersion, score.WeightsVersion);
        Assert.Equal(ConfluenceAggregator.DefaultWeight, score.Contributing.Single().Weight);
        Assert.Equal(Policy.PolicyHash, score.PolicyHash);
    }

    [Fact]
    public void InvalidWeightsFallBackToTheUniformDefault()
    {
        var weights = new Dictionary<string, double>
        {
            [TechniqueNames.Macd] = double.NaN,
            [TechniqueNames.Rsi] = -1
        };
        var score = Aggregate([Cf.Signal(TechniqueNames.Macd, 1.0, 1.0), Cf.Signal(TechniqueNames.Rsi, 1.0, 1.0)],
            weights);
        Assert.All(score.Contributing, x => Assert.Equal(ConfluenceAggregator.DefaultWeight, x.Weight));
    }

    [Fact]
    public void EveryTechniqueStaysVisibleEvenWhenItDoesNotContribute()
    {
        var input = Cf.Input(Cf.Ramp(40, 100, .2), relativeVolume: 1.5);
        var signals = ConfluenceTechniques.Evaluate(input, Policy);
        var score = ConfluenceAggregator.Aggregate(Cf.Symbol, input.Bars[^1].End, signals, Policy);
        Assert.Equal(TechniqueNames.All.Length, score.Contributing.Length);
        Assert.Equal(TechniqueNames.All, score.Contributing.Select(x => x.Name));
        Assert.Contains(score.Contributing, x => !x.Contributing);
        Assert.NotNull(score.Score);
    }
}
