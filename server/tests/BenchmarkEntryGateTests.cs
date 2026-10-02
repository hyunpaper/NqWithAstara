using Astra.Server;
using Astra.Server.Domain.Structure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class BenchmarkEntryGateTests
{
    static readonly DateTimeOffset ConfirmationEnd = DateTimeOffset.Parse("2026-07-20T16:30:00Z");
    static readonly StructurePolicy Enabled = StructurePolicy.Default with
    {
        RequirePositiveBenchmarkForRebound = true
    };

    [Fact]
    public void PositiveReturnAllowsLongRebound()
    {
        var result = BenchmarkEntryGate.Evaluate(Enabled, "REBOUND", TradeSide.Long,
            Bars(100, 101), ConfirmationEnd);

        Assert.True(result.Allowed);
        Assert.True(result.Available);
        Assert.Equal(BenchmarkEntryGate.NonNegative, result.Reason);
        Assert.Equal(1, result.ReturnPercent!.Value, 10);
    }

    [Fact]
    public void NegativeReturnBlocksLongRebound()
    {
        var result = BenchmarkEntryGate.Evaluate(Enabled, "REBOUND", TradeSide.Long,
            Bars(100, 99), ConfirmationEnd);

        Assert.False(result.Allowed);
        Assert.True(result.Available);
        Assert.Equal(BenchmarkEntryGate.Negative, result.Reason);
        Assert.Equal(-1, result.ReturnPercent!.Value, 10);
    }

    [Fact]
    public void ZeroReturnAllowsLongRebound()
    {
        var result = BenchmarkEntryGate.Evaluate(Enabled, "REBOUND", TradeSide.Long,
            Bars(100, 100), ConfirmationEnd);

        Assert.True(result.Allowed);
        Assert.True(result.Available);
        Assert.Equal(0, result.ReturnPercent);
    }

    [Fact]
    public void MissingBarsAllowWithUnavailableEvidence()
    {
        var result = BenchmarkEntryGate.Evaluate(Enabled, "REBOUND", TradeSide.Long,
            [], ConfirmationEnd);

        Assert.True(result.Allowed);
        Assert.False(result.Available);
        Assert.Equal(BenchmarkEntryGate.Unavailable, result.Reason);
    }

    [Fact]
    public void GapAllowsWithUnavailableEvidence()
    {
        var bars = Bars(100, 99).Where(x => x.Timestamp != ConfirmationEnd.AddMinutes(-8)).ToArray();

        var result = BenchmarkEntryGate.Evaluate(Enabled, "REBOUND", TradeSide.Long,
            bars, ConfirmationEnd);

        Assert.True(result.Allowed);
        Assert.False(result.Available);
        Assert.Equal(BenchmarkEntryGate.Unavailable, result.Reason);
    }

    [Fact]
    public void FutureBarsDoNotChangeTheConfirmationCutoff()
    {
        var bars = Bars(100, 101).Append(new Candle(ConfirmationEnd, 50, 50, 50, 50, 1)).ToArray();

        var result = BenchmarkEntryGate.Evaluate(Enabled, "REBOUND", TradeSide.Long,
            bars, ConfirmationEnd);

        Assert.True(result.Allowed);
        Assert.Equal(1, result.ReturnPercent!.Value, 10);
    }

    [Fact]
    public void SessionReturnIsPitAndIndependentOfEntryGateEnablement()
    {
        var bars = Bars(100, 102).Append(new Candle(ConfirmationEnd, 50, 50, 50, 50, 1)).ToArray();

        var result = BenchmarkEntryGate.SessionReturnPercent(bars, ConfirmationEnd);

        Assert.Equal(2, result!.Value, 10);
        Assert.False(StructurePolicy.Default.RequirePositiveBenchmarkForRebound);
    }

    [Fact]
    public void DefaultImmutableSessionBarsAreUnavailable()
    {
        System.Collections.Immutable.ImmutableArray<Candle> bars = default;

        Assert.Null(BenchmarkEntryGate.SessionReturnPercent(bars, ConfirmationEnd));
    }

    [Theory]
    [InlineData(false, "REBOUND", TradeSide.Long)]
    [InlineData(true, "PULLBACK", TradeSide.Long)]
    [InlineData(true, "BREAKOUT", TradeSide.Long)]
    [InlineData(true, "REBOUND", TradeSide.Short)]
    public void GateOnlyAppliesToEnabledLongRebound(bool enabled, string kind, TradeSide side)
    {
        var policy = Enabled with { RequirePositiveBenchmarkForRebound = enabled };

        var result = BenchmarkEntryGate.Evaluate(policy, kind, side, Bars(100, 99), ConfirmationEnd);

        Assert.True(result.Allowed);
        Assert.False(result.Available);
        Assert.Equal(BenchmarkEntryGate.NotRequired, result.Reason);
    }

    static Candle[] Bars(double firstClose, double lastClose) => Enumerable.Range(0, 16)
        .Select(i =>
        {
            var close = i == 0 ? firstClose : i == 15 ? lastClose : firstClose;
            return new Candle(ConfirmationEnd.AddMinutes(-16 + i), close, close, close, close, 1);
        }).ToArray();
}
