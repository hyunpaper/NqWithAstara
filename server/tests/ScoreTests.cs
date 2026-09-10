using Astra.Server;
using Xunit;

public sealed class ScoreTests
{
    static List<Candle> FlatThenPush(double lastClose)
    {
        var t = DateTimeOffset.Parse("2026-09-08T22:30:00+09:00");
        var bars = Enumerable.Range(0, 34)
            .Select(i => new Candle(t.AddMinutes(i), 100, 100.5, 99.5, 100, 1000))
            .ToList();
        bars.Add(new Candle(t.AddMinutes(34), 100, Math.Max(lastClose, 100) + .5, 99.5, lastClose, 1000));
        return bars;
    }

    [Fact]
    public void StrongerTrendScoresHigherThanWeakerTrend()
    {
        var weak = Indicators.Evaluate(FlatThenPush(100.8));
        var strong = Indicators.Evaluate(FlatThenPush(101.4));
        Assert.True(strong.Score > weak.Score, $"strong {strong.Score} <= weak {weak.Score}");
    }

    [Fact]
    public void OverextensionFromVwapIsPenalized()
    {
        var moderate = Indicators.Evaluate(FlatThenPush(102));
        var blowoff = Indicators.Evaluate(FlatThenPush(107));
        Assert.True(blowoff.Score < moderate.Score, $"blowoff {blowoff.Score} >= moderate {moderate.Score}");
        Assert.Contains(blowoff.Reasons, r => r.Contains("추격 주의"));
    }

    [Fact]
    public void ScoreStaysInBounds()
    {
        foreach (var close in new[] { 90.0, 96, 100, 103, 115 })
        {
            var r = Indicators.Evaluate(FlatThenPush(close));
            Assert.InRange(r.Score, 0, 100);
        }
    }
}
