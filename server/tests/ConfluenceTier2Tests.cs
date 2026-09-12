using System.Collections.Immutable;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Xunit;

/// <summary>C3-2 2군 기법 어댑터 (#170).</summary>
public sealed class ConfluenceTier2Tests
{
    static readonly ConfluencePolicy Policy = ConfluencePolicy.Default;

    static ImmutableArray<IndicatorBar> WithPattern(int priorCount, Func<IndicatorBar, int, IndicatorBar> pattern)
    {
        var bars = Cf.Downtrend(priorCount, 100, .3);
        return bars.Add(pattern(bars[^1], priorCount));
    }

    static IndicatorBar BullishEngulfing(IndicatorBar previous, int index) => Cf.Ohlc(index,
        (double)previous.Close - .02, (double)previous.Open + .07, (double)previous.Low - .45,
        (double)previous.Open + .05);

    static IndicatorBar Hammer(IndicatorBar previous, int index)
    {
        var bottom = (double)previous.Low - .01;
        return Cf.Ohlc(index, bottom, bottom + .03, bottom - 1, bottom + .02);
    }

    static IndicatorBar PinBar(IndicatorBar previous, int index)
    {
        var bottom = (double)previous.Close + .2;
        return Cf.Ohlc(index, bottom, bottom + .03, (double)previous.Low - .5, bottom + .02);
    }

    [Fact]
    public void Candle_scores_a_bullish_engulfing_that_makes_a_new_five_bar_low()
    {
        var signal = ConfluenceTechniques.Candle(Cf.Input(WithPattern(20, BullishEngulfing)), Policy);

        Assert.False(signal.Warmup);
        Assert.Equal(Policy.CandleEngulfingScore, signal.Score, 4);
        Assert.Equal(1, signal.Confidence);
        Assert.Equal(1, Cf.Evidence(signal, "engulfing"));
        Assert.Equal(1, Cf.Evidence(signal, "newLow"));
    }

    [Fact]
    public void Candle_scores_a_hammer_below_the_engulfing_score_and_lowers_confidence_for_a_small_body()
    {
        var signal = ConfluenceTechniques.Candle(Cf.Input(WithPattern(20, Hammer)), Policy);

        Assert.Equal(Policy.CandleHammerScore, signal.Score, 4);
        Assert.Equal(Policy.CandleWeakBodyConfidence, signal.Confidence, 4);
        Assert.Equal(0, Cf.Evidence(signal, "engulfing"));
        Assert.Equal(1, Cf.Evidence(signal, "hammer"));
    }

    [Fact]
    public void Candle_scores_a_pin_bar_when_the_hammer_definition_does_not_hold()
    {
        var signal = ConfluenceTechniques.Candle(Cf.Input(WithPattern(20, PinBar)), Policy);

        Assert.Equal(0, Cf.Evidence(signal, "hammer"));
        Assert.Equal(1, Cf.Evidence(signal, "pinBar"));
        Assert.Equal(Policy.CandlePinBarScore, signal.Score, 4);
    }

    [Fact]
    public void Candle_stays_flat_when_the_pattern_bar_does_not_make_a_new_low()
    {
        var bars = Cf.Downtrend(20, 100, .3);
        var previous = bars[^1];
        var shallow = Cf.Ohlc(20, (double)previous.Close - .02, (double)previous.Open + .07,
            (double)previous.Low + .01, (double)previous.Open + .05);

        var signal = ConfluenceTechniques.Candle(Cf.Input(bars.Add(shallow)), Policy);

        Assert.False(signal.Warmup);
        Assert.Equal(0, signal.Score);
        Assert.Equal(1, Cf.Evidence(signal, "engulfing"));
        Assert.Equal(0, Cf.Evidence(signal, "newLow"));
    }

    [Fact]
    public void Candle_is_warmup_before_atr_and_the_prior_low_window_are_available()
    {
        Assert.True(ConfluenceTechniques.Candle(Cf.Input(Cf.Downtrend(4, 100, .3)), Policy).Warmup);
        Assert.True(ConfluenceTechniques.Candle(Cf.Input([]), Policy).Warmup);
    }
    [Theory]
    [InlineData(3, 3, 1)]
    [InlineData(2, 3, 0.3333)]
    [InlineData(1, 3, -0.3333)]
    [InlineData(0, 3, -1)]
    [InlineData(2, 2, 1)]
    [InlineData(1, 2, 0)]
    public void MtaAlign_maps_the_alignment_ratio_onto_the_full_range(int aligned, int total, double expected)
    {
        Assert.Equal(expected, ConfluenceTechniques.AlignmentScore(aligned, total), 4);
    }

    [Fact]
    public void MtaAlign_scores_a_full_three_timeframe_uptrend_with_confidence_one()
    {
        var signal = ConfluenceTechniques.MultiTimeframeAlignment(Cf.Input(Cf.Ramp(330, 100, .05)), Policy);

        Assert.False(signal.Warmup);
        Assert.Equal(1, signal.Score, 4);
        Assert.Equal(1, signal.Confidence);
        Assert.Equal(3, Cf.Evidence(signal, "timeframes"));
        Assert.Equal(3, Cf.Evidence(signal, "aligned"));
    }

    [Fact]
    public void MtaAlign_drops_confidence_to_the_policy_value_before_the_fifteen_minute_ema_exists()
    {
        var signal = ConfluenceTechniques.MultiTimeframeAlignment(Cf.Input(Cf.Ramp(200, 100, .05)), Policy);

        Assert.Equal(Policy.MtaHigherTimeframeWarmupConfidence, signal.Confidence, 4);
        Assert.Equal(2, Cf.Evidence(signal, "timeframes"));
        Assert.Equal(1, signal.Score, 4);
        Assert.Null(Cf.Evidence(signal, "align15m"));
    }

    [Fact]
    public void MtaAlign_goes_negative_when_no_timeframe_is_aligned()
    {
        var signal = ConfluenceTechniques.MultiTimeframeAlignment(Cf.Input(Cf.Ramp(200, 100, -.05)), Policy);

        Assert.Equal(-1, signal.Score, 4);
        Assert.Equal(0, Cf.Evidence(signal, "aligned"));
    }

    [Fact]
    public void MtaAlign_is_warmup_until_the_five_minute_ema21_exists()
    {
        Assert.True(ConfluenceTechniques.MultiTimeframeAlignment(Cf.Input(Cf.Ramp(100, 100, .05)), Policy).Warmup);
        Assert.True(ConfluenceTechniques.MultiTimeframeAlignment(Cf.Input([]), Policy).Warmup);
    }
    static ImmutableArray<IndicatorBar> SqueezeThen(params double[] closes)
    {
        var bars = Cf.Closes(Cf.Flat(30, 100));
        foreach (var close in closes)
        {
            var index = bars.Length;
            bars = bars.Add(Cf.Ohlc(index, 100, Math.Max(100, close) + .2, Math.Min(100, close) - .2, close));
        }
        return bars;
    }

    [Fact]
    public void Squeeze_scores_the_release_bar_that_closes_above_the_keltner_upper_band()
    {
        var signal = ConfluenceTechniques.Squeeze(Cf.Input(SqueezeThen(105)), Policy);

        Assert.False(signal.Warmup);
        Assert.Equal(Policy.SqueezeReleaseScore, signal.Score, 4);
        Assert.Equal(1, signal.Confidence);
        Assert.Equal(1, Cf.Evidence(signal, "released"));
    }

    [Fact]
    public void Squeeze_scores_the_release_bar_that_closes_below_the_keltner_lower_band()
    {
        var signal = ConfluenceTechniques.Squeeze(Cf.Input(SqueezeThen(95)), Policy);

        Assert.Equal(-Policy.SqueezeReleaseScore, signal.Score, 4);
        Assert.Equal(1, signal.Confidence);
    }

    [Fact]
    public void Squeeze_scores_only_the_release_bar_and_not_the_bar_after_it()
    {
        var signal = ConfluenceTechniques.Squeeze(Cf.Input(SqueezeThen(105, 105.2)), Policy);

        Assert.Equal(0, signal.Score);
        Assert.Equal(Policy.SqueezeIdleConfidence, signal.Confidence, 4);
        Assert.Equal(0, Cf.Evidence(signal, "released"));
        Assert.Equal(0, Cf.Evidence(signal, "squeeze"));
    }

    [Fact]
    public void Squeeze_waits_with_a_lower_confidence_while_the_bands_stay_inside_the_channel()
    {
        var signal = ConfluenceTechniques.Squeeze(Cf.Input(Cf.Closes(Cf.Flat(30, 100))), Policy);

        Assert.Equal(0, signal.Score);
        Assert.Equal(Policy.SqueezeActiveConfidence, signal.Confidence, 4);
        Assert.Equal(1, Cf.Evidence(signal, "squeeze"));
    }

    [Fact]
    public void Squeeze_is_warmup_before_both_bands_exist_on_the_previous_bar()
    {
        Assert.True(ConfluenceTechniques.Squeeze(Cf.Input(Cf.Closes(Cf.Flat(20, 100))), Policy).Warmup);
        Assert.True(ConfluenceTechniques.Squeeze(Cf.Input([]), Policy).Warmup);
    }
    static ImmutableArray<IndicatorBar> PreviousDaily(double close, double high = 102, double low = 100)
    {
        var day = Cf.SessionStart.AddDays(-1);
        return Enumerable.Range(0, 3)
            .Select(i => new IndicatorBar(day.AddDays(-i), day.AddDays(-i).AddHours(6.5), (decimal)close,
                (decimal)high, (decimal)low, (decimal)close, 1_000_000m))
            .Reverse()
            .ToImmutableArray();
    }

    [Fact]
    public void VolatilityBreakout_clamps_a_wide_breakout_at_one_atr()
    {
        var signal = ConfluenceTechniques.VolatilityBreakout(
            Cf.Input(Cf.Ramp(30, 100, .05), previousDaily: PreviousDaily(99)), Policy);

        Assert.False(signal.Warmup);
        Assert.Equal(1, signal.Score, 4);
        Assert.Equal(1, signal.Confidence);
        Assert.Equal(101, Cf.Evidence(signal, "target"));
        Assert.Equal(.5, Cf.Evidence(signal, "k"));
    }

    [Fact]
    public void VolatilityBreakout_scales_a_narrow_breakout_by_atr()
    {
        var bars = Cf.Closes(Cf.Flat(29, 100)).Add(Cf.Bar(29, 101.05));

        var signal = ConfluenceTechniques.VolatilityBreakout(
            Cf.Input(bars, previousDaily: PreviousDaily(99)), Policy);

        Assert.Equal(.05 / Cf.Evidence(signal, "atr")!.Value, signal.Score, 3);
        Assert.InRange(signal.Score, .1, .3);
    }

    [Fact]
    public void VolatilityBreakout_goes_negative_in_proportion_to_the_previous_range_below_the_target()
    {
        var bars = Cf.Closes(Cf.Flat(29, 100)).Add(Cf.Bar(29, 100.5));

        var signal = ConfluenceTechniques.VolatilityBreakout(
            Cf.Input(bars, previousDaily: PreviousDaily(99)), Policy);

        Assert.Equal(-.25, signal.Score, 4);
    }

    [Fact]
    public void VolatilityBreakout_halves_confidence_when_the_session_opens_below_the_three_day_average()
    {
        var signal = ConfluenceTechniques.VolatilityBreakout(
            Cf.Input(Cf.Ramp(30, 100, .05), previousDaily: PreviousDaily(101)), Policy);

        Assert.Equal(Policy.VolatilityBreakoutFilterConfidence, signal.Confidence, 4);
        Assert.Equal(101, Cf.Evidence(signal, "filterAverage"));
    }

    [Fact]
    public void VolatilityBreakout_is_warmup_without_a_previous_session_range()
    {
        Assert.True(ConfluenceTechniques.VolatilityBreakout(Cf.Input(Cf.Ramp(30, 100, .05)), Policy).Warmup);
        Assert.True(ConfluenceTechniques.VolatilityBreakout(
            Cf.Input(Cf.Ramp(30, 100, .05), previousDaily: PreviousDaily(99, high: 100, low: 100)), Policy).Warmup);
        Assert.True(ConfluenceTechniques.VolatilityBreakout(
            Cf.Input(Cf.Ramp(10, 100, .05), previousDaily: PreviousDaily(99)), Policy).Warmup);
    }
    static ImmutableArray<SessionVolumeProfile> Profiles(int count, int minutes, decimal perMinute) =>
        Enumerable.Range(0, count)
            .Select(i => new SessionVolumeProfile(new DateOnly(2026, 8, 1).AddDays(i),
                [..Enumerable.Range(1, minutes).Select(k => perMinute * k)]))
            .ToImmutableArray();

    [Fact]
    public void SessionVolumeProfile_carries_the_last_cumulative_value_across_missing_minutes()
    {
        var bars = ImmutableArray.Create(Cf.Bar(0, 100, 500), Cf.Bar(3, 100, 700));

        var profile = SessionVolumeProfile.FromBars(new DateOnly(2026, 9, 11), Cf.SessionStart, bars);

        Assert.Equal(new[] { 500m, 500m, 500m, 1200m }, profile.Cumulative.ToArray());
        Assert.Equal(500m, profile.At(2));
        Assert.Equal(1200m, profile.At(9));
        Assert.Null(profile.At(0));
    }

    [Fact]
    public void RvolDaily_is_positive_when_today_outpaces_the_twenty_session_average_and_price_is_up()
    {
        var signal = ConfluenceTechniques.RelativeVolumeDaily(
            Cf.Input(Cf.Ramp(30, 100, .05), previousVolumes: Profiles(20, 60, 1000)), Policy);

        Assert.False(signal.Warmup);
        Assert.Equal(30, Cf.Evidence(signal, "elapsedMinutes"));
        Assert.Equal(1, Cf.Evidence(signal, "relativeVolumeDaily"));
        Assert.Equal(0, signal.Score, 4);
        Assert.Equal(1, signal.Confidence);

        var busy = ConfluenceTechniques.RelativeVolumeDaily(
            Cf.Input(Cf.Ramp(30, 100, .05), previousVolumes: Profiles(20, 60, 500)), Policy);

        Assert.Equal(2, Cf.Evidence(busy, "relativeVolumeDaily"));
        Assert.Equal(Math.Tanh(1), busy.Score, 4);
    }

    [Fact]
    public void RvolDaily_flips_sign_with_the_session_direction()
    {
        var signal = ConfluenceTechniques.RelativeVolumeDaily(
            Cf.Input(Cf.Ramp(30, 100, -.05), previousVolumes: Profiles(20, 60, 500)), Policy);

        Assert.Equal(-Math.Tanh(1), signal.Score, 4);
        Assert.Equal(-1, Cf.Evidence(signal, "direction"));
    }

    [Fact]
    public void RvolDaily_is_warmup_without_twenty_prior_sessions()
    {
        var short_ = ConfluenceTechniques.RelativeVolumeDaily(
            Cf.Input(Cf.Ramp(30, 100, .05), previousVolumes: Profiles(19, 60, 500)), Policy);

        Assert.True(short_.Warmup);
        Assert.Equal(19, Cf.Evidence(short_, "sessions"));
        Assert.True(ConfluenceTechniques.RelativeVolumeDaily(Cf.Input(Cf.Ramp(30, 100, .05)), Policy).Warmup);
        Assert.True(ConfluenceTechniques.RelativeVolumeDaily(Cf.Input([]), Policy).Warmup);
    }
}
