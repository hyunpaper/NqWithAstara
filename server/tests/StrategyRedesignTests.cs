using Astra.Server;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class StrategyRedesignTests
{
    [Fact]
    public void 숏계획은_저항을손절로_지지를목표로_사용한다()
    {
        var resistance = D2.Resistance(100.60m, 100.80m, id: "short-resistance");
        var support = D2.Support(97.00m, 97.50m, id: "short-support");
        var request = new PlanRequest("TEST", "short-event", "BREAKOUT", 100m, 100.80m,
            resistance, [support, resistance], .20, .02m, Fx.At(40), Fx.At(45), true,
            TradeSide.Short);

        var result = StructuralPlanner.Evaluate(request, D2.WideNetR with { ShortBorrowCostPercent = .10 });

        Assert.True(result.Viable, string.Join(',', result.ReasonCodes));
        Assert.Equal(TradeSide.Short, result.Plan!.Side);
        Assert.True(result.Plan.Stop > result.Plan.EntryReference);
        Assert.True(result.Plan.Target < result.Plan.EntryReference);
        Assert.True(result.Plan.NetReward > 0);
    }

    [Fact]
    public void 숏청산은_손절과목표를_롱과대칭으로_판정한다()
    {
        var trade = new SimTrade("short", "TEST", "BREAKOUT", Fx.At(40), 100, 95, 105,
            null, null, "OPEN", null, null, null, 100, Side: TradeSide.Short);

        var stopped = SimulationEngine.ReplayBars([trade], "TEST",
            [new Candle(Fx.At(41), 100, 106, 99, 104, 1)]);
        Assert.Equal("STOP", Assert.Single(stopped).Status);
        Assert.Equal(-5.2, stopped[0].PnlPercent);

        var targetTrade = trade with { Id = "short-target" };
        var targeted = SimulationEngine.ReplayBars([targetTrade], "TEST",
            [new Candle(Fx.At(41), 100, 101, 94, 96, 1)]);
        Assert.Equal("TARGET", Assert.Single(targeted).Status);
        Assert.Equal(4.8, targeted[0].PnlPercent);
    }

    [Fact]
    public void 숏은_차입비용이_없으면_shadow계획으로도_승격하지_않는다()
    {
        var resistance = D2.Resistance(100.60m, 100.80m, id: "short-resistance-missing");
        var support = D2.Support(97.00m, 97.50m, id: "short-support-missing");
        var request = new PlanRequest("TEST", "short-missing", "BREAKOUT", 100m, 100.80m,
            resistance, [support, resistance], .20, .02m, Fx.At(40), Fx.At(45), true,
            TradeSide.Short);

        var result = StructuralPlanner.Evaluate(request, D2.WideNetR);

        Assert.False(result.Viable);
        Assert.Contains(StructuralPlanner.ShortBorrowCostMissing, result.ReasonCodes);
    }

    [Fact]
    public void 동일한_완료봉에서_분류한_regime은_결정적이다()
    {
        var trend = D2.Trend(TrendState.Range, 5);
        var bars = Enumerable.Range(1, 30).Select(i => new StructureBar(
            Fx.At(i), Fx.At(i + 1), 100m, 100.20m, 99.80m, 100m, 100)).ToArray();

        var first = StrategyRegimeClassifier.Classify(trend, bars, StructurePolicy.Default);
        var second = StrategyRegimeClassifier.Classify(trend, bars, StructurePolicy.Default);

        Assert.Equal(first, second);
        Assert.Equal(StrategyDirection.Range, first.Direction);
        Assert.Equal(VolatilityBand.Low, first.Volatility);
    }
}
