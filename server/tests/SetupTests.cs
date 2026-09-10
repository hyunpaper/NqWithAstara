using Astra.Server;
using Xunit;

public sealed class SetupTests
{
    static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-09-08T22:30:00+09:00");

    static List<Candle> Trend(double slope)
    {
        var bars = new List<Candle>();
        for (var i = 0; i < 31; i++)
        {
            var close = 100 + i * slope;
            bars.Add(new Candle(T.AddMinutes(i), close - .05, close + .1, close - .15, close, 1000));
        }
        return bars;
    }

    static List<Candle> AddPullback(List<Candle> bars, double breakBelowZone)
    {
        for (var k = 0; k < 2; k++)
        {
            var ema = Indicators.EmaSeries(bars.Select(x => x.Close).ToArray(), 9)[^1];
            var vwap = Indicators.VwapSeries(bars)[^1];
            var zone = Math.Max(ema, vwap);
            bars.Add(new Candle(bars[^1].Timestamp.AddMinutes(1), bars[^1].Close, bars[^1].Close + .02, zone - breakBelowZone, zone + .05, 900));
        }
        return bars;
    }

    static IndicatorSnapshot Ind(List<Candle> bars)
    {
        var vwap = Indicators.Vwap(bars);
        return new(60, 0, 0, vwap, Indicators.Atr(bars), 1, 0, 0, Indicators.VwapStd(bars, vwap));
    }

    [Fact]
    public void DetectsPullbackReclaimAsSetup()
    {
        var bars = AddPullback(Trend(.01), 0);
        var prevHigh = bars[^1].High;
        bars.Add(new Candle(bars[^1].Timestamp.AddMinutes(1), bars[^1].Close, prevHigh + .2, bars[^1].Close - .05, prevHigh + .15, 1500));
        Assert.Equal("SETUP", Indicators.DetectSetup(bars, Ind(bars), 80));
    }

    [Fact]
    public void NoTriggerBarMeansNoSetup()
    {
        var bars = AddPullback(Trend(.02), 0);
        bars.Add(new Candle(bars[^1].Timestamp.AddMinutes(1), bars[^1].Close, bars[^1].Close + .01, bars[^1].Close - .1, bars[^1].Close - .02, 800));
        Assert.Null(Indicators.DetectSetup(bars, Ind(bars), 80));
    }

    [Fact]
    public void DeepVwapBreakInvalidatesSetup()
    {
        var bars = AddPullback(Trend(.02), 1.0);
        var prevHigh = bars[^1].High;
        bars.Add(new Candle(bars[^1].Timestamp.AddMinutes(1), bars[^1].Close, prevHigh + .2, bars[^1].Close - .05, prevHigh + .15, 1500));
        Assert.Null(Indicators.DetectSetup(bars, Ind(bars), 80));
    }

    [Fact]
    public void LowScoreSuppressesSetup()
    {
        var bars = AddPullback(Trend(.02), 0);
        var prevHigh = bars[^1].High;
        bars.Add(new Candle(bars[^1].Timestamp.AddMinutes(1), bars[^1].Close, prevHigh + .2, bars[^1].Close - .05, prevHigh + .15, 1500));
        Assert.Null(Indicators.DetectSetup(bars, Ind(bars), 60));
    }

    [Fact]
    public void OverextensionFlagsChase()
    {
        // 횡보하다 3봉 연속 급등: VWAP 밴드(σ) 기준 한참 위로 벗어난 상태
        var bars = Trend(.02);
        for (var k = 0; k < 3; k++)
        {
            var close = bars[^1].Close + 2;
            bars.Add(new Candle(bars[^1].Timestamp.AddMinutes(1), bars[^1].Close, close + .1, bars[^1].Close - .05, close, 1000));
        }
        Assert.Equal("CHASE", Indicators.DetectSetup(bars, Ind(bars), 80));
    }
}
