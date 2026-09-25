using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ExecutionCostValidatorTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 25, 13, 30, 0, TimeSpan.Zero);
    static readonly DateTimeOffset End = Start.AddHours(6.5);
    static readonly DateTimeOffset AsOf = End.AddMinutes(1);

    [Fact]
    public void 모델비용과관측비용을서로다른코호트로집계한다()
    {
        var report = ExecutionCostValidator.Evaluate([
            Row("M1", ExecutionCostBasis.Modeled, gross: 2, spread: .1, fee: .1, slippage: .2, gap: .1, borrow: 0),
            Row("O1", ExecutionCostBasis.Observed, gross: 1, spread: .05, fee: .1, slippage: .1, gap: .05, borrow: 0)
        ], AsOf);

        var modeled = Assert.Single(report.Cohorts, x => x.Basis == ExecutionCostBasis.Modeled);
        var observed = Assert.Single(report.Cohorts, x => x.Basis == ExecutionCostBasis.Observed);
        Assert.Equal(1.5, modeled.Metrics.NetPnlPercent);
        Assert.Equal(.7, observed.Metrics.NetPnlPercent);
        Assert.Equal(.5, modeled.Metrics.TotalCostPercent);
        Assert.Equal(.3, observed.Metrics.TotalCostPercent);
    }

    [Fact]
    public void 비용결측과체결실패를성과표본으로세지않는다()
    {
        var missing = Row("U1", ExecutionCostBasis.Observed, gross: 5, slippage: null);
        var failed = Row("F1", ExecutionCostBasis.Observed, gross: 9) with
        {
            FillStatus = ExecutionFillStatus.Rejected,
            FilledAt = null,
            GrossPnlPercent = null
        };

        var report = ExecutionCostValidator.Evaluate([missing, failed], AsOf);
        var cohort = Assert.Single(report.Cohorts);

        Assert.Equal(0, cohort.Metrics.Samples);
        Assert.Null(cohort.Metrics.NetPnlPercent);
        Assert.Equal(2, cohort.Unavailable);
        Assert.Equal(1, cohort.FillFailures);
        Assert.Contains(ExecutionCostAvailability.SlippageUnavailable, report.Rows[1].Reasons.Concat(report.Rows[0].Reasons));
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.FillFailed]);
    }

    [Fact]
    public void 스프레드수수료슬리피지가격갭차입비용을각각분류한다()
    {
        var rows = new[]
        {
            Row("S", ExecutionCostBasis.Observed) with { SpreadPercent = null },
            Row("F", ExecutionCostBasis.Observed) with { FeePercent = null },
            Row("L", ExecutionCostBasis.Observed) with { SlippagePercent = null },
            Row("G", ExecutionCostBasis.Observed) with { PriceGapPercent = null },
            Row("B", ExecutionCostBasis.Observed) with { BorrowCostPercent = null },
            Row("Q", ExecutionCostBasis.Observed) with { AvailableQuantity = null },
            Row("D", ExecutionCostBasis.Observed) with { DataAvailable = false }
        };

        var report = ExecutionCostValidator.Evaluate(rows, AsOf);

        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.SpreadUnavailable]);
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.FeeUnavailable]);
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.SlippageUnavailable]);
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.PriceGapUnavailable]);
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.BorrowCostUnavailable]);
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.LiquidityUnavailable]);
        Assert.Equal(1, report.ReasonCounts[ExecutionCostAvailability.DataUnavailable]);
    }

    [Fact]
    public void 유동성부족과손익지표를함께보고한다()
    {
        var report = ExecutionCostValidator.Evaluate([
            Row("A", ExecutionCostBasis.Observed, gross: 2, available: 50),
            Row("B", ExecutionCostBasis.Observed, gross: -.5, available: 100),
            Row("C", ExecutionCostBasis.Observed, gross: -.5, available: 100)
        ], AsOf);
        var cohort = Assert.Single(report.Cohorts);

        Assert.Equal(1, cohort.LiquidityShortfalls);
        Assert.Equal(50m, report.Rows.Single(x => x.Id == "A").LiquidityCoveragePercent);
        Assert.Equal(.4, cohort.Metrics.NetPnlPercent);
        Assert.Equal(1.4, cohort.Metrics.MaxDrawdownPercent);
        Assert.Equal(1.8 / 1.4, cohort.Metrics.ProfitFactor!.Value, 6);
    }

    [Fact]
    public void 미래관측과세션밖체결은검증성과에서제외한다()
    {
        var future = Row("F", ExecutionCostBasis.Observed) with { ObservedAt = AsOf.AddSeconds(1) };
        var outside = Row("O", ExecutionCostBasis.Observed) with { FilledAt = End.AddSeconds(1) };

        var report = ExecutionCostValidator.Evaluate([future, outside], AsOf);

        Assert.All(report.Cohorts, x => Assert.Equal(0, x.Metrics.Samples));
        Assert.Contains(ExecutionCostAvailability.FutureObservation, report.Rows.Single(x => x.Id == "F").Reasons);
        Assert.Contains(ExecutionCostAvailability.OutsideSession, report.Rows.Single(x => x.Id == "O").Reasons);
    }

    [Fact]
    public void 학습과검증세그먼트를섞지않는다()
    {
        var report = ExecutionCostValidator.Evaluate([
            Row("I", ExecutionCostBasis.Observed) with { Segment = ExecutionValidationSegment.InSample },
            Row("O", ExecutionCostBasis.Observed) with { Segment = ExecutionValidationSegment.OutOfSample }
        ], AsOf);

        Assert.Equal(2, report.Cohorts.Count);
        Assert.All(report.Cohorts, x => Assert.Equal(1, x.Observations));
    }

    static ExecutionCostObservation Row(string id, ExecutionCostBasis basis, double gross = 1,
        double? spread = .05, double? fee = .1, double? slippage = .05, double? gap = 0,
        double? borrow = 0, decimal? available = 100) =>
        new(id, "SOXL", DateOnly.FromDateTime(Start.Date), Start, End, Start.AddMinutes(10),
            Start.AddMinutes(11), Start.AddMinutes(30), ExecutionValidationSegment.OutOfSample,
            basis, "기본", ExecutionFillStatus.Filled, gross, spread, fee, slippage, gap, borrow,
            100, available);
}
