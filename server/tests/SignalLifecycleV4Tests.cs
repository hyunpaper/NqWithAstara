using Astra.Server;
using Astra.Server.Domain;
using Xunit;

public sealed class SignalLifecycleV4Tests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-09T13:30:00-04:00");
    static Candle Bar(int minute, double low = 99, double close = 101) => new(T.AddMinutes(minute), 100, 102, low, close, 1000);

    [Fact]
    public void RepeatedDetectionCannotBypassInvalidationOnSameTriggerBar()
    {
        var prior = new SetupLatch(T, T, 99, "SETUP", T.Date);
        var invalid = SignalLifecycle.UpdateSetup(prior, "SETUP", Bar(0), 98, 70, 100, T.AddMinutes(1), T.Date, true);
        Assert.Null(invalid.Display); Assert.False(invalid.Emit); Assert.True(invalid.State!.Invalidated);
        var repeated = SignalLifecycle.UpdateSetup(invalid.State, "SETUP", Bar(0), 101, 70, 100, T.AddMinutes(2), T.Date, true);
        Assert.False(repeated.Emit);
    }

    [Fact]
    public void NewSessionClearsTombstoneAndAllowsSignal()
    {
        var prior = new SetupLatch(T, T, 99, "SETUP", T.Date, true);
        var next = SignalLifecycle.UpdateSetup(prior, "SETUP", Bar(1), 101, 70, 100, T.AddDays(1), T.Date.AddDays(1), true);
        Assert.True(next.Emit);
    }

    [Fact]
    public void SameTriggerBarIsDeduplicatedAndKeepsOriginalLow()
    {
        var prior = new SetupLatch(T, T, 99, "SETUP", T.Date);
        var next = SignalLifecycle.UpdateSetup(prior, "SETUP", Bar(0, 95), 101, 70, 100, T.AddMinutes(1), T.Date, true);
        Assert.False(next.Emit); Assert.Equal(99, next.State!.TriggerLow);
    }

    [Fact]
    public void ChaseRemainsVisibleWithoutCreatingEntry()
    {
        var result = SignalLifecycle.UpdateSetup(null, "CHASE", Bar(0), 101, 80, 100, T, T.Date, true);
        Assert.Equal("CHASE", result.Display); Assert.False(result.Emit);
    }

    [Fact]
    public void NullDetectionStillLeavesTombstoneAfterInvalidation()
    {
        var prior = new SetupLatch(T, T, 99, "SETUP", T.Date);
        var invalid = SignalLifecycle.UpdateSetup(prior, null, Bar(0), 98, 70, 100, T.AddMinutes(1), T.Date, true);
        var repeated = SignalLifecycle.UpdateSetup(invalid.State, "SETUP", Bar(0), 101, 70, 100, T.AddMinutes(2), T.Date, true);
        Assert.True(invalid.State!.Invalidated);
        Assert.False(repeated.Emit);
    }

    [Fact]
    public void NewlyDetectedSetupAlreadyInvalidAtLivePriceDoesNotEmit()
    {
        var result = SignalLifecycle.UpdateSetup(null, "SETUP", Bar(0), 98, 80, 100, T, T.Date, true);
        Assert.False(result.Emit);
        Assert.True(result.State!.Invalidated);
    }

    [Fact]
    public void ExpiredTombstoneCanRearmOnlyOnStrictlyNewerBar()
    {
        var tombstone = new SetupLatch(T, T, 99, "SETUP", T.Date, true);
        var next = SignalLifecycle.UpdateSetup(tombstone, "SETUP", Bar(1), 101, 80, 100, T.AddMinutes(10), T.Date, true);
        Assert.True(next.Emit);
        Assert.False(next.State!.Invalidated);
        Assert.Equal(T.AddMinutes(1), next.State.TriggerBarAt);
    }

    [Fact]
    public void ChaseDisplayTakesPriorityWhileValidSetupLatchIsRetained()
    {
        var prior = new SetupLatch(T, T, 99, "SETUP", T.Date);
        var result = SignalLifecycle.UpdateSetup(prior, "CHASE", Bar(1), 101, 80, 100, T.AddMinutes(1), T.Date, true);
        Assert.Equal("CHASE", result.Display);
        Assert.Same(prior, result.State);
        Assert.False(result.Emit);
    }

    [Fact]
    public void NewBreakoutRetracedAtLivePriceKeepsCooldownWithoutEntryOrDisplay()
    {
        var hit = new PriceLevel(101, "전일 고가");
        var result = SignalLifecycle.UpdateBreakout(null, hit, Bar(0, close: 102), 100.9, T, T.Date, true);
        Assert.NotNull(result.State);
        Assert.Null(result.Display);
        Assert.False(result.Emit);
    }

    [Fact]
    public void ActiveBreakoutIsHiddenWhenLivePriceRetracesBelowLevel()
    {
        var prior = new BreakoutLatch(T, T, "전일 고가", 101, T.Date);
        var result = SignalLifecycle.UpdateBreakout(prior, null, Bar(1, close: 102), 100.9, T.AddMinutes(1), T.Date, true);
        Assert.Same(prior, result.State);
        Assert.Null(result.Display);
        Assert.False(result.Emit);
    }
}
