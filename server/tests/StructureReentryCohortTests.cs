using System.Text.Json;
using Astra.Server;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;
using Xunit;

public sealed class StructureReentryCohortTests
{
    static readonly StructurePolicy NoCooldown = StructurePolicy.Default with { StopReentryCooldownBars = 0 };
    static readonly DateTimeOffset Open = new(2026, 9, 11, 9, 30, 0, TimeSpan.FromHours(-4));
    static readonly DateTimeOffset Close = new(2026, 9, 11, 16, 0, 0, TimeSpan.FromHours(-4));
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    const string Intc = "INTC";

    static DateTimeOffset At(int minute) => Open.AddMinutes(minute);

    static StructuralTradePlan Plan()
    {
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), StructurePolicy.Default);
        Assert.True(evaluation.Viable);
        return evaluation.Plan!;
    }

    static StructuralEntryRequest Request(string eventId, int triggerMinute, bool withBars = true,
        IReadOnlyList<string>? aliases = null, DateTimeOffset? sessionStart = null) =>
        new(Intc, At(triggerMinute), At(triggerMinute).AddMinutes(1), Close,
            StructuralSimulation.Freeze(Plan(), eventId, "UP", 41.0, 55.5, At(triggerMinute), At(triggerMinute)),
            withBars ? Enumerable.Range(0, triggerMinute + 1).Select(At).ToArray() : null,
            sessionStart ?? Open, aliases);

    static SimTrade Stopped(SimTrade trade, int exitMinute = 30) =>
        trade with { Status = "STOP", ExitPrice = trade.Stop, ExitAt = At(exitMinute).AddSeconds(20), PnlPercent = -1.1 };

    static SimTrade FirstEntry(string eventId = "INTC|e1", int triggerMinute = 20) =>
        StructuralSimulation.Enter([], Request(eventId, triggerMinute), NoCooldown).Trade!;

    [Fact]
    public void TheIntcSecondEntryIsTaggedAsAStopReentryIntoTheSameTargetZone()
    {
        var stopped = Stopped(FirstEntry());

        var second = StructuralSimulation.Enter([stopped], Request("INTC|e2", 31), NoCooldown);

        Assert.Equal(StructuralEntryOutcome.Entered, second.Outcome);
        var tags = second.Trade!.Structure!.Reentry!;
        Assert.Equal(1, tags.SameSymbolWithinBars);
        Assert.Equal("STOP", tags.PrevExitStatus);
        Assert.True(tags.SameTargetZone);
        Assert.Equal(55.5, tags.PrevEntryQuality);
        Assert.Equal("UP", tags.PrevTrend);
    }

    [Fact]
    public void TheStopBarIsBarZeroSoAnEntryOnThatBarIsTaggedZero()
    {
        var stopped = Stopped(FirstEntry(), 30);

        var second = StructuralSimulation.Enter([stopped], Request("INTC|e2", 30), NoCooldown);

        Assert.Equal(0, second.Trade!.Structure!.Reentry!.SameSymbolWithinBars);
    }

    [Fact]
    public void AFirstEntryLeavesEveryReentryTagNull()
    {
        var trade = FirstEntry();

        var tags = trade.Structure!.Reentry!;
        Assert.Null(tags.SameSymbolWithinBars);
        Assert.Null(tags.PrevExitStatus);
        Assert.Null(tags.SameTargetZone);
        Assert.Null(tags.PrevEntryQuality);
        Assert.Null(tags.PrevTrend);
    }

    [Fact]
    public void ADifferentTargetZoneIsTaggedFalseUnlessItIsTheSameLineage()
    {
        var first = FirstEntry();
        var moved = Stopped(first with
        {
            Structure = first.Structure! with
            {
                PlanSnapshot = first.Structure!.PlanSnapshot with { TargetZoneId = "z-merged-away" }
            }
        });

        var withoutLineage = StructuralSimulation.Enter([moved], Request("INTC|e2", 31), NoCooldown);
        var withLineage = StructuralSimulation.Enter([moved],
            Request("INTC|e3", 31, aliases: ["z-merged-away"]), NoCooldown);

        Assert.False(withoutLineage.Trade!.Structure!.Reentry!.SameTargetZone);
        Assert.True(withLineage.Trade!.Structure!.Reentry!.SameTargetZone);
    }

    [Fact]
    public void WithoutCompletedBarEvidenceTheBarCountStaysNullWhileTheExitReasonIsKept()
    {
        var stopped = Stopped(FirstEntry());

        var second = StructuralSimulation.Enter([stopped], Request("INTC|e2", 31, withBars: false), NoCooldown);

        var tags = second.Trade!.Structure!.Reentry!;
        Assert.Null(tags.SameSymbolWithinBars);
        Assert.Equal("STOP", tags.PrevExitStatus);
    }

    [Fact]
    public void AnExitFromAnEarlierSessionIsNotCountedInThisSessionsBars()
    {
        var stopped = Stopped(FirstEntry()) with { ExitAt = At(30).AddDays(-1) };

        var second = StructuralSimulation.Enter([stopped], Request("INTC|e2", 31), NoCooldown);

        var tags = second.Trade!.Structure!.Reentry!;
        Assert.Null(tags.SameSymbolWithinBars);
        Assert.Equal("STOP", tags.PrevExitStatus);
    }

    [Fact]
    public void ALegacyTradeWithoutTheTagRoundTripsAsNullAndTaggedTradesSurviveSerialization()
    {
        var legacy = JsonSerializer.Deserialize<SimTrade>(
            """
            {"id":"legacy-1","symbol":"INTC","kind":"PULLBACK","enteredAt":"2026-09-11T09:50:00-04:00",
             "entryPrice":100,"target":101.5,"stop":99.4,"targetBasis":null,"stopBasis":null,"status":"STOP",
             "exitPrice":99.4,"exitAt":"2026-09-11T10:00:00-04:00","pnlPercent":-0.8,"lastPrice":99.4,
             "logic":"v5-structure.1",
             "structure":{"entryEventId":"INTC|legacy","planSnapshot":{"planId":"p1","kind":"PULLBACK",
               "entryReference":100,"invalidationAnchor":99.6,"stop":99.4,"target":101.5,
               "invalidationZoneId":"z-s","invalidationLower":99.4,"invalidationUpper":99.7,
               "targetZoneId":"z-r","targetLower":101.5,"targetUpper":101.9,"buffer":0.05,
               "bufferBasis":"session-atr","frontRunBuffer":0.02,"netReward":1.3,"netRisk":0.8,"netR":1.6,
               "riskPercent":0.6,"feePerShare":0.1,"extraCostPerShare":0.02,"validSpread":0.04,
               "missingLiquidity":false,"eligibilityCostModelVersion":"cost-eligibility.1",
               "realizedFillCostModelVersion":"cost-fill.1","createdAt":"2026-09-11T09:49:00-04:00",
               "expiresAt":"2026-09-11T09:55:00-04:00","engineVersion":"v5-structure.1","policyHash":"hash-A",
               "reasonCodes":[],"explanation":"legacy"},"trendAtEntry":"UP","signedTrendAtEntry":40,
               "entryQualityAtEntry":60,"analysisAsOf":"2026-09-11T09:50:00-04:00",
               "quoteAt":"2026-09-11T09:50:00-04:00","structuralExitPolicyVersion":"v5-exit.frozen-plan.1"}}
            """, Json)!;

        Assert.Null(legacy.Structure!.Reentry);

        var tagged = StructuralSimulation.Enter([legacy], Request("INTC|e2", 31), NoCooldown).Trade!;
        var restored = JsonSerializer.Deserialize<SimTrade>(JsonSerializer.Serialize(tagged, Json), Json)!;

        Assert.Equal(tagged.Structure!.Reentry, restored.Structure!.Reentry);
        Assert.Equal("STOP", restored.Structure!.Reentry!.PrevExitStatus);
    }

    [Fact]
    public void TheCohortReportSeparatesFirstEntriesReentriesAndUntaggedTrades()
    {
        var first = FirstEntry();
        var stopped = Stopped(first);
        var reentry = StructuralSimulation.Enter([stopped], Request("INTC|e2", 31), NoCooldown).Trade!;
        var untagged = first with
        {
            Id = "untagged-1", Structure = first.Structure! with { Reentry = null }, Status = "TARGET",
            ExitAt = At(45), PnlPercent = 1.4
        };

        var report = SimulationCohorts.Build([stopped, reentry, untagged]);

        var window = Assert.Single(report.Groups, g => g.Dimension == "reentry");
        Assert.Equal(1, Assert.Single(window.Cohorts, c => c.Key == SimulationCohorts.FirstEntryKey).Stats.Total);
        Assert.Equal(1, Assert.Single(window.Cohorts, c => c.Key == "R0_2").Stats.Total);
        var missing = Assert.Single(window.Cohorts, c => c.Key == SimulationCohorts.ReentryUntaggedKey);
        Assert.Equal(1, missing.Stats.Total);
        Assert.False(missing.Collected);

        var zones = Assert.Single(report.Groups, g => g.Dimension == "reentryTargetZone");
        Assert.Equal(1, Assert.Single(zones.Cohorts, c => c.Key == SimulationCohorts.SameTargetZoneKey).Stats.Total);

        var exits = Assert.Single(report.Groups, g => g.Dimension == "reentryPrevExit");
        Assert.Equal(1, Assert.Single(exits.Cohorts, c => c.Key == "PREV_STOP").Stats.Total);
        Assert.Equal(3, window.Cohorts.Sum(c => c.Stats.Total));
    }

    [Fact]
    public void TheValidationReportCountsSamePollSuppressionAndStopCooldownSeparately()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("e1", rejections: [EntryBlockCodes.SuppressedBySamePollExit])]),
            Vx.Row("obs-2", [Vx.Candidate("e2", rejections: [EntryBlockCodes.BlockedByStopCooldown])], minute: 42),
            Vx.Row("obs-3", [Vx.Candidate("e3", rejections: [EntryBlockCodes.BlockedByStopCooldown])], minute: 43,
                symbol: "AMD")
        };

        var evaluation = ValidationEvaluator.Evaluate(Vx.Link(rows, []).Candidates, Vx.AsOf, Vx.Small);

        var suppressed = Assert.Single(evaluation.EntryBlocks,
            x => x.Code == EntryBlockCodes.SuppressedBySamePollExit);
        var cooldown = Assert.Single(evaluation.EntryBlocks, x => x.Code == EntryBlockCodes.BlockedByStopCooldown);
        Assert.Equal(1, suppressed.Events);
        Assert.Equal(1, suppressed.Symbols);
        Assert.Equal(2, cooldown.Events);
        Assert.Equal(2, cooldown.Symbols);
    }
}
