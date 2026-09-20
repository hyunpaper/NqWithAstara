using Astra.Server.Domain;

namespace Astra.Server.Tests;

public sealed class StrategyExecutionContractTests
{
    [Fact]
    public void 확인봉_이전에는_체결하지_않는다()
    {
        var pending = Pending();
        var bar = new Candle(pending.ConfirmationBarStart.AddMinutes(-1), 101, 102, 100, 101, 1000);
        var result = PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp, 101);
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

    static PendingEntry Pending() => new("event-1", "TEST", TradeSide.Long,
        new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 14, 1, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 14, 6, 0, TimeSpan.Zero), 99, 104, 101, "plan-1", "hash");
}
