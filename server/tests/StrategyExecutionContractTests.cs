using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using System.Text.Json;
using Astra.Server;
using Xunit;

namespace Astra.Server.Tests;

public sealed class StrategyExecutionContractTests
{
    [Fact]
    public void 저장된_기존_확인결정의_숫자값은_변하지_않는다()
    {
        Assert.Equal(PendingEntryDecision.RejectedUnobservedFill,
            JsonSerializer.Deserialize<PendingEntryDecision>("3"));
        Assert.Equal(PendingEntryDecision.RejectedInvalidPlan,
            JsonSerializer.Deserialize<PendingEntryDecision>("4"));
        Assert.Equal("5", JsonSerializer.Serialize(PendingEntryDecision.RejectedThesisInvalidated));
    }

    [Fact]
    public void 돌파_대기계약은_계획의_무효화구간_경계와_정책버전을_동결한다()
    {
        var plan = Assert.IsType<StructuralTradePlan>(StructuralPlanner.Evaluate(
            D2.ExampleA() with { Kind = "BREAKOUT" }, StructurePolicy.Default).Plan);

        var pending = PendingEntryPolicy.Create("event", "TEST", plan.CreatedAt,
            plan.CreatedAt.AddMinutes(1), plan.ExpiresAt, plan,
            StructurePolicy.Default.BreakoutConfirmationGateVersion);

        Assert.Equal("BREAKOUT", pending.SetupKind);
        Assert.Equal((double)plan.InvalidationZoneSnapshot.Upper, pending.ConfirmationBoundary);
        Assert.Equal("hold-breakout-boundary.1", pending.ConfirmationPolicyVersion);
        Assert.Equal(plan.PolicyHash, pending.PlanPolicyHash);
    }

    [Fact]
    public void 돌파가_아닌_대기계약에는_확인경계를_만들지_않는다()
    {
        var plan = Assert.IsType<StructuralTradePlan>(
            StructuralPlanner.Evaluate(D2.ExampleA(), StructurePolicy.Default).Plan);

        var pending = PendingEntryPolicy.Create("event", "TEST", plan.CreatedAt,
            plan.CreatedAt.AddMinutes(1), plan.ExpiresAt, plan,
            StructurePolicy.Default.BreakoutConfirmationGateVersion);

        Assert.Equal("PULLBACK", pending.SetupKind);
        Assert.Null(pending.ConfirmationBoundary);
    }

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

    [Theory]
    [InlineData(100.00, PendingEntryDecision.RejectedThesisInvalidated)]
    [InlineData(100.01, PendingEntryDecision.Confirmed)]
    public void 롱_돌파는_확인봉_종가가_돌파경계_위에_남아야_한다(double close,
        PendingEntryDecision expected)
    {
        var pending = Pending() with
        {
            SetupKind = "BREAKOUT", ConfirmationBoundary = 100,
            ConfirmationPolicyVersion = "hold-breakout-boundary.1"
        };
        var bar = new Candle(pending.ConfirmationBarStart, 101, 102, 99, close, 1000);

        Assert.Equal(expected,
            PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), close).Decision);
    }

    [Theory]
    [InlineData(100.00, PendingEntryDecision.RejectedThesisInvalidated)]
    [InlineData(99.99, PendingEntryDecision.Confirmed)]
    public void 숏_돌파는_확인봉_종가가_돌파경계_아래에_남아야_한다(double close,
        PendingEntryDecision expected)
    {
        var pending = Pending() with
        {
            Side = TradeSide.Short, PlannedStop = 105, PlannedTarget = 95,
            SetupKind = "BREAKOUT", ConfirmationBoundary = 100,
            ConfirmationPolicyVersion = "hold-breakout-boundary.1"
        };
        var bar = new Candle(pending.ConfirmationBarStart, 99, 101, 98, close, 1000);

        Assert.Equal(expected,
            PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), close).Decision);
    }

    [Fact]
    public void 이전_대기계약은_돌파경계_필드가_없어도_기존대로_확인한다()
    {
        var pending = Pending() with { SetupKind = "BREAKOUT", ConfirmationBoundary = 100 };
        var bar = new Candle(pending.ConfirmationBarStart, 101, 102, 99, 100, 1000);

        Assert.Equal(PendingEntryDecision.Confirmed,
            PendingEntryPolicy.Confirm(pending, bar, bar.Timestamp.AddMinutes(1), 100).Decision);
    }

    static PendingEntry Pending() => new("event-1", "TEST", TradeSide.Long,
        new DateTimeOffset(2026, 9, 20, 14, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 14, 1, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, 14, 6, 0, TimeSpan.Zero), 99, 104, 101, "plan-1", "hash");
}
