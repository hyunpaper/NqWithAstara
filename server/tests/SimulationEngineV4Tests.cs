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
        var result = SimulationEngine.CloseExpiredSessions([Open(end: end) with { LastPrice = 103 }], end.AddMinutes(1));
        Assert.Equal("EOD", result[0].Status);
        Assert.Equal(end, result[0].ExitAt);
        Assert.True(result[0].ExitEstimated);
        Assert.Equal(T, result[0].LastPriceAt); // session end is not fabricated as a market observation
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
}
