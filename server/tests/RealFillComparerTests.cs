using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RealFillComparerTests
{
    static readonly DateOnly TradingDate = new(2026, 9, 11);
    static readonly DateTimeOffset FilledAt = new(2026, 9, 11, 10, 0, 0, TimeSpan.FromHours(-4));

    static RealFill Buy(decimal price = 100.2m, int minute = 0, string symbol = "TEST") =>
        new(symbol, "BUY", FilledAt.AddMinutes(minute), price, 10m, .1m, "MARKET");

    static RealFill Sell(decimal price, int minute = 30, string symbol = "TEST") =>
        new(symbol, "SELL", FilledAt.AddMinutes(minute), price, 10m, .1m, "LIMIT");

    static RealFillCandidateRow Candidate(string state, double offsetMinutes = 0, string eventId = "E1",
        decimal? entryReference = 100m, double? atr = .2, string symbol = "TEST", string[]? rejections = null) =>
        new(symbol, FilledAt.AddMinutes(offsetMinutes), eventId, "PULLBACK", state, entryReference, atr, "UP", 62,
            rejections ?? []);

    static RealVsV5Report Compare(RealFill[] fills, RealFillCandidateRow[] candidates,
        RealFillOpenTrade[]? trades = null) =>
        RealFillComparer.Compare(TradingDate, fills, candidates, trades ?? []);

    [Fact]
    public void BuyWithReadyCandidateInWindowIsMatched()
    {
        var report = Compare([Buy()], [Candidate(RealFillCandidateStates.Ready, 2)]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(RealFillClasses.Matched, row.Classification);
        Assert.Equal("E1", row.EventId);
        Assert.Equal(1, report.Matched);
        Assert.Equal(100d, report.MatchRatePercent);
    }

    [Fact]
    public void EnteredCandidateIsPreferredOverReadyAsRepresentative()
    {
        var report = Compare([Buy()],
            [Candidate(RealFillCandidateStates.Ready, 1, "E-READY"), Candidate(RealFillCandidateStates.Entered, 3, "E-ENTERED")]);

        var row = Assert.Single(report.Rows);
        Assert.Equal("E-ENTERED", row.EventId);
        Assert.Equal(RealFillCandidateStates.Entered, row.V5State);
    }

    [Fact]
    public void EnteredEventWithoutRealBuyIsEngineOnly()
    {
        var report = Compare([], [Candidate(RealFillCandidateStates.Entered, 0, "E1")]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(RealFillClasses.EngineOnly, row.Classification);
        Assert.Null(row.FillPrice);
        Assert.Equal(1, report.EngineOnly);
        Assert.Equal(0, report.RealBuys);
        Assert.Null(report.MatchRatePercent);
    }

    [Fact]
    public void RepeatedObservationsOfOneEnteredEventCollapseIntoOneEngineOnlyRow()
    {
        var report = Compare([],
            [Candidate(RealFillCandidateStates.Entered, 0, "E1"), Candidate(RealFillCandidateStates.Entered, 1, "E1")]);

        Assert.Equal(1, report.EngineOnly);
    }

    [Fact]
    public void BuyWithoutAnyCandidateIsUserOnly()
    {
        var report = Compare([Buy()], []);

        var row = Assert.Single(report.Rows);
        Assert.Equal(RealFillClasses.UserOnly, row.Classification);
        Assert.Null(row.EventId);
        Assert.Null(row.DeviationAtr);
        Assert.Equal(1, report.UserOnly);
        Assert.Equal(0d, report.MatchRatePercent);
    }

    [Fact]
    public void BuyWithOnlyRejectedCandidateIsUserOnlyAndKeepsTheRejectionCodes()
    {
        var report = Compare([Buy()],
            [Candidate(RealFillCandidateStates.Rejected, 1, rejections: ["QUALITY_BELOW_THRESHOLD"])]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(RealFillClasses.UserOnly, row.Classification);
        Assert.Equal(RealFillCandidateStates.Rejected, row.V5State);
        Assert.Equal(new RealFillRejectionCount("QUALITY_BELOW_THRESHOLD", 1),
            Assert.Single(report.TopUserOnlyRejections));
    }

    [Fact]
    public void TopUserOnlyRejectionsKeepsAtMostFiveCodesOrderedByCount()
    {
        var codes = new[] { "A", "B", "C", "D", "E", "F" };
        var fills = codes.Select((_, i) => Buy(minute: i * 60)).ToArray();
        var candidates = codes.Select((_, i) =>
                Candidate(RealFillCandidateStates.Rejected, i * 60, $"E{i}", rejections: codes.Take(i + 1).ToArray()))
            .ToArray();

        var report = Compare(fills, candidates);

        Assert.Equal(5, report.TopUserOnlyRejections.Count);
        Assert.Equal(new RealFillRejectionCount("A", 6), report.TopUserOnlyRejections[0]);
        Assert.Equal(new RealFillRejectionCount("E", 2), report.TopUserOnlyRejections[4]);
        Assert.DoesNotContain(report.TopUserOnlyRejections, x => x.Code == "F");
    }

    [Theory]
    [InlineData(5, RealFillClasses.Matched)]
    [InlineData(-5, RealFillClasses.Matched)]
    [InlineData(5.001, RealFillClasses.UserOnly)]
    [InlineData(-5.001, RealFillClasses.UserOnly)]
    public void WindowBoundaryIsInclusiveOnBothSides(double offsetMinutes, string expected)
    {
        var report = Compare([Buy()], [Candidate(RealFillCandidateStates.Ready, offsetMinutes)]);

        Assert.Equal(expected, report.Rows.First(x => x.Side == RealFillComparer.Buy).Classification);
    }

    [Fact]
    public void DeviationIsMeasuredInAtrUnitsFromEntryReference()
    {
        var report = Compare([Buy(100.3m)], [Candidate(RealFillCandidateStates.Ready, 1, entryReference: 100m, atr: .2)]);

        Assert.Equal(1.5, Assert.Single(report.Rows).DeviationAtr);
        Assert.Equal(1.5, report.DeviationMedianAtr);
        Assert.Equal(1, report.DeviationSamples);
    }

    [Fact]
    public void DeviationIsNullWhenAtrIsMissing()
    {
        var report = Compare([Buy(100.3m)], [Candidate(RealFillCandidateStates.Ready, 1, atr: null)]);

        Assert.Null(Assert.Single(report.Rows).DeviationAtr);
        Assert.Equal(0, report.DeviationSamples);
        Assert.Null(report.DeviationMedianAtr);
        Assert.Null(report.DeviationQ1Atr);
        Assert.Null(report.DeviationQ3Atr);
    }

    [Fact]
    public void DeviationIsNullWhenAtrIsNotPositive()
    {
        var report = Compare([Buy(100.3m)], [Candidate(RealFillCandidateStates.Ready, 1, atr: 0)]);

        Assert.Null(Assert.Single(report.Rows).DeviationAtr);
    }

    [Fact]
    public void DeviationQuartilesAreReportedOverTheBuySamples()
    {
        var fills = new[] { Buy(100.1m, 0), Buy(100.2m, 60), Buy(100.3m, 120), Buy(100.4m, 180) };
        var candidates = new[]
        {
            Candidate(RealFillCandidateStates.Ready, 0, "E0"), Candidate(RealFillCandidateStates.Ready, 60, "E1"),
            Candidate(RealFillCandidateStates.Ready, 120, "E2"), Candidate(RealFillCandidateStates.Ready, 180, "E3")
        };

        var report = Compare(fills, candidates);

        Assert.Equal(4, report.DeviationSamples);
        Assert.Equal(1.25, report.DeviationMedianAtr);
        Assert.Equal(.875, report.DeviationQ1Atr);
        Assert.Equal(1.625, report.DeviationQ3Atr);
    }

    [Theory]
    [InlineData(102.5, RealFillSellPositions.AboveTarget)]
    [InlineData(100.5, RealFillSellPositions.BetweenStopAndTarget)]
    [InlineData(98.5, RealFillSellPositions.BelowStop)]
    public void SellIsPlacedAgainstTheOpenV5StopAndTarget(double price, string expected)
    {
        var trade = new RealFillOpenTrade("TEST", FilledAt.AddMinutes(-10), null, 99m, 101.5m);

        var report = Compare([Sell((decimal)price)], [], [trade]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(RealFillClasses.Sell, row.Classification);
        Assert.Equal(expected, row.SellPosition);
        Assert.Equal(1, report.RealSells);
    }

    [Fact]
    public void SellWithoutAnOpenV5TradeHasNoPosition()
    {
        var closed = new RealFillOpenTrade("TEST", FilledAt.AddMinutes(-60), FilledAt.AddMinutes(-10), 99m, 101.5m);

        var report = Compare([Sell(100.5m)], [], [closed]);

        Assert.Null(Assert.Single(report.Rows).SellPosition);
    }

    [Fact]
    public void SellsAreExcludedFromTheMatchRateDenominator()
    {
        var report = Compare([Buy(), Sell(101m)], [Candidate(RealFillCandidateStates.Ready, 1)]);

        Assert.Equal(1, report.RealBuys);
        Assert.Equal(1, report.RealSells);
        Assert.Equal(100d, report.MatchRatePercent);
    }

    [Fact]
    public void CandidatesOfAnotherSymbolNeverMatch()
    {
        var report = Compare([Buy()], [Candidate(RealFillCandidateStates.Ready, 1, symbol: "OTHER")]);

        var row = Assert.Single(report.Rows);
        Assert.Equal(RealFillClasses.UserOnly, row.Classification);
        Assert.Null(row.EventId);
    }
}
