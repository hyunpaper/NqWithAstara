using Astra.Server;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureTrailingExitTests
{
    static StructurePolicy Wiring => D6.WiringPolicy;

    static StructuralTradePlan FarTargetPlan()
    {
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), Wiring);
        Assert.True(evaluation.Viable);
        var plan = evaluation.Plan!;
        var risk = plan.EntryReference - plan.Stop;
        return plan with { Target = plan.EntryReference + risk * 10 };
    }

    static SimTrade Open(StructurePolicy policy, StructuralTradePlan plan, string id = "TEST|trail", double? benchmark = null)
    {
        var context = StructuralSimulation.Freeze(plan, id, "UP", 40, 60, Fx.At(40), Fx.At(40), policy);
        var request = new StructuralEntryRequest(Fx.Symbol, Fx.At(39), Fx.At(40), Fx.SessionEnd, context)
            with { BenchmarkReturnPercent = benchmark };
        var result = StructuralSimulation.Enter([], request, policy);
        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
        return result.Trade!;
    }

    static SimTrade Step(SimTrade trade, int minute, double open, double high, double low, double close) =>
        Assert.Single(SimulationEngine.ReplayBars([trade], Fx.Symbol, [Fx.Candle(minute, open, high, low, close)]));

    [Fact]
    public void TrailingFieldsNullKeepsHashAndExitVersionAndBehaviourUnchanged()
    {
        Assert.Null(StructurePolicy.Default.TrailingStopTriggerR);
        Assert.Null(StructurePolicy.Default.TrailingStopDistanceR);
        Assert.Null(StructurePolicy.Default.StructuralTargetExtensionR);
        var canonical = StructurePolicy.Default.CanonicalJson;
        Assert.DoesNotContain("TrailingStopTriggerR", canonical);
        Assert.DoesNotContain("TrailingStopDistanceR", canonical);
        Assert.DoesNotContain("StructuralTargetExtensionR", canonical);

        var plan = FarTargetPlan();
        var trade = Open(Wiring, plan);
        Assert.Equal(StructuralSimulation.ExitPolicyVersion, trade.Structure!.StructuralExitPolicyVersion);
        Assert.Null(trade.Structure.TrailingStopTriggerR);
        Assert.Null(trade.Structure.StructuralTargetExtensionR);

        var oneR = trade.EntryPrice + (trade.EntryPrice - trade.Stop);
        var after = Step(trade, 41, trade.EntryPrice, oneR + 1, trade.Stop + .01, oneR);
        Assert.Equal("OPEN", after.Status);
        Assert.Equal(trade.Stop, after.Stop, 10);
        Assert.Equal(trade.StopBasis, after.StopBasis);
    }

    [Fact]
    public void TrailingStaysInactiveBeforeTheTriggerCloseIsReached()
    {
        var policy = Wiring with { TrailingStopTriggerR = 1.0, TrailingStopDistanceR = 1.0 };
        var trade = Open(policy, FarTargetPlan());
        var risk = trade.EntryPrice - trade.Stop;

        var belowTrigger = trade.EntryPrice + .9 * risk;
        var after = Step(trade, 41, trade.EntryPrice, belowTrigger, trade.Stop + .01, belowTrigger);

        Assert.Equal("OPEN", after.Status);
        Assert.Equal(trade.Stop, after.Stop, 10);
        Assert.Equal(trade.StopBasis, after.StopBasis);
    }

    [Fact]
    public void TrailingArmsAfterTriggerAndFollowsBarHighMinusDistance()
    {
        var policy = Wiring with { TrailingStopTriggerR = 1.0, TrailingStopDistanceR = 1.0 };
        var trade = Open(policy, FarTargetPlan());
        var risk = trade.EntryPrice - trade.Stop;
        var oneR = trade.EntryPrice + risk;

        var high = oneR + .20;
        var armed = Step(trade, 41, trade.EntryPrice, high, trade.Stop + .01, oneR);
        Assert.Equal("OPEN", armed.Status);
        Assert.Equal(high - risk, armed.Stop, 10);
        Assert.True(armed.Stop > trade.Stop);
        Assert.Contains("+trail.", armed.Structure!.StructuralExitPolicyVersion);
    }

    [Fact]
    public void TrailingStopIsMonotonicAndKeepsFollowingAfterCloseDipsBelowTrigger()
    {
        var policy = Wiring with { TrailingStopTriggerR = 1.0, TrailingStopDistanceR = 1.0 };
        var trade = Open(policy, FarTargetPlan());
        var risk = trade.EntryPrice - trade.Stop;
        var oneR = trade.EntryPrice + risk;

        var firstHigh = oneR + .20;
        var armed = Step(trade, 41, trade.EntryPrice, firstHigh, trade.Stop + .01, oneR);
        var raisedStop = armed.Stop;

        var lowerClose = trade.EntryPrice + .5 * risk;
        var held = Step(armed, 42, oneR, oneR, raisedStop + .01, lowerClose);
        Assert.Equal("OPEN", held.Status);
        Assert.Equal(raisedStop, held.Stop, 10);

        var higherHigh = oneR + 1.00;
        var trailed = Step(held, 43, lowerClose, higherHigh, raisedStop + .01, lowerClose);
        Assert.Equal("OPEN", trailed.Status);
        Assert.Equal(higherHigh - risk, trailed.Stop, 10);
        Assert.True(trailed.Stop > raisedStop);
    }

    [Fact]
    public void TargetReleaseSkipsTargetExitAndRunsToStopOrEod()
    {
        var policy = Wiring with { StructuralTargetExtensionR = 0.0 };
        var plan = FarTargetPlan();
        var trade = Open(policy, plan);
        Assert.Equal(0.0, trade.Structure!.StructuralTargetExtensionR);
        Assert.Contains("StructuralTargetExtensionR", policy.CanonicalJson);

        var aboveTarget = trade.Target + 5.0;
        var held = Step(trade, 41, trade.EntryPrice, aboveTarget, trade.Stop + .01, aboveTarget - .5);
        Assert.Equal("OPEN", held.Status);

        var stopped = Step(held, 42, held.Stop + .02, held.Stop + .03, held.Stop - .01, held.Stop - .01);
        Assert.Equal("STOP", stopped.Status);

        var eod = Assert.Single(SimulationEngine.CloseExpiredSessions([trade], Fx.SessionEnd.AddMinutes(1)));
        Assert.Equal("EOD", eod.Status);
    }

    [Fact]
    public void TargetExtensionMovesTargetToEntryPlusExtensionR()
    {
        var policy = Wiring with { StructuralTargetExtensionR = 3.0 };
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), Wiring);
        var basePlan = evaluation.Plan!;
        var risk = basePlan.EntryReference - basePlan.Stop;
        var plan = basePlan with { Target = basePlan.EntryReference + risk };
        var trade = Open(policy, plan);

        var expected = trade.EntryPrice + 3 * (trade.EntryPrice - trade.Stop);
        Assert.Equal(expected, trade.Target, 10);

        var twoR = trade.EntryPrice + 2 * (trade.EntryPrice - trade.Stop);
        var held = Step(trade, 41, trade.EntryPrice, twoR, trade.Stop + .01, twoR - .1);
        Assert.Equal("OPEN", held.Status);
        var hit = Step(held, 42, twoR, expected + .01, twoR - .1, expected);
        Assert.Equal("TARGET", hit.Status);
        Assert.Equal(expected, hit.ExitPrice!.Value, 10);
    }

    [Fact]
    public void TrailingCoexistsWithTheFeeBreakEvenStop()
    {
        var policy = Wiring with
        {
            EnableTwoRFeeBreakEvenStop = true,
            TrailingStopTriggerR = 1.0,
            TrailingStopDistanceR = 1.0
        };
        var plan = FarTargetPlan();
        var trade = Open(policy, plan);
        var risk = trade.EntryPrice - trade.Stop;
        var version = trade.Structure!.StructuralExitPolicyVersion;
        Assert.Equal(StructuralSimulation.TwoRFeeBreakEvenExitPolicyVersion, StructuralSimulation.BaseExitVersion(version));
        Assert.Contains("+trail.", version);

        var oneRHigh = trade.EntryPrice + 1.5 * risk;
        var oneRClose = trade.EntryPrice + risk;
        var afterTrail = Step(trade, 41, trade.EntryPrice, oneRHigh, trade.Stop + .01, oneRClose);
        Assert.Equal(oneRHigh - risk, afterTrail.Stop, 10);

        var twoRHigh = trade.EntryPrice + 2.5 * risk;
        var twoRClose = trade.EntryPrice + 2 * risk;
        var feeBreakEven = trade.EntryPrice + (double)(plan.Costs.FeePerShare + plan.Costs.ExtraCostPerShare);
        var afterBoth = Step(afterTrail, 42, oneRClose, twoRHigh, afterTrail.Stop + .01, twoRClose);
        Assert.Equal("OPEN", afterBoth.Status);
        Assert.Equal(Math.Max(afterTrail.Stop, Math.Max(feeBreakEven, twoRHigh - risk)), afterBoth.Stop, 10);
        Assert.True(afterBoth.Stop >= afterTrail.Stop);
    }
}
