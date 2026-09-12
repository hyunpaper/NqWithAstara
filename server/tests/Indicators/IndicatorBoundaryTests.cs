using Astra.Server.Domain.Indicators;
using Xunit;

namespace Astra.Server.Tests.ConfluenceIndicators;

public sealed class IndicatorBoundaryTests
{
    static readonly DateTimeOffset SessionOpen = new(2026, 9, 11, 9, 30, 0, TimeSpan.FromHours(-4));

    static IndicatorBar Bar(int index, decimal open, decimal high, decimal low, decimal close, decimal volume = 1000m)
        => new(SessionOpen.AddMinutes(index), SessionOpen.AddMinutes(index + 1), open, high, low, close, volume);

    static IReadOnlyList<IndicatorBar> Ramp(int count, decimal step = 0.25m)
    {
        var bars = new List<IndicatorBar>(count);
        for (var i = 0; i < count; i++)
        {
            var close = 100m + step * i;
            bars.Add(Bar(i, close - step / 2, close + step, close - step, close));
        }
        return bars;
    }

    [Fact]
    public void Empty_input_yields_empty_series_for_every_indicator()
    {
        var empty = Array.Empty<IndicatorBar>();

        Assert.Empty(Ema.Series(empty, 20));
        Assert.Empty(Macd.Series(empty));
        Assert.Empty(Rsi.Series(empty));
        Assert.Empty(AverageTrueRange.Series(empty, null));
        Assert.Empty(BollingerBands.Series(empty));
        Assert.Empty(DirectionalMovement.Series(empty));
        Assert.Empty(StochasticSlow.Series(empty));
        Assert.Empty(Donchian.Series(empty));
        Assert.Empty(SessionVwap.Series(empty));
        Assert.Empty(SessionTimeframe.Aggregate(empty, SessionOpen, SessionTimeframe.FiveMinutes));
        Assert.Empty(TickRule.Series(Array.Empty<TickTrade>()));
    }

    [Fact]
    public void Ema_first_value_appears_exactly_at_required_bars_and_equals_seed_sma()
    {
        var bars = Ramp(Ema.RequiredBars(5));
        var series = Ema.Series(bars, 5);

        Assert.All(series.Take(4), x => Assert.True(x.Warmup));
        Assert.False(series[4].Warmup);
        Assert.Equal(100.5, series[4].Value);
    }

    [Fact]
    public void Ema_below_required_bars_is_entirely_warmup()
    {
        Assert.All(Ema.Series(Ramp(4), 5), x => Assert.True(x.Warmup && x.Value is null));
    }

    [Fact]
    public void Macd_signal_warmup_ends_at_required_bars()
    {
        var bars = Ramp(Macd.RequiredBars());
        var series = Macd.Series(bars);

        Assert.True(series[^2].Warmup);
        Assert.False(series[^1].Warmup);
        Assert.NotNull(series[^1].Histogram);
        Assert.Equal(34, Macd.RequiredBars());
    }

    [Fact]
    public void Rsi_requires_one_more_bar_than_period()
    {
        var series = Rsi.Series(Ramp(Rsi.RequiredBars()));

        Assert.Equal(15, Rsi.RequiredBars());
        Assert.True(series[^2].Warmup);
        Assert.False(series[^1].Warmup);
        Assert.Equal(100.0, series[^1].Value);
    }

    [Fact]
    public void Atr_first_true_range_uses_previous_session_close()
    {
        var bars = new[] { Bar(0, 10m, 10.5m, 9.9m, 10.2m) };

        var withoutPrevious = AverageTrueRange.Series(bars, null, 1);
        var withPrevious = AverageTrueRange.Series(bars, 8m, 1);

        Assert.Equal(0.6, withoutPrevious[0].Value);
        Assert.Equal(2.5, withPrevious[0].Value);
    }

    [Fact]
    public void Atr_session_reset_seeds_only_from_current_session_bars()
    {
        var bars = Ramp(AverageTrueRange.RequiredBars());

        var afterGapDown = AverageTrueRange.Series(bars, 90m);
        var withoutPrevious = AverageTrueRange.Series(bars, null);

        Assert.Equal(14, AverageTrueRange.RequiredBars());
        Assert.True(afterGapDown[12].Warmup);
        Assert.False(afterGapDown[13].Warmup);
        Assert.Equal(0.5, withoutPrevious[13].Value);
        Assert.Equal(1.1964, afterGapDown[13].Value);
    }

    [Fact]
    public void BollingerBands_with_flat_closes_collapse_and_percent_b_is_null()
    {
        var bars = Enumerable.Range(0, 20).Select(i => Bar(i, 50m, 50m, 50m, 50m)).ToList();
        var series = BollingerBands.Series(bars);

        Assert.Equal(50.0, series[^1].Upper);
        Assert.Equal(50.0, series[^1].Lower);
        Assert.Null(series[^1].PercentB);
        Assert.Equal(0.0, series[^1].BandWidth);
    }

    [Fact]
    public void Adx_and_di_have_different_warmup_lengths()
    {
        var bars = Ramp(DirectionalMovement.RequiredBarsForAdx());
        var series = DirectionalMovement.Series(bars);

        Assert.Equal(15, DirectionalMovement.RequiredBarsForDi());
        Assert.Equal(28, DirectionalMovement.RequiredBarsForAdx());
        Assert.True(series[13].DiWarmup);
        Assert.False(series[14].DiWarmup);
        Assert.True(series[26].AdxWarmup);
        Assert.False(series[27].AdxWarmup);
    }

    [Fact]
    public void Stochastic_with_equal_high_and_low_yields_zero_instead_of_dividing_by_zero()
    {
        var bars = Enumerable.Range(0, StochasticSlow.RequiredBars()).Select(i => Bar(i, 7m, 7m, 7m, 7m)).ToList();
        var series = StochasticSlow.Series(bars);

        Assert.Equal(18, StochasticSlow.RequiredBars());
        Assert.True(series[16].Warmup);
        Assert.Equal(0.0, series[17].SlowK);
        Assert.Equal(0.0, series[17].SlowD);
    }

    [Fact]
    public void Donchian_middle_is_the_midpoint_of_the_channel()
    {
        var series = Donchian.Series(Ramp(20));

        Assert.True(series[18].Warmup);
        Assert.Equal(series[19].Middle, (series[19].Upper + series[19].Lower) / 2);
    }

    [Fact]
    public void Vwap_with_zero_volume_stays_in_warmup_until_volume_arrives()
    {
        var bars = new[] { Bar(0, 10m, 11m, 9m, 10m, 0m), Bar(1, 10m, 11m, 9m, 10m, 500m) };
        var series = SessionVwap.Series(bars);

        Assert.True(series[0].Warmup);
        Assert.False(series[1].Warmup);
        Assert.Equal(10.0, series[1].Vwap);
        Assert.Equal(0.0, series[1].StdDev);
    }

    [Fact]
    public void Vwap_uses_hlc3_typical_price()
    {
        var bars = new[] { Bar(0, 10m, 12m, 9m, 10.5m, 100m) };

        Assert.Equal(10.5, SessionVwap.Series(bars)[0].Vwap);
    }

    [Fact]
    public void Aggregation_drops_buckets_with_missing_minutes()
    {
        var bars = Ramp(10).Where((_, i) => i != 7).ToList();
        var aggregated = SessionTimeframe.Aggregate(bars, SessionOpen, SessionTimeframe.FiveMinutes);

        Assert.Single(aggregated);
        Assert.Equal(SessionOpen, aggregated[0].Start);
        Assert.Equal(SessionOpen.AddMinutes(5), aggregated[0].End);
    }

    [Fact]
    public void Aggregation_ignores_bars_before_the_session_open()
    {
        var before = new IndicatorBar(SessionOpen.AddMinutes(-1), SessionOpen, 1m, 1m, 1m, 1m, 1m);
        var bars = new[] { before }.Concat(Ramp(5)).ToList();

        var aggregated = SessionTimeframe.Aggregate(bars, SessionOpen, SessionTimeframe.FiveMinutes);

        Assert.Single(aggregated);
        Assert.Equal(SessionOpen, aggregated[0].Start);
    }

    [Fact]
    public void Aggregation_boundaries_are_anchored_to_the_session_open()
    {
        var aggregated = SessionTimeframe.Aggregate(Ramp(30), SessionOpen, SessionTimeframe.FifteenMinutes);

        Assert.Equal(2, aggregated.Length);
        Assert.Equal(SessionOpen, aggregated[0].Start);
        Assert.Equal(SessionOpen.AddMinutes(15), aggregated[1].Start);
        Assert.Equal(30000m, aggregated[0].Volume + aggregated[1].Volume);
    }

    [Fact]
    public void TickRule_first_trade_of_session_is_undetermined()
    {
        Assert.Equal(TickDirection.Undetermined, TickRule.Classify(null, 10m));
        Assert.Null(TickRule.Sign(TickDirection.Undetermined));
    }

    [Fact]
    public void TickRule_threshold_is_strictly_below_half_a_cent()
    {
        Assert.Equal(TickDirection.Flat, TickRule.Classify(10m, 10.0049m));
        Assert.Equal(TickDirection.Up, TickRule.Classify(10m, 10.005m));
        Assert.Equal(TickDirection.Flat, TickRule.Classify(10m, 9.9951m));
        Assert.Equal(TickDirection.Down, TickRule.Classify(10m, 9.995m));
        Assert.Equal(0, TickRule.Sign(TickDirection.Flat));
    }

    [Fact]
    public void Rounding_constants_are_four_digits_for_price_and_ratio()
    {
        Assert.Equal(4, IndicatorRounding.PriceDigits);
        Assert.Equal(4, IndicatorRounding.RatioDigits);
        Assert.Equal(1.2346, IndicatorRounding.Price(1.23455));
        Assert.Equal(0.6667, IndicatorRounding.Ratio(2.0 / 3.0));
    }

    [Fact]
    public void Non_positive_periods_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ema.Series(Ramp(5), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rsi.Series(Ramp(5), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BollingerBands.Series(Ramp(5), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectionalMovement.Series(Ramp(5), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionTimeframe.Aggregate(Ramp(5), SessionOpen, TimeSpan.Zero));
    }
}
