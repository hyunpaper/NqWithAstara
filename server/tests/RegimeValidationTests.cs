using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RegimeValidationTests
{
    static readonly DateTimeOffset Session = new(2026, 9, 1, 9, 30, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void 분류는_평가시각까지_완료된_동일세션_봉만_사용한다()
    {
        var bars = Bars(20, 1m).Append(Bar(Session.AddMinutes(20), 10m))
            .Append(Bar(Session.AddDays(1), 100m)).ToArray();

        var result = RegimeValidationClassifier.Classify(TrendState.Up, 2, bars, Session,
            Session.AddHours(6.5), Session.AddMinutes(20));

        Assert.Equal(RegimeClassification.Available, result.Status);
        Assert.Equal("TRENDUP_HIGH", result.Key);
        Assert.Equal(20, result.CompletedBars);
    }

    [Fact]
    public void 완료봉이나_atr이_부족하면_unavailable이다()
    {
        var few = RegimeValidationClassifier.Classify(TrendState.Down, 1, Bars(19, 1m), Session,
            Session.AddHours(6.5), Session.AddHours(1));
        var noAtr = RegimeValidationClassifier.Classify(TrendState.Down, null, Bars(20, 1m), Session,
            Session.AddHours(6.5), Session.AddHours(1));

        Assert.Equal(RegimeClassification.Unavailable, few.Status);
        Assert.StartsWith(RegimeValidationClassifier.InsufficientBars, few.UnavailableReason);
        Assert.Equal(RegimeValidationClassifier.AtrUnavailable, noAtr.UnavailableReason);
        Assert.Null(few.Key);
        Assert.Null(noAtr.Key);
    }

    [Fact]
    public void 방향과_변동성_국면을_분리한다()
    {
        var bars = Bars(20, 2m);

        var down = RegimeValidationClassifier.Classify(TrendState.Down, 1, bars, Session,
            Session.AddHours(6.5), Session.AddHours(1));
        var range = RegimeValidationClassifier.Classify(TrendState.Range, 2, bars, Session,
            Session.AddHours(6.5), Session.AddHours(1));

        Assert.Equal("TRENDDOWN_LOW", down.Key);
        Assert.Equal("RANGE_NORMAL", range.Key);
    }

    [Fact]
    public void 측정은_oos_구간만_국면과_진입종류별로_집계한다()
    {
        var samples = new[]
        {
            Sample("TRAIN", 0, "TRENDUP_NORMAL", "PULLBACK", true, true, "TARGET", 1, 2, 3),
            Sample("A", 2, "TRENDUP_NORMAL", "PULLBACK", true, true, "TARGET", 1, 2, 2),
            Sample("B", 2, "TRENDUP_NORMAL", "PULLBACK", true, true, "STOP", 1.2, 2.4, -1),
            Sample("C", 3, "RANGE_HIGH", "BREAKOUT", false, false, null, null, null, null),
            Sample("D", 3, "TRENDUP_NORMAL", "PULLBACK", false, false, null, null, null, null)
        };

        var report = RegimeValidationEvaluator.Evaluate(samples, DateOnly.FromDateTime(Session.Date),
            DateOnly.FromDateTime(Session.Date.AddDays(1)), DateOnly.FromDateTime(Session.Date.AddDays(2)),
            DateOnly.FromDateTime(Session.Date.AddDays(3)));

        Assert.Equal(RegimeClassification.Available, report.Status);
        Assert.Equal(2, report.OutOfSampleSessions);
        var trend = Assert.Single(report.Measurements, x => x.Regime == "TRENDUP_NORMAL");
        Assert.Equal(3, trend.Signals);
        Assert.Equal(2, trend.Entries);
        Assert.Equal(1, trend.Stops);
        Assert.Equal(1, trend.Targets);
        Assert.Equal(50, trend.WinRatePercent);
        Assert.Equal(1, trend.NetPnlPercent);
        Assert.Equal(1.1, trend.AverageStopDistancePercent);
        Assert.Equal(2.2, trend.AverageTargetDistancePercent);
    }

    [Fact]
    public void 표본부족과_국면결측은_unavailable로_표시한다()
    {
        var report = RegimeValidationEvaluator.Evaluate(
            [Sample("A", 2, null, "PULLBACK", false, false, null, null, null, null)],
            DateOnly.FromDateTime(Session.Date), DateOnly.FromDateTime(Session.Date.AddDays(1)),
            DateOnly.FromDateTime(Session.Date.AddDays(2)), DateOnly.FromDateTime(Session.Date.AddDays(3)));

        Assert.Equal(RegimeClassification.Unavailable, report.Status);
        Assert.Contains($"{RegimeValidationEvaluator.InsufficientOosSessions}:1/2", report.Limitations);
        Assert.Contains(RegimeValidationEvaluator.RegimeUnavailable, report.Limitations);
        Assert.Empty(report.Measurements);
    }

    [Fact]
    public void 훈련과_oos_구간이_겹치면_거부한다()
    {
        var day = DateOnly.FromDateTime(Session.Date);

        Assert.Throws<ArgumentException>(() => RegimeValidationEvaluator.Evaluate([], day, day.AddDays(2),
            day.AddDays(2), day.AddDays(3)));
    }

    static StructureBar[] Bars(int count, decimal range) => Enumerable.Range(0, count)
        .Select(i => Bar(Session.AddMinutes(i), range)).ToArray();

    static StructureBar Bar(DateTimeOffset start, decimal range) =>
        new(start, start.AddMinutes(1), 100m, 100m + range, 100m, 100m + range / 2, 1000);

    static RegimeValidationSample Sample(string id, int day, string? regime, string kind, bool approved,
        bool entered, string? exit, double? stop, double? target, double? pnl) =>
        new(id, "TEST", DateOnly.FromDateTime(Session.Date.AddDays(day)), Session.AddDays(day), regime, kind,
            approved, entered, exit, stop, target, pnl);
}
