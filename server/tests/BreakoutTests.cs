using Astra.Server;
using Xunit;

public sealed class BreakoutTests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-09T22:30:00+09:00");
    static readonly PriceLevel[] Levels = [new(101, "전일 고가"), new(103, "매물대 집중")];

    static List<Candle> Bars(double prevClose, double lastClose, double lastOpen)
    {
        var bars = Enumerable.Range(0, 20)
            .Select(i => new Candle(T.AddMinutes(i), 100, 100.5, 99.5, 100, 1000)).ToList();
        bars[^1] = bars[^1] with { Close = prevClose };
        bars.Add(new Candle(T.AddMinutes(20), lastOpen, Math.Max(lastClose, lastOpen) + .1, lastOpen - .1, lastClose, 3000));
        return bars;
    }

    [Fact]
    public void DetectsUpwardCrossOfResistanceWithVolume()
    {
        var hit = PriceLevels.DetectBreakout(Bars(100.8, 101.4, 100.9), Levels, 2.5);
        Assert.NotNull(hit);
        Assert.Equal("전일 고가", hit!.Label);
    }

    [Fact]
    public void PicksHighestLevelWhenCrossingMultiple()
    {
        var hit = PriceLevels.DetectBreakout(Bars(100.8, 103.5, 100.9), Levels, 2.5);
        Assert.Equal("매물대 집중", hit!.Label);
    }

    [Fact]
    public void RequiresVolumeAndUpBarAndCross()
    {
        Assert.Null(PriceLevels.DetectBreakout(Bars(100.8, 101.4, 100.9), Levels, 1.2)); // 거래량 부족
        Assert.Null(PriceLevels.DetectBreakout(Bars(100.8, 101.4, 101.6), Levels, 2.5)); // 음봉
        Assert.Null(PriceLevels.DetectBreakout(Bars(101.5, 102.0, 101.6), Levels, 2.5)); // 이미 레벨 위
    }
}
