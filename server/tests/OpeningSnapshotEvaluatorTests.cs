using Astra.Server;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Opening;
using Xunit;

public sealed class OpeningSnapshotEvaluatorTests
{
    static readonly DateTimeOffset Open = DateTimeOffset.Parse("2026-09-09T13:30:00Z");
    static readonly DateOnly Date = new(2026, 9, 9);

    static Candle Flat(int minute, double volume = 200) => new(Open.AddMinutes(minute), 100, 100, 100, 100, volume);

    static Candle[] FlatBars(int count) => Enumerable.Range(0, count).Select(i => Flat(i)).ToArray();

    static SessionVolumeProfile[] Prev(int count, decimal at5) =>
        Enumerable.Range(0, count).Select(_ => new SessionVolumeProfile(Date, [10, 20, 30, 40, at5])).ToArray();

    static OpeningScanInput Input(Candle[] bars, SessionVolumeProfile[] previous, double price,
        string quoteStatus = "fresh", IReadOnlyList<Candle>? daily = null, IReadOnlyList<Candle>? all = null) =>
        new("NVDA", "엔비디아", Date, Open, Open.AddMinutes(bars.Length), bars, all ?? [], daily, previous,
            price, Open.AddMinutes(bars.Length), quoteStatus, OpeningScanPolicy.Default);

    [Fact]
    public void StrongRequiresBothVolumeAndPrice()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 200), 102));
        Assert.Equal("STRONG", s.Grade);
        Assert.True(s.RvolNow >= 2);
        Assert.Equal("full", s.VolumeStatus);
    }

    [Fact]
    public void VolumeOnlyWhenPriceNotUp()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 200), 99));
        Assert.Equal("VOLUME_ONLY", s.Grade);
    }

    [Fact]
    public void PriceOnlyWhenVolumeWeak()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 102));
        Assert.Equal("PRICE_ONLY", s.Grade);
    }

    [Fact]
    public void WeakWhenNeither()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 99));
        Assert.Equal("WEAK", s.Grade);
    }

    [Fact]
    public void FewerThanThreeSessionsCannotBeStrong()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(2, 10), 102));
        Assert.Equal("insufficient", s.VolumeStatus);
        Assert.Null(s.RvolNow);
        Assert.Null(s.Rvol3.Ratio);
        Assert.Equal(2, s.Rvol3.SampleCount);
        Assert.Equal("PRICE_ONLY", s.Grade);
    }

    [Fact]
    public void ThreeSessionsGradeOnShortWindow()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(3, 200), 102));
        Assert.Equal("partial", s.VolumeStatus);
        Assert.NotNull(s.Rvol3.Ratio);
        Assert.NotNull(s.Rvol5.Ratio);
        Assert.Equal(s.Rvol5.Ratio, s.RvolNow);
        Assert.Equal("STRONG", s.Grade);
    }

    [Fact]
    public void ThreeWindowsAllComputedWithTwentySessions()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 200), 102));
        Assert.Equal(3, s.Rvol3.SampleCount);
        Assert.Equal(5, s.Rvol5.SampleCount);
        Assert.Equal(20, s.Rvol20.SampleCount);
        Assert.Equal(200, s.Rvol5.BaselineVolume);
        Assert.Equal(s.Rvol5.Ratio, s.RvolNow);
    }

    [Fact]
    public void PriceUpBoundaryIsInclusive()
    {
        Assert.Equal("PRICE_ONLY", OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 100.5)).Grade);
        Assert.Equal("WEAK", OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 100.49)).Grade);
    }

    [Fact]
    public void BelowVwapIsNotPriceUp()
    {
        var bars = new[]
        {
            new Candle(Open, 100, 110, 100, 110, 1000),
            new Candle(Open.AddMinutes(1), 101, 101, 101, 101, 1000),
            new Candle(Open.AddMinutes(2), 101, 101, 101, 101, 1000),
            new Candle(Open.AddMinutes(3), 101, 101, 101, 101, 1000),
            new Candle(Open.AddMinutes(4), 101, 101, 101, 101, 1000),
        };
        var s = OpeningSnapshotEvaluator.Evaluate(Input(bars, Prev(20, 5000), 101.5));
        Assert.False(s.AboveVwap);
        Assert.NotEqual("PRICE_ONLY", s.Grade);
    }

    [Fact]
    public void First5CountsUpBarsAndNewHighs()
    {
        var bars = new[]
        {
            new Candle(Open, 100, 101, 99, 101, 200),
            new Candle(Open.AddMinutes(1), 101, 103, 100, 102, 200),
            new Candle(Open.AddMinutes(2), 102, 102, 100, 100, 200),
        };
        var s = OpeningSnapshotEvaluator.Evaluate(Input(bars, Prev(20, 5000), 100));
        Assert.Equal(3, s.First5!.BarsSeen);
        Assert.Equal(2, s.First5.UpBars);
        Assert.Equal(1, s.First5.NewHighs);
        Assert.Null(s.OpeningRangeHigh5);
    }

    [Fact]
    public void OpeningRangeBreakDetectedAtFiveBars()
    {
        var bars = Enumerable.Range(0, 5).Select(i => new Candle(Open.AddMinutes(i), 100, 100 + i, 99, 100, 200)).ToArray();
        var s = OpeningSnapshotEvaluator.Evaluate(Input(bars, Prev(20, 5000), 110));
        Assert.Equal(104, s.OpeningRangeHigh5);
        Assert.True(s.BrokeOpeningRange);
    }

    [Fact]
    public void PrevCloseDailyBeatsBars1m()
    {
        var daily = new[] { new Candle(Open.AddDays(-1), 90, 95, 89, 93, 1000) };
        var all = new[] { new Candle(Open.AddDays(-1).AddHours(2), 80, 81, 79, 80, 100) };
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 100, daily: daily, all: all));
        Assert.Equal("daily", s.PrevCloseSource);
        Assert.Equal(93, s.PrevClose);
    }

    [Fact]
    public void PrevCloseFallsBackToBars1mBefore1600()
    {
        var all = new[]
        {
            new Candle(Open.AddDays(-1).AddMinutes(10), 80, 81, 79, 80, 100),
            new Candle(DateTimeOffset.Parse("2026-09-08T20:30:00Z"), 70, 71, 69, 70, 100),
        };
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 100, all: all));
        Assert.Equal("bars1m", s.PrevCloseSource);
        Assert.Equal(80, s.PrevClose);
    }

    [Fact]
    public void NoPremarketBarsLeavePremarketNull()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 5000), 100));
        Assert.Null(s.Premarket);
    }

    [Fact]
    public void StaleQuoteDropsPriceIndicators()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 200), 102, quoteStatus: "stale"));
        Assert.Null(s.ChangeFromOpenPercent);
        Assert.Null(s.Vwap);
        Assert.Null(s.AboveVwap);
        Assert.Contains("시세 지연 — 가격 지표 생략", s.Reasons);
        Assert.NotEqual("STRONG", s.Grade);
    }

    [Fact]
    public void ReasonsCarryVolumeAndPriceAndGap()
    {
        var daily = new[] { new Candle(Open.AddDays(-1), 90, 95, 89, 100, 1000) };
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(20, 200), 102, daily: daily));
        Assert.Contains(s.Reasons, r => r.StartsWith("거래량 "));
        Assert.Contains(s.Reasons, r => r.Contains("시가 대비 +2.0%"));
        Assert.Contains(s.Reasons, r => r.Contains("갭"));
    }

    [Fact]
    public void ScoreIsZeroWhenInputsMissing()
    {
        var s = OpeningSnapshotEvaluator.Evaluate(Input(FlatBars(5), Prev(2, 10), 100, quoteStatus: "stale"));
        Assert.Equal(0, s.Score);
    }
}
