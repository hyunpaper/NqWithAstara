using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RiskFrequencyEvaluatorTests
{
    static readonly RiskFrequencyThresholds Small = new(3, 4, 0.5, 1, 2, 20);

    [Fact]
    public void SymbolMetricsMeasureFrequencyReturnRiskAndExposureTogether()
    {
        var (a, ta) = Vx.Entered("A", 2, 0, symbol: "AAA");
        var (b, tb) = Vx.Entered("B", -1, 1, symbol: "AAA");
        var (c, tc) = Vx.Entered("C", 1, 2, symbol: "AAA");
        var waiting = Vx.Row("obs-D", [Vx.Candidate("D", day: 3)], 3, symbol: "AAA");

        var report = RiskFrequencyEvaluator.Evaluate(Vx.Link([a, b, c, waiting], [ta, tb, tc]).Candidates,
            Small, 3);

        var metrics = Assert.Single(report.Overall);
        Assert.Equal("AAA", metrics.Symbol);
        Assert.Equal(4, metrics.Sessions);
        Assert.Equal(4, metrics.Opportunities);
        Assert.Equal(3, metrics.Trades);
        Assert.Equal(3, metrics.RealizedTrades);
        Assert.Equal(1, metrics.AverageOpportunitiesPerSession);
        Assert.Equal(.75, metrics.AverageTradesPerSession);
        Assert.Equal(66.6667, metrics.WinRatePercent);
        Assert.Equal(1.5, metrics.PayoffRatio);
        Assert.Equal(3, metrics.ProfitFactor);
        Assert.Equal(1, metrics.MaxDrawdownPercent);
        Assert.Equal(54, metrics.TotalExposureMinutes);
        Assert.Equal(18, metrics.AverageExposureMinutesPerTrade);
        Assert.Equal(13.5, metrics.AverageExposureMinutesPerSession);
        Assert.Equal(RiskFrequencyVerdict.MeetsContract, metrics.Verdict);
        Assert.Equal(4, metrics.Daily.Count);
        Assert.Equal(new DailyRiskFrequencyMetrics(DateOnly.FromDateTime(Vx.SessionOn(0).Date), 1, 1, 1, 18),
            metrics.Daily[0]);
        Assert.Equal(new DailyRiskFrequencyMetrics(DateOnly.FromDateTime(Vx.SessionOn(3).Date), 1, 0, 0, null),
            metrics.Daily[3]);
    }

    [Fact]
    public void RatiosStayNullWhenThereIsNoLossSample()
    {
        var (row, trade) = Vx.Entered("A", 2);

        var metrics = Assert.Single(RiskFrequencyEvaluator.Evaluate(Vx.Link([row], [trade]).Candidates).Overall);

        Assert.Null(metrics.PayoffRatio);
        Assert.Null(metrics.ProfitFactor);
        Assert.Equal(RiskFrequencyVerdict.InsufficientSample, metrics.Verdict);
    }

    [Fact]
    public void DrawdownUsesChronologicalExitOrderInsteadOfInputOrder()
    {
        var (first, firstTrade) = Vx.Entered("FIRST", 2, 0);
        var (second, secondTrade) = Vx.Entered("SECOND", -3, 1);
        var (third, thirdTrade) = Vx.Entered("THIRD", 1, 2);

        var metrics = Assert.Single(RiskFrequencyEvaluator.Evaluate(
            Vx.Link([third, first, second], [thirdTrade, firstTrade, secondTrade]).Candidates).Overall);

        Assert.Equal(3, metrics.MaxDrawdownPercent);
    }

    [Fact]
    public void WalkForwardUsesOnlyLaterDisjointOutOfSampleSessions()
    {
        var rows = new List<ObservationRow>();
        var trades = new List<SimTrade>();
        for (var day = 0; day < 8; day++)
        {
            var (row, trade) = Vx.Entered($"E{day}", day % 2 == 0 ? 1 : -1, day);
            rows.Add(row);
            trades.Add(trade);
        }

        var thresholds = new RiskFrequencyThresholds(1, 1, 0, 10, 99, 999);
        var report = RiskFrequencyEvaluator.Evaluate(Vx.Link(rows, trades).Candidates, thresholds, 3);

        Assert.Equal(RiskFrequencyEvaluator.Method, report.Method);
        Assert.Equal(3, report.Folds.Count);
        Assert.All(report.Folds, fold => Assert.True(fold.InSampleTo < fold.OutOfSampleFrom));
        Assert.True(report.Folds[0].OutOfSampleTo < report.Folds[1].OutOfSampleFrom);
        Assert.True(report.Folds[1].OutOfSampleTo < report.Folds[2].OutOfSampleFrom);
        Assert.All(report.Folds, fold => Assert.Single(fold.OutOfSample));
    }

    [Fact]
    public void TooFewSessionsNeverFallsBackToRandomValidation()
    {
        var (row, trade) = Vx.Entered("A", 1);

        var report = RiskFrequencyEvaluator.Evaluate(Vx.Link([row], [trade]).Candidates);

        Assert.Empty(report.Folds);
        Assert.Contains($"{RiskFrequencyEvaluator.InsufficientSessions}:1/4", report.Limitations);
        Assert.NotEmpty(report.Contract);
    }

    [Fact]
    public void RiskLimitsAreReportedWithoutChangingAnyStrategyPolicy()
    {
        var (a, ta) = Vx.Entered("A", 1, 0);
        var (b, tb) = Vx.Entered("B", -3, 1);
        var thresholds = new RiskFrequencyThresholds(2, 2, 0, 2, 2, 10);

        var metrics = Assert.Single(RiskFrequencyEvaluator.Evaluate(Vx.Link([a, b], [ta, tb]).Candidates,
            thresholds).Overall);

        Assert.Equal(RiskFrequencyVerdict.RiskLimitExceeded, metrics.Verdict);
        Assert.Contains("MDD_ABOVE_MAX:3/2", metrics.VerdictReasons);
        Assert.Contains("EXPOSURE_ABOVE_MAX:18/10", metrics.VerdictReasons);
    }
}
