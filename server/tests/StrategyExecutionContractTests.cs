using Astra.Server.Domain;
using Astra.Server;
using Xunit;

namespace Astra.Server.Tests;

public sealed class StrategyExecutionContractTests
{
    [Fact]
    public void 확인봉_이전에는_체결하지_않는다()
    {
        var pending = Pending();
        var bar = new Candle(pending.ConfirmationBarStart.AddMinutes(-1), 101, 102, 100, 101, 1000);
        var result = PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), 101);
        Assert.Equal(PendingEntryDecision.RejectedUnobservedFill, result.Decision);
        Assert.Null(result.FillPrice);
    }

    [Fact]
    public void 롱은_손절갭이면_진입하지_않는다()
    {
        var pending = Pending();
        var bar = new Candle(pending.ConfirmationBarStart, 98, 102, 97, 100, 1000);
        var result = PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), 98);
        Assert.Equal(PendingEntryDecision.RejectedGap, result.Decision);
    }

    [Fact]
    public void 숏은_가격순서와_손절갭을_독립적으로_판정한다()
    {
        var pending = Pending() with { Side = TradeSide.Short, PlannedStop = 105, PlannedTarget = 95 };
        var bar = new Candle(pending.ConfirmationBarStart, 100, 103, 96, 99, 1000);
        var result = PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), 99);
        Assert.Equal(PendingEntryDecision.Confirmed, result.Decision);
        Assert.Equal(99, result.FillPrice);
    }

    [Fact]
    public void 확인봉_종료_직전은_관측으로_인정하지_않는다()
    {
        var pending = Pending();
        var bar = new Candle(pending.ConfirmationBarStart, 100, 103, 99, 102, 1000);
        var result = PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddSeconds(59), 101);
        Assert.Equal(PendingEntryDecision.RejectedUnobservedFill, result.Decision);
    }

    [Fact]
    public void 확인봉_범위_밖_체결가는_관측하지_않는다()
    {
        var pending = Pending();
        var bar = new Candle(pending.ConfirmationBarStart, 100, 103, 99, 102, 1000);
        var result = PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), 104);
        Assert.Equal(PendingEntryDecision.RejectedUnobservedFill, result.Decision);
    }

    [Fact]
    public void 예정된_확인봉보다_이후_봉은_확인하지_않는다()
    {
        var pending = Pending();
        var later = new Candle(pending.ConfirmationBarStart.AddMinutes(1), 100, 103, 99, 102, 1000);
        var result = PendingEntryPolicy.Confirm(pending, later, later.Timestamp.AddMinutes(1), 101);
        Assert.Equal(PendingEntryDecision.RejectedUnobservedFill, result.Decision);
    }

    [Fact]
    public void 숏은_갭과_잘못된_가격순서를_거부한다()
    {
        var pending = Pending() with { Side = TradeSide.Short, PlannedStop = 105, PlannedTarget = 95 };
        var gap = new Candle(pending.ConfirmationBarStart, 106, 107, 104, 105, 1000);
        Assert.Equal(PendingEntryDecision.RejectedGap,
            PendingEntryPolicy.Confirm(pending, gap, gap.Timestamp.AddMinutes(1), 106).Decision);
        var invalid = pending with { PlannedTarget = 106 };
        var bar = new Candle(invalid.ConfirmationBarStart, 100, 103, 96, 99, 1000);
        Assert.Equal(PendingEntryDecision.RejectedInvalidPlan,
            PendingEntryPolicy.Confirm(invalid, bar, bar.Timestamp.AddMinutes(1), 99).Decision);
    }

    [Fact]
    public void 만료된_대기는_확인하지_않는다()
    {
        var pending = Pending();
        var bar = new Candle(pending.ConfirmationBarStart, 100, 103, 99, 102, 1000);
        Assert.Equal(PendingEntryDecision.Expired,
            PendingEntryPolicy.Confirm(pending, bar, pending.ExpiresAt.AddSeconds(1), 101).Decision);
    }

    static PendingEntry Pending() => new("event-1", "TEST", TradeSide.Long,
        new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 14, 1, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 14, 6, 0, TimeSpan.Zero), 99, 104, 101, "plan-1", "hash");
}
