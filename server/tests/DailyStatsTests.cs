using Astra.Server;
using Xunit;

public sealed class DailyStatsTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-08T10:00:00-04:00");
    static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-09-08T16:00:00-04:00");
    static MarketSession Open() => new(true, "정규장", null, Start, End);

    static List<Candle> Fixture(double todayVolume = 750_000)
    {
        var days = Enumerable.Range(0, 24)
            .Select(i => new Candle(Start.AddDays(i - 24), 100, 110, 90, 100, 1_000_000))
            .ToList();
        days.Add(new Candle(Start, 102, 106, 101, 104, todayVolume));
        return days;
    }

    [Fact]
    public void AdjustsVolumeRatioForElapsedSession()
    {
        var now = Start.AddHours(3); // 6시간 세션의 절반 경과
        var m = DailyStats.Compute(Fixture(), Open(), now, 105);
        Assert.NotNull(m);
        Assert.Equal(1.5, m!.VolumeRatio3);
        Assert.Equal(1.5, m.VolumeRatio5);
        Assert.Equal(1.5, m.VolumeRatio20);
        Assert.Equal(50, m.SessionElapsedPercent);
        Assert.Equal(2, m.GapPercent);
        Assert.Equal(5, m.ChangeFromPrevClose);
        Assert.Equal(75, m.RangePosition20);
        Assert.Equal(5, m.MaGap5);
        Assert.Equal(5, m.MaGap20);
    }

    [Fact]
    public void FallsBackToTodayCloseWithoutLivePrice()
    {
        var m = DailyStats.Compute(Fixture(), Open(), Start.AddHours(3));
        Assert.Equal(4, m!.ChangeFromPrevClose);
    }

    [Fact]
    public void ReturnsNullOutsideRegularSession()
    {
        var closed = new MarketSession(false, "정규장 외", null, Start, End);
        Assert.Null(DailyStats.Compute(Fixture(), closed, Start.AddHours(3)));
    }

    [Fact]
    public void ReturnsNullWhenTodayCandleIsMissing()
    {
        var days = Fixture();
        days.RemoveAt(days.Count - 1); // 마지막 봉이 어제 봉
        Assert.Null(DailyStats.Compute(days, Open(), Start.AddHours(3)));
    }

    [Fact]
    public void RejectsFutureCandleInsteadOfTreatingItAsToday()
    {
        var days = Fixture();
        days.RemoveAt(days.Count - 1);
        days.Add(new Candle(Start.AddDays(1), 102, 106, 101, 104, 750_000));
        Assert.Null(DailyStats.Compute(days, Open(), Start.AddHours(3)));
    }

    [Fact]
    public void FindsSessionCandleAcrossMixedTimestampOffsets()
    {
        var days = Fixture().Select(x => x with { Timestamp = x.Timestamp.ToUniversalTime() }).ToList();
        Assert.NotNull(DailyStats.Compute(days, Open(), Start.AddHours(3)));
    }

    [Fact]
    public void ReturnsNullWhenNowIsOutsideDeclaredSession()
        => Assert.Null(DailyStats.Compute(Fixture(), Open(), End));

    [Fact]
    public void ZeroVolumeHistoryStillContributesToPriceAverages()
    {
        var days = Fixture();
        days[^2] = days[^2] with { Close = 200, Volume = 0 };
        var m = DailyStats.Compute(days, Open(), Start.AddHours(3), 105);
        Assert.NotNull(m);
        Assert.NotEqual(5, m!.MaGap5);
    }

    [Fact]
    public void RejectsNonFiniteCurrentCandle()
    {
        var days = Fixture();
        days[^1] = days[^1] with { Volume = double.NaN };
        Assert.Null(DailyStats.Compute(days, Open(), Start.AddHours(3)));
    }
}
