using Astra.Server;
using Xunit;
namespace Astra.Server.Tests;
public class IndicatorTests
{
    [Fact] public void EmaRecurrence() => Assert.Equal(3.125, Indicators.Ema(new double[] { 1, 2, 3, 4 }, 3), 8);
    [Fact] public void RsiAllGains() => Assert.Equal(100, Indicators.Rsi(Enumerable.Range(1, 20).Select(x => (double)x).ToArray()));
    [Fact] public void BollingerSampleStd() { var b = Indicators.Bollinger(new double[] { 1, 2, 3 }, 3, 1); Assert.Equal(1, b.Lower, 8); Assert.Equal(3, b.Upper, 8); }
    [Fact] public void AtrIncludesGaps() { var t = DateTimeOffset.UtcNow; var b = new List<Candle> { new(t, 10, 11, 9, 10, 1) }; for (var i = 1; i <= 14; i++) b.Add(new(t.AddMinutes(i), 12, 13, 11, 12, 1)); Assert.True(Indicators.Atr(b) >= 2); }
    [Fact] public void WarmupRequired() => Assert.Throws<ArgumentException>(() => Indicators.Evaluate(Array.Empty<Candle>()));
    [Fact] public void RiskPendingThenFrozenOnce() { var pending = MarketRules.Enter(100, 2, null); Assert.Null(pending.Target); var frozen = MarketRules.FreezeRisk(pending, 3); Assert.Equal(109, frozen.Target); Assert.Equal(92.5, frozen.Stop); Assert.Equal(frozen, MarketRules.FreezeRisk(frozen, 10)); }
    [Fact] public void SessionEndExclusive() { var s = DateTimeOffset.Parse("2026-11-27T23:30:00+09:00"); var e = DateTimeOffset.Parse("2026-11-28T03:00:00+09:00"); Assert.True(MarketRules.IsOpen(s, s, e)); Assert.False(MarketRules.IsOpen(e, s, e)); }
    [Fact] public void FiltersPremarketCurrentAndFutureBars() { var s = DateTimeOffset.Parse("2026-09-08T22:30:00+09:00"); var session = new MarketSession(true, "정규장", null, s, s.AddHours(6.5)); var bars = new[] { new Candle(s.AddMinutes(-1), 1, 1, 1, 1, 1), new Candle(s, 1, 1, 1, 1, 1), new Candle(s.AddMinutes(1), 1, 1, 1, 1, 1), new Candle(s.AddMinutes(2), 1, 1, 1, 1, 1) }; var result = MarketRules.CompletedRegularBars(bars, session, s.AddMinutes(2)); Assert.Equal(2, result.Length); }
}
