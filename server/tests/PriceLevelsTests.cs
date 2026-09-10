using Astra.Server;
using Xunit;

public sealed class PriceLevelsTests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-08T22:30:00+09:00");

    static List<Candle> Intraday()
    {
        // 99~101 사이 횡보, 100 부근에 거래량 집중(매물대), 마지막에 103까지 상승
        var bars = new List<Candle>();
        for (var i = 0; i < 30; i++)
        {
            var px = 100 + (i % 3 - 1) * .5; // 99.5 / 100 / 100.5 순환
            bars.Add(new Candle(T.AddMinutes(i), px, px + .3, px - .3, px, px == 100 ? 9000 : 1000));
        }
        for (var i = 30; i < 36; i++)
            bars.Add(new Candle(T.AddMinutes(i), 100 + (i - 30) * .5, 100.8 + (i - 30) * .5, 99.9 + (i - 30) * .5, 100.5 + (i - 30) * .5, 1200));
        return bars;
    }

    static List<Candle> Daily() =>
    [
        .. Enumerable.Range(0, 20).Select(i => new Candle(T.AddDays(i - 21), 95, 105, 90, 95 + i * .2, 1_000_000)),
        new Candle(T.AddDays(-1), 98, 102.4, 96.5, 99, 1_000_000), // 전일
        new Candle(T, 100, 103.8, 99.4, 103, 500_000), // 오늘(진행 중)
    ];

    [Fact]
    public void BuildsSupportAndResistanceLevels()
    {
        var levels = PriceLevels.Compute(Intraday(), Daily(), 1);
        var dump = string.Join(" | ", levels.Select(x => $"{x.Label}@{x.Price:0.##}"));
        Assert.True(levels.Any(x => x.Label == "전일 고가" && Math.Abs(x.Price - 102.4) < .01), dump);
        Assert.True(levels.Any(x => x.Label.StartsWith("매물대")), dump);
        Assert.True(levels.Any(x => x.Label.Contains("저가")), dump);
    }

    [Fact]
    public void StopSitsBelowNearestSupportAndTargetAtResistance()
    {
        var levels = PriceLevels.Compute(Intraday(), Daily(), 1);
        var p = PriceLevels.Enter(103, 1, 1, levels);
        Assert.NotNull(p.Stop);
        Assert.NotNull(p.Target);
        Assert.True(p.Stop < 103 - .5, $"stop {p.Stop}");
        Assert.True(p.Target > 103.5, $"target {p.Target}");
        Assert.NotNull(p.StopBasis);
        Assert.NotNull(p.TargetBasis);
    }

    [Fact]
    public void FallsBackToAtrWhenNoLevels()
    {
        var p = PriceLevels.Enter(100, 1, 2, []);
        Assert.Equal(106, p.Target); // 3 ATR
        Assert.Equal(95, p.Stop); // 2.5 ATR
    }

    [Fact]
    public void UsesLatestCompletedDailyBarWhenTodayBarIsMissing()
    {
        var daily = Daily();
        daily.RemoveAt(daily.Count - 1);
        var levels = PriceLevels.Compute(Intraday(), daily, 1);
        Assert.Contains(levels, x => x.Label == "전일 고가" && Math.Abs(x.Price - 102.4) < .01);
    }

    [Fact]
    public void IgnoresFutureDailyBarsWhenSelectingPreviousSession()
    {
        var daily = Daily();
        daily.Add(new Candle(T.AddDays(1), 200, 220, 180, 210, 1_000_000));
        var levels = PriceLevels.Compute(Intraday(), daily, 1);
        Assert.Contains(levels, x => x.Label == "전일 고가" && Math.Abs(x.Price - 102.4) < .01);
        Assert.DoesNotContain(levels, x => x.Price >= 180);
    }

    [Fact]
    public void SelectsPreviousSessionAcrossMixedTimestampOffsets()
    {
        var intraday = Intraday().Select(x => x with { Timestamp = x.Timestamp.ToOffset(TimeSpan.FromHours(9)) }).ToList();
        var daily = Daily().Select(x => x with { Timestamp = x.Timestamp.ToUniversalTime() }).ToList();
        var levels = PriceLevels.Compute(intraday, daily, 1);
        Assert.Contains(levels, x => x.Label == "전일 고가" && Math.Abs(x.Price - 102.4) < .01);
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(.005, 1, 1)]
    [InlineData(100, 0, 1)]
    [InlineData(100, 1, -1)]
    public void RejectsInvalidEntryInputs(double entry, double quantity, double atr)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PriceLevels.Enter(entry, quantity, atr, []));

    [Fact]
    public void EmptyLevelsStillApplyMinimumRiskAndTargetPolicies()
    {
        var p = PriceLevels.Enter(100, 1, .01, []);
        Assert.Equal(99.55, p.Stop);
        Assert.Equal(100.5, p.Target);
    }


    [Fact]
    public void RoundedPlanKeepsStrictPriceOrdering()
    {
        var p = PriceLevels.Enter(100.009, 1, .01, []);
        Assert.True(0 < p.Stop && p.Stop < p.EntryPrice && p.EntryPrice < p.Target);
    }
}
