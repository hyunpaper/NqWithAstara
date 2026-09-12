using Astra.Server.Application.Backtest;
using Astra.Server.Domain;
using Xunit;

namespace Astra.Server.Tests;

public sealed class HistoricalStructureTradeReplayTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-08T13:30:00Z");

    [Fact]
    public void EntryCannotPrecedeTheAnalysisThatCreatedThePlan()
    {
        Assert.Equal(Start.AddMinutes(10), HistoricalStructureTradeReplay.EntryTime(
            Start.AddMinutes(9), Start.AddMinutes(10)));
        Assert.Equal(Start.AddMinutes(11), HistoricalStructureTradeReplay.EntryTime(
            Start.AddMinutes(11), Start.AddMinutes(10)));
    }

    [Fact]
    public void FinalReplayOnlyAppliesBarsThatWereNotAlreadyProcessedBeforeEntry()
    {
        var trade = new SimTrade("id", "TSLA", "PULLBACK", Start, 100, 110, 95, null, null, "OPEN",
            null, null, null, 100, SessionEnd: Start.AddMinutes(3), LastPriceAt: Start);
        var trigger = new Candle(Start, 100, 101, 90, 100, 1000);
        var afterEntry = new Candle(Start.AddMinutes(1), 100, 111, 99, 110, 1000);

        var result = HistoricalStructureTradeReplay.ReplayPendingBars([trade], "TSLA",
            [trigger, afterEntry], 1);

        Assert.Equal("TARGET", Assert.Single(result).Status);
        Assert.Equal(afterEntry.Timestamp, result[0].LastEvaluatedBarAt);
    }
}
