using System.Collections.Immutable;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Xunit;

/// <summary>C3 기법 어댑터 1군의 score·confidence·warmup 계약 (C3, #167).</summary>
public sealed class ConfluenceTechniqueTests
{
    static readonly ConfluencePolicy Policy = ConfluencePolicy.Default;

    // ── MACD ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MacdIsPositiveAndFullyConfidentWhenTheHistogramRisesAboveZero()
    {
        var bars = Cf.Closes(Cf.Flat(30, 100).Concat(Cf.Slope(30, 100, .5)));
        var signal = ConfluenceTechniques.Macd(Cf.Input(bars), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
        Assert.True(Cf.Evidence(signal, "histogram") > 0);
    }

    [Fact]
    public void MacdIsNegativeAndHalfConfidentBelowTheZeroLine()
    {
        var bars = Cf.Closes(Cf.Flat(30, 100).Concat(Cf.Slope(30, 100, -.5)));
        var signal = ConfluenceTechniques.Macd(Cf.Input(bars), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score < 0);
        Assert.Equal(Policy.MacdBelowZeroConfidence, signal.Confidence);
    }

    [Fact]
    public void MacdIsWarmupBeforeTheSignalLineExists()
    {
        var signal = ConfluenceTechniques.Macd(Cf.Input(Cf.Ramp(30, 100, .1)), Policy);
        Assert.True(signal.Warmup);
        Assert.Equal(0, signal.Confidence);
        Assert.Equal(0, signal.Score);
    }

    [Fact]
    public void MacdCrossBonusOnlyAppearsOnBarsAboveTheZeroLine()
    {
        var closes = Cf.Flat(30, 100).Concat(Cf.Slope(25, 100, .5)).Concat(Cf.Slope(5, 112.5, -.8))
            .Concat(Cf.Slope(10, 108.5, .8)).ToArray();
        var bonused = new List<TechniqueSignal>();
        for (var length = Macd.RequiredBars() + 1; length <= closes.Length; length++)
        {
            var signal = ConfluenceTechniques.Macd(Cf.Input(Cf.Closes(closes.Take(length))), Policy);
            if (Cf.Evidence(signal, "crossBonus") > 0) bonused.Add(signal);
        }
        Assert.NotEmpty(bonused);
        Assert.All(bonused, x =>
        {
            Assert.True(Cf.Evidence(x, "macd") > 0);
            Assert.Equal(1, x.Confidence);
        });
    }

    // ── RSI ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RsiIsPositiveWhenEveryCloseGainsAndTheTrendIsStrong()
    {
        var signal = ConfluenceTechniques.Rsi(Cf.Input(Cf.Ramp(60, 100, .3)), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void RsiIsNegativeWhenEveryCloseLoses()
    {
        var signal = ConfluenceTechniques.Rsi(Cf.Input(Cf.Ramp(60, 130, -.3)), Policy);
        Assert.True(signal.Score < 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void RsiIsWarmupBeforeFourteenBars()
    {
        var signal = ConfluenceTechniques.Rsi(Cf.Input(Cf.Ramp(10, 100, .3)), Policy);
        Assert.True(signal.Warmup);
        Assert.Equal(0, signal.Confidence);
    }

    [Fact]
    public void RsiFallsBackToTheNonTrendConfidenceWhenAdxIsBelowThreshold()
    {
        var signal = ConfluenceTechniques.Rsi(Cf.Input(Cf.Zigzag(60, 100, .5)), Policy);
        Assert.False(signal.Warmup);
        Assert.True(Cf.Evidence(signal, "adx") < Policy.TrendAdxThreshold);
        Assert.Equal(Policy.RsiNonTrendConfidence, signal.Confidence);
    }

    // ── BB %B ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PercentBIsPositiveWhenTheCloseSitsAtTheUpperBand()
    {
        var bars = Cf.Closes(Cf.Zigzag(30, 100, .2).Select(x => (double)x.Close).Append(104));
        var signal = ConfluenceTechniques.BollingerPercentB(Cf.Input(bars), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
    }

    [Fact]
    public void PercentBIsNegativeWhenTheCloseSitsAtTheLowerBand()
    {
        var bars = Cf.Closes(Cf.Zigzag(30, 100, .2).Select(x => (double)x.Close).Append(96));
        var signal = ConfluenceTechniques.BollingerPercentB(Cf.Input(bars), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void PercentBIsWarmupBeforeTwentyBars()
    {
        var signal = ConfluenceTechniques.BollingerPercentB(Cf.Input(Cf.Zigzag(10, 100, .2)), Policy);
        Assert.True(signal.Warmup);
        Assert.Equal(0, signal.Confidence);
    }

    [Fact]
    public void PercentBAddsTheSqueezeBonusOnlyWhenTheBandWidthLowWasSetOnThePreviousBar()
    {
        var closes = Enumerable.Range(0, 20).Select(i => 100 + (i % 2 == 0 ? 2.0 : -2.0))
            .Concat(Enumerable.Range(0, 20).Select(i => 100 + (i % 2 == 0 ? .01 : -.01)))
            .Append(112.0).ToArray();
        var squeezed = ConfluenceTechniques.BollingerPercentB(Cf.Input(Cf.Closes(closes)), Policy);
        Assert.Equal(Policy.BollingerSqueezeBonus, Cf.Evidence(squeezed, "squeezeBonus"));
        Assert.Equal(Policy.BollingerExtremeConfidence, squeezed.Confidence);

        var withoutBreakout = ConfluenceTechniques.BollingerPercentB(
            Cf.Input(Cf.Closes(closes.SkipLast(1).Append(100.0))), Policy);
        Assert.Equal(0, Cf.Evidence(withoutBreakout, "squeezeBonus"));
    }

    // ── ADX/DMI ───────────────────────────────────────────────────────────────

    [Fact]
    public void AdxIsPositiveWhenPlusDiLeadsAStrongTrend()
    {
        var signal = ConfluenceTechniques.AdxDmi(Cf.Input(Cf.Ramp(60, 100, .3)), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void AdxIsNegativeWhenMinusDiLeads()
    {
        var signal = ConfluenceTechniques.AdxDmi(Cf.Input(Cf.Ramp(60, 130, -.3)), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void AdxIsWarmupBeforeTwiceThePeriod()
    {
        var signal = ConfluenceTechniques.AdxDmi(Cf.Input(Cf.Ramp(20, 100, .3)), Policy);
        Assert.True(signal.Warmup);
    }

    [Fact]
    public void AdxUsesTheLowConfidenceOutsideATrend()
    {
        var signal = ConfluenceTechniques.AdxDmi(Cf.Input(Cf.Zigzag(60, 100, .5)), Policy);
        Assert.False(signal.Warmup);
        Assert.Equal(Policy.AdxLowConfidence, signal.Confidence);
    }

    // ── VWAP 이격 ─────────────────────────────────────────────────────────────

    [Fact]
    public void VwapDeviationIsPositiveAboveTheAnchoredAverage()
    {
        var signal = ConfluenceTechniques.VwapDeviation(Cf.Input(Cf.Ramp(40, 100, .2)), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void VwapDeviationIsNegativeBelowTheAnchoredAverage()
    {
        var signal = ConfluenceTechniques.VwapDeviation(Cf.Input(Cf.Ramp(40, 120, -.2)), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void VwapDeviationIsWarmupWhileTheWeightedSigmaIsZero()
    {
        var signal = ConfluenceTechniques.VwapDeviation(Cf.Input(Cf.Closes(Cf.Flat(5, 100))), Policy);
        Assert.True(signal.Warmup);
        Assert.Equal(0, signal.Confidence);
    }

    // ── RVOL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RelativeVolumeIsPositiveWhenVolumeExpandsOnAnUpDay()
    {
        var signal = ConfluenceTechniques.RelativeVolume(Cf.Input(Cf.Ramp(30, 100, .2), relativeVolume: 2.5), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void RelativeVolumeIsNegativeWhenVolumeExpandsOnADownDay()
    {
        var signal = ConfluenceTechniques.RelativeVolume(Cf.Input(Cf.Ramp(30, 120, -.2), relativeVolume: 2.5), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void RelativeVolumeIsWarmupWithoutAnyBar()
    {
        var signal = ConfluenceTechniques.RelativeVolume(Cf.Input([], relativeVolume: 2.5), Policy);
        Assert.True(signal.Warmup);
    }

    [Fact]
    public void RelativeVolumeDropsToZeroConfidenceWithoutASample()
    {
        var signal = ConfluenceTechniques.RelativeVolume(Cf.Input(Cf.Ramp(30, 100, .2)), Policy);
        Assert.False(signal.Warmup);
        Assert.Equal(0, signal.Confidence);
        Assert.Equal(0, signal.Score);
    }

    // ── ATR 채널 ──────────────────────────────────────────────────────────────

    [Fact]
    public void AtrChannelIsPositiveAboveTheMovingAverage()
    {
        var signal = ConfluenceTechniques.AtrChannel(Cf.Input(Cf.Ramp(40, 100, .2)), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void AtrChannelIsNegativeBelowTheMovingAverage()
    {
        var signal = ConfluenceTechniques.AtrChannel(Cf.Input(Cf.Ramp(40, 120, -.2)), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void AtrChannelIsWarmupBeforeTheChannelBasisExists()
    {
        var signal = ConfluenceTechniques.AtrChannel(Cf.Input(Cf.Ramp(10, 100, .2)), Policy);
        Assert.True(signal.Warmup);
    }

    [Fact]
    public void AtrChannelClampsFarExcursionsToOne()
    {
        var bars = Cf.Closes(Cf.Flat(30, 100).Append(180.0));
        var signal = ConfluenceTechniques.AtrChannel(Cf.Input(bars), Policy);
        Assert.Equal(1, signal.Score);
    }

    // ── ORB(15) ───────────────────────────────────────────────────────────────

    [Fact]
    public void OpeningRangeIsPositiveAboveTheRangeHigh()
    {
        var signal = ConfluenceTechniques.OpeningRange(Cf.Input(Cf.Ramp(25, 100, .2)), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void OpeningRangeIsNegativeBelowTheRangeLow()
    {
        var signal = ConfluenceTechniques.OpeningRange(Cf.Input(Cf.Ramp(25, 120, -.2)), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void OpeningRangeIsZeroInsideTheRange()
    {
        var closes = Cf.Slope(15, 100, .2).Concat(Cf.Flat(5, 101.5)).ToArray();
        var signal = ConfluenceTechniques.OpeningRange(Cf.Input(Cf.Closes(closes)), Policy);
        Assert.False(signal.Warmup);
        Assert.Equal(0, signal.Score);
    }

    [Fact]
    public void OpeningRangeIsWarmupUntilTheFifteenMinuteWindowIsComplete()
    {
        Assert.True(ConfluenceTechniques.OpeningRange(Cf.Input(Cf.Ramp(10, 100, .2)), Policy).Warmup);
        Assert.True(ConfluenceTechniques.OpeningRange(Cf.Input(Cf.Ramp(15, 100, .2)), Policy).Warmup);
        Assert.False(ConfluenceTechniques.OpeningRange(Cf.Input(Cf.Ramp(16, 100, .2)), Policy).Warmup);
    }

    // ── RS vs QQQ ─────────────────────────────────────────────────────────────

    [Fact]
    public void RelativeStrengthIsPositiveWhenTheSymbolOutrunsTheBenchmark()
    {
        var bars = Cf.Ramp(40, 100, .2);
        var signal = ConfluenceTechniques.RelativeStrength(Cf.Input(bars, Cf.Closes(Cf.Flat(40, 300))), Policy);
        Assert.False(signal.Warmup);
        Assert.True(signal.Score > 0);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void RelativeStrengthIsNegativeWhenTheBenchmarkOutrunsTheSymbol()
    {
        var bars = Cf.Ramp(40, 100, .05);
        var signal = ConfluenceTechniques.RelativeStrength(Cf.Input(bars, Cf.Ramp(40, 300, 3)), Policy);
        Assert.True(signal.Score < 0);
    }

    [Fact]
    public void RelativeStrengthIsWarmupBeforeAtrExists()
    {
        var signal = ConfluenceTechniques.RelativeStrength(
            Cf.Input(Cf.Ramp(5, 100, .2), Cf.Closes(Cf.Flat(5, 300))), Policy);
        Assert.True(signal.Warmup);
    }

    [Fact]
    public void RelativeStrengthDropsToZeroConfidenceWhenTheBenchmarkIsOutOfSync()
    {
        var bars = Cf.Ramp(40, 100, .2);
        var stale = Cf.Closes(Cf.Flat(35, 300));
        var stalled = ConfluenceTechniques.RelativeStrength(Cf.Input(bars, stale), Policy);
        Assert.False(stalled.Warmup);
        Assert.Equal(0, stalled.Confidence);
        Assert.Equal(0, stalled.Score);

        var missing = ConfluenceTechniques.RelativeStrength(Cf.Input(bars), Policy);
        Assert.Equal(0, missing.Confidence);

        var shifted = Cf.Shifted(Cf.Closes(Cf.Flat(40, 300)), TimeSpan.FromSeconds(30));
        Assert.Equal(1, ConfluenceTechniques.RelativeStrength(Cf.Input(bars, shifted), Policy).Confidence);
    }

    // ── OBI ───────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderBookImbalanceIsPositiveWhenBidSizeDominates()
    {
        var signal = ConfluenceTechniques.OrderBookImbalance(Cf.Input(Cf.Ramp(30, 100, .2), book: Book(
            (300, 100), (300, 100), (300, 100))), Policy);
        Assert.False(signal.Warmup);
        Assert.Equal(.5, signal.Score, 6);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void OrderBookImbalanceIsNegativeWhenAskSizeDominates()
    {
        var signal = ConfluenceTechniques.OrderBookImbalance(Cf.Input(Cf.Ramp(30, 100, .2), book: Book(
            (100, 300), (100, 300), (100, 300))), Policy);
        Assert.Equal(-.5, signal.Score, 6);
    }

    [Fact]
    public void OrderBookImbalanceDropsToZeroConfidenceWithoutAQuote()
    {
        var signal = ConfluenceTechniques.OrderBookImbalance(Cf.Input(Cf.Ramp(30, 100, .2)), Policy);
        Assert.False(signal.Warmup);
        Assert.Equal(0, signal.Confidence);
        Assert.Equal(0, signal.Score);
    }

    [Fact]
    public void OrderBookImbalanceAveragesOnlyTheMostRecentPolls()
    {
        var signal = ConfluenceTechniques.OrderBookImbalance(Cf.Input(Cf.Ramp(30, 100, .2), book: Book(
            (1000, 0), (1000, 0), (300, 100), (100, 300), (200, 200))), Policy);
        Assert.Equal((0.5 - 0.5 + 0) / 3, signal.Score, 6);
    }

    static ImmutableArray<OrderBookSnapshot> Book(params (double Bid, double Ask)[] snapshots) =>
        snapshots.Select((x, i) => new OrderBookSnapshot(Cf.SessionStart.AddSeconds(i * 15), x.Bid, x.Ask))
            .ToImmutableArray();
}
