using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class WalkForwardExpectedValueTests
{
    [Fact]
    public void 임계값은_훈련_구간에서만_선택하고_검증구간에서_재선택하지_않는다()
    {
        var training = new[]
        {
            new ExpectedValueObservation(Fx.At(1), "A", .2, -.5, true, TradeSide.Long, "RANGE"),
            new ExpectedValueObservation(Fx.At(2), "A", .8, 1.2, true, TradeSide.Long, "TREND_UP"),
            new ExpectedValueObservation(Fx.At(3), "A", .9, 1.0, true, TradeSide.Long, "TREND_UP")
        };
        var selected = WalkForwardExpectedValue.Select(training, [0, .5, .8], minimumRows: 2);
        var validation = WalkForwardExpectedValue.EvaluateValidation(selected, [
            new ExpectedValueObservation(Fx.At(4), "A", .2, 5, true, TradeSide.Long, "RANGE"),
            new ExpectedValueObservation(Fx.At(5), "A", .9, -.4, true, TradeSide.Long, "TREND_UP")
        ]);

        Assert.Equal(.5, selected.Value);
        Assert.Equal(2, selected.TrainingRows);
        Assert.Equal(1, validation.ValidationRows);
        Assert.Equal(-.4, validation.ValidationExpectedNetR);
        Assert.Equal(selected.Value, validation.Value);
    }

    [Fact]
    public void 최소_거래수_미달_임계값은_선택하지_않는다()
    {
        var training = new[]
        {
            new ExpectedValueObservation(Fx.At(1), "A", .9, 2, true, TradeSide.Long, "TREND_UP")
        };

        Assert.Throws<InvalidOperationException>(() => WalkForwardExpectedValue.Select(training, [.8, .9], 2));
    }

    [Fact]
    public void 학습결과를_정책에_배선하면_검증구간에서도_같은_임계값을_쓴다()
    {
        var training = new[]
        {
            new ExpectedValueObservation(Fx.At(1), "A", .2, -.5, true, TradeSide.Long, "RANGE"),
            new ExpectedValueObservation(Fx.At(2), "A", .8, 1.2, true, TradeSide.Long, "TREND_UP")
        };
        var selected = WalkForwardExpectedValue.Select(training, [0, .5], minimumRows: 1);
        var policy = WalkForwardExpectedValue.ApplyToPolicy(StructurePolicy.Default, selected);

        Assert.Equal(selected.Value, policy.ExpectedValueFeatureThreshold);
        Assert.False(WalkForwardExpectedValue.Allows(policy, selected.Value - .01));
        Assert.True(WalkForwardExpectedValue.Allows(policy, selected.Value));
    }
}
