using Astra.Server.Application.Backtest;
using Astra.Server.Domain;
using Xunit;

namespace Astra.Server.Tests;

public sealed class HistoricalReplayDiagnosticsTests
{
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-08T13:30:00Z");

    [Fact]
    public void ItSeparatesConfirmedCostsFromUnavailableSlippageAcrossRequiredCohorts()
    {
        var diagnostics = HistoricalReplayDiagnosticsBuilder.Build([
            Row("REBOUND", "STOP", -1.2, -1, .2, Context("RANGE", 52, 1.7m)),
            Row("PULLBACK", "TARGET", 1.8, 2, .2, Context("UP", 61, 2.4m)),
            Row("BREAKOUT", "EOD", -.2, 0, .2, null)
        ]);

        Assert.Equal(3, diagnostics.Summary.ClosedTrades);
        Assert.Equal(1, diagnostics.Summary.Wins);
        Assert.Equal(33.3, diagnostics.Summary.WinRatePercent);
        Assert.Equal(1, diagnostics.Summary.GrossPnlPercent);
        Assert.Equal(.6, diagnostics.Summary.FeePercent);
        Assert.Equal(.4, diagnostics.Summary.NetPnlPercent);
        Assert.Null(diagnostics.Summary.SlippagePercent);
        Assert.Contains("slippage", diagnostics.Notice, StringComparison.Ordinal);
        Assert.Equal(new[] { "entryQuality", "kind", "planNetR", "trend" }, diagnostics.Cohorts
            .Select(x => x.Dimension).Distinct().Order().ToArray());
        var missing = Assert.Single(diagnostics.Cohorts.Where(x => x.Dimension == "entryQuality" && x.Key == "UNCOLLECTED"));
        Assert.False(missing.Collected);
        Assert.Equal(1, missing.Summary.ClosedTrades);
    }

    [Fact]
    public void EquivalentRowsProduceTheSameFingerprintRegardlessOfInputOrder()
    {
        var first = Row("REBOUND", "STOP", -.7, -.5, .2, Context("DOWN", 49, 1.4m));
        var second = Row("PULLBACK", "TARGET", 1.3, 1.5, .2, Context("UP", 71, 3.1m)) with
        {
            Trade = Row("PULLBACK", "TARGET", 1.3, 1.5, .2, Context("UP", 71, 3.1m)).Trade with { EnteredAt = At.AddMinutes(1) }
        };

        var left = HistoricalReplayDiagnosticsBuilder.Build([first, second]);
        var right = HistoricalReplayDiagnosticsBuilder.Build([second, first]);

        Assert.Equal(left.ResultFingerprint, right.ResultFingerprint);
    }

    static HistoricalReplayTradeResult Row(string kind, string status, double net, double gross, double fee,
        FrozenStructureContext? context) => new(new SimTrade(kind + status, "TSLA", kind, At, 100, 110, 95,
            null, null, status, 100 + gross, At.AddMinutes(5), net, 100, Structure: context), gross, fee, null, net);

    static FrozenStructureContext Context(string trend, double quality, decimal netR) => new("event-" + trend,
        new FrozenPlanSnapshot("plan", "REBOUND", 100, 95, 95, 110, "invalid", 94, 96, "target", 109, 111,
            .1m, "basis", .1m, 9m, 5m, netR, .05, .2m, 0, null, true, "cost", "fill", At, At.AddMinutes(5),
            "v5-structure.1", "policy", [], "설명"), trend, 0, quality, At, At, "v5-exit.frozen-plan.1");
}
