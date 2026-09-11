using Astra.Server;
using Astra.Server.Domain;
using Xunit;

public sealed class SimulationEngineV4Tests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-09T13:30:00-04:00");
    static SimTrade Open(DateTimeOffset? entered = null, DateTimeOffset? cursor = null, DateTimeOffset? end = null)
        => new("id", "NVDA", "SETUP", entered ?? T, 100, 110, 95, null, null, "OPEN", null, null, null, 100,
            LastEvaluatedBarAt: cursor, SessionEnd: end, LastPriceAt: entered ?? T);
    static Candle Bar(int minute, double open, double high, double low, double close)
        => new(T.AddMinutes(minute), open, high, low, close, 1000);

    [Fact]
    public void ReplaysEveryUnprocessedBarChronologicallyAndStopsAtFirstExit()
    {
        var bars = new[] { Bar(1, 100, 101, 99, 100), Bar(2, 100, 111, 99, 110), Bar(3, 100, 101, 94, 96) };
        var result = SimulationEngine.Process([Open()], "NVDA", bars, 96, T.AddMinutes(4), 50, 100, []);
        Assert.Equal("TARGET", result[0].Status); // an earlier target cannot be overwritten by the latest stop
        Assert.Equal(110, result[0].ExitPrice);
        Assert.Equal(T.AddMinutes(2), result[0].LastEvaluatedBarAt);
    }

    [Fact]
    public void EarlierStopWinsEvenWhenLatestBarTouchesTarget()
    {
        var bars = new[] { Bar(1, 100, 101, 94, 95), Bar(2, 100, 111, 99, 110) };
        var result = SimulationEngine.Process([Open()], "NVDA", bars, 110, T.AddMinutes(3), 50, 100, []);
        Assert.Equal("STOP", result[0].Status);
    }

    [Fact]
    public void ExcludesEntryMinuteWhenEntryWasInsideItButIncludesExactBoundary()
    {
        var dangerous = Bar(0, 100, 120, 80, 100);
        var inside = SimulationEngine.Process([Open(T.AddSeconds(30))], "NVDA", [dangerous], 100, T.AddMinutes(1), 50, 100, []);
        var boundary = SimulationEngine.Process([Open(T)], "NVDA", [dangerous], 100, T.AddMinutes(1), 50, 100, []);
        Assert.Equal("OPEN", inside[0].Status);
        Assert.Equal("STOP", boundary[0].Status);
    }

    [Fact]
    public void EntryMinuteStartsUnobservedAndApostEntryQuoteMarksOnlyPartialCoverage()
    {
        var entered = T.AddSeconds(3);
        var created = SimulationEngine.Process([], "NVDA", [], 100, entered, 50, 100,
            [new SimulationEntry("SETUP", 100, 110, 95, null, null, 70, 0, 2, 60, 55, [], entered, T.AddHours(3), T)]);
        Assert.Equal("UNOBSERVED", Assert.IsType<ExecutionProvenance>(created[0].Execution).EntryMinuteCoverage);

        var observed = SimulationEngine.Process(created, "NVDA", [], 101, T.AddSeconds(17), 50, 100, []);
        var execution = Assert.IsType<ExecutionProvenance>(observed[0].Execution);
        Assert.Equal("PARTIALLY_OBSERVED_QUOTE", execution.EntryMinuteCoverage);
        Assert.Equal(T.AddSeconds(17), execution.EntryMinuteEvidenceAt);
    }

    [Fact]
    public void CompletedBarKeepsCursorAtStartButRecordsCloseKnownTime()
    {
        var trade = Open() with { Execution = new ExecutionProvenance(T, T.AddMinutes(1), "UNOBSERVED", null) };
        var result = SimulationEngine.Process([trade], "NVDA", [Bar(1, 100, 101, 99, 101)], 101, T.AddMinutes(2), 50, 100, []);
        Assert.Equal(T.AddMinutes(1), result[0].LastEvaluatedBarAt);
        Assert.Equal(T.AddMinutes(2), result[0].LastPriceAt);
        var execution = Assert.IsType<ExecutionProvenance>(result[0].Execution);
        Assert.Equal(T.AddMinutes(1), execution.EvaluatedBarStart);
        Assert.Equal(T.AddMinutes(2), execution.EvaluatedBarCloseAt);
    }

    [Fact]
    public void ReorderedAndRepeatedBarsRemainIdempotentWithReplayEvidence()
    {
        var trade = Open() with { Execution = new ExecutionProvenance(T, T.AddMinutes(1), "UNOBSERVED", null) };
        var first = SimulationEngine.ReplayBars([trade], "NVDA", [Bar(2, 100, 101, 99, 101), Bar(1, 100, 101, 99, 101)]);
        var restarted = SimulationEngine.ReplayBars(first, "NVDA", [Bar(1, 100, 101, 99, 101), Bar(2, 100, 101, 99, 101)]);
        Assert.Equal(T.AddMinutes(2), restarted[0].LastEvaluatedBarAt);
        Assert.Equal(T.AddMinutes(3), Assert.IsType<ExecutionProvenance>(restarted[0].Execution).EvaluatedBarCloseAt);
    }

    [Fact]
    public void GapBelowStopUsesWorseOpeningPrice()
    {
        var result = SimulationEngine.Process([Open()], "NVDA", [Bar(1, 90, 92, 88, 91)], 91, T.AddMinutes(2), 50, 100, []);
        Assert.Equal(90, result[0].ExitPrice);
    }

    [Fact]
    public void DoesNotReuseBarsBeforeEntryOrAtPersistedCursor()
    {
        var trade = Open(T.AddMinutes(2), T.AddMinutes(2));
        var result = SimulationEngine.Process([trade], "NVDA", [Bar(1, 100, 120, 80, 100), Bar(2, 100, 120, 80, 100)], 100, T.AddMinutes(3), 50, 100, []);
        Assert.Equal("OPEN", result[0].Status);
    }

    [Fact]
    public void RetentionNeverEvictsOpenTrades()
    {
        var source = Enumerable.Range(0, 501).Select(i => Open() with { Id = $"o{i}", Symbol = $"S{i}" }).ToList();
        var result = SimulationEngine.Process(source, "OTHER", [], 1, T, 50, 1, []);
        Assert.Equal(501, result.Count);
        Assert.All(result, x => Assert.Equal("OPEN", x.Status));
    }

    [Fact]
    public void RestartClosesTradeAtPersistedSessionEndWithEstimatedProvenance()
    {
        var end = T.AddHours(2);
        var result = SimulationEngine.CloseExpiredSessions([Open(end: end) with { LastPrice = 103, Execution = new ExecutionProvenance(T, T.AddMinutes(1), "UNOBSERVED", null) }], end.AddMinutes(1));
        Assert.Equal("EOD", result[0].Status);
        Assert.Equal(end, result[0].ExitAt);
        Assert.True(result[0].ExitEstimated);
        Assert.Equal(T, result[0].LastPriceAt); // session end is not fabricated as a market observation
        var execution = Assert.IsType<ExecutionProvenance>(result[0].Execution);
        Assert.Equal("EOD_LAST_PRICE_FALLBACK", execution.ExitSource);
        Assert.Equal(T, execution.ExitEvidenceAt);
    }

    [Fact]
    public void LegacyRowsWithoutExecutionRemainCompatible()
    {
        var result = SimulationEngine.Process([Open()], "NVDA", [Bar(1, 100, 101, 99, 101)], 101, T.AddMinutes(2), 50, 100, []);
        Assert.Null(result[0].Execution);
        Assert.Equal(T.AddMinutes(2), result[0].LastPriceAt);
    }

    [Fact]
    public void FinalSessionBarIsReplayedBeforeEstimatedEodFallback()
    {
        var end = T.AddMinutes(3);
        var replayed = SimulationEngine.ReplayBars([Open(end: end)], "NVDA", [Bar(2, 100, 111, 99, 110)]);
        var closed = SimulationEngine.CloseExpiredSessions(replayed, end.AddMinutes(1));
        Assert.Equal("TARGET", closed[0].Status);
        Assert.False(closed[0].ExitEstimated);
    }

    [Fact]
    public void LegacyTradeDoesNotExpireAtUtcMidnightDuringSameEasternSession()
    {
        var entered = DateTimeOffset.Parse("2026-09-09T23:30:00Z"); // 19:30 EDT is synthetic but same Eastern date
        var trade = Open(entered);
        var result = SimulationEngine.CloseExpiredSessions([trade], DateTimeOffset.Parse("2026-09-10T00:01:00Z"));
        Assert.Equal("OPEN", result[0].Status);
    }

    [Fact]
    public void SameTriggerBarCannotCreateDuplicateAfterRestart()
    {
        var entry = new SimulationEntry("SETUP", 100, 110, 95, null, null, 70, 0, 2, 60, 55, [], T, T.AddHours(3), T.AddMinutes(-1));
        var closed = Open() with { Status = "STOP", TriggerBarAt = entry.TriggerBarAt };
        var result = SimulationEngine.Process([closed], "NVDA", [], 100, T, 50, 100, [entry]);
        Assert.Single(result);
    }

    // --- Issue #45: gap-up target must not be overridden by a later intrabar stop touch ---

    [Fact]
    public void IssueReproductionFixtureEntry100Stop99Target104ResolvesToTarget()
    {
        // Exact repro from issue #45: Entry 100 / Stop 99 / Target 104. O105 H106 L98 C100.
        // Previously reported STOP@99; expected TARGET@104 since the open already cleared target.
        var trade = new SimTrade("id", "NVDA", "SETUP", T, 100, 104, 99, null, null, "OPEN", null, null, null, 100);
        var result = SimulationEngine.Process([trade], "NVDA", [Bar(1, 105, 106, 98, 100)], 100, T.AddMinutes(2), 50, 100, []);
        Assert.Equal("TARGET", result[0].Status);
        Assert.Equal(104, result[0].ExitPrice);
    }

    [Fact]
    public void GapAboveTargetFillsTargetEvenWhenTheSameBarLaterTouchesStop()
    {
        // Entry 100 / Stop 95 / Target 110. O111 H112 L94 C100 — open already clears target,
        // so the target order is treated as filled before the bar's own low reaches stop.
        var result = SimulationEngine.Process([Open()], "NVDA", [Bar(1, 111, 112, 94, 100)], 100, T.AddMinutes(2), 50, 100, []);
        Assert.Equal("TARGET", result[0].Status);
        Assert.Equal(110, result[0].ExitPrice); // conservative fill at Target, not the more favorable Open
    }

    [Fact]
    public void GapBelowStopStillWinsOverAnyLaterTargetTouch()
    {
        // Existing gap-down behavior must be untouched by the new gap-up target check.
        var result = SimulationEngine.Process([Open()], "NVDA", [Bar(1, 90, 111, 88, 91)], 91, T.AddMinutes(2), 50, 100, []);
        Assert.Equal("STOP", result[0].Status);
        Assert.Equal(90, result[0].ExitPrice);
    }

    [Fact]
    public void MidRangeOpenTouchingBothLevelsStillPrefersStopFirst()
    {
        // Open is strictly between Stop and Target; order of intrabar touches is unknowable,
        // so the pre-existing stop-first tie-break must be preserved (regression pin).
        var result = SimulationEngine.Process([Open()], "NVDA", [Bar(1, 100, 111, 94, 100)], 100, T.AddMinutes(2), 50, 100, []);
        Assert.Equal("STOP", result[0].Status);
        Assert.Equal(95, result[0].ExitPrice);
    }

    [Fact]
    public void GapAboveTargetFillsTargetWhenLowNeverReachesStop()
    {
        // Confirms the pre-existing gap-up-to-target behavior is unchanged when stop is never touched.
        var result = SimulationEngine.Process([Open()], "NVDA", [Bar(1, 111, 112, 105, 108)], 108, T.AddMinutes(2), 50, 100, []);
        Assert.Equal("TARGET", result[0].Status);
        Assert.Equal(110, result[0].ExitPrice);
    }

    [Fact]
    public void GapAboveTargetAlsoAppliesToFrozenV5StructuralTrades()
    {
        // Same gap-up-then-stop-touch fixture as above, but on a v5 structure-owned trade
        // (Logic prefix "v5-structure.*"), confirming EvaluateBar's order applies uniformly.
        var v5Trade = Open() with { Logic = "v5-structure.1", Kind = "BREAKOUT" };
        var result = SimulationEngine.Process([v5Trade], "NVDA", [Bar(1, 111, 112, 94, 100)], 100, T.AddMinutes(2), 50, 100, []);
        Assert.Equal("TARGET", result[0].Status);
        Assert.Equal(110, result[0].ExitPrice);
    }
}
