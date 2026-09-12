using Astra.Server.Domain.Indicators;
using Xunit;

namespace Astra.Server.Tests.ConfluenceIndicators;

public sealed class IndicatorReferenceVectorTests
{
    [Theory]
    [InlineData("ema-normal.json")]
    [InlineData("ema-boundary.json")]
    [InlineData("ema-subpenny.json")]
    public void Ema_matches_talib_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = Ema.Series(IndicatorVectors.Bars(root), root.GetProperty("period").GetInt32());

        IndicatorVectors.AssertMatches("ema", series.Select(x => x.Value).ToList(),
            IndicatorVectors.Expected(root, "expected"));
    }

    [Theory]
    [InlineData("macd-normal.json")]
    [InlineData("macd-boundary.json")]
    public void Macd_matches_talib_composed_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = Macd.Series(IndicatorVectors.Bars(root),
            root.GetProperty("fastPeriod").GetInt32(),
            root.GetProperty("slowPeriod").GetInt32(),
            root.GetProperty("signalPeriod").GetInt32());

        IndicatorVectors.AssertMatches("macd", series.Select(x => x.Macd).ToList(),
            IndicatorVectors.Expected(root, "expectedMacd"));
        IndicatorVectors.AssertMatches("signal", series.Select(x => x.Signal).ToList(),
            IndicatorVectors.Expected(root, "expectedSignal"));
        IndicatorVectors.AssertMatches("histogram", series.Select(x => x.Histogram).ToList(),
            IndicatorVectors.Expected(root, "expectedHistogram"));
    }

    [Theory]
    [InlineData("rsi-normal.json")]
    [InlineData("rsi-boundary.json")]
    public void Rsi_matches_talib_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = Rsi.Series(IndicatorVectors.Bars(root), root.GetProperty("period").GetInt32());

        IndicatorVectors.AssertMatches("rsi", series.Select(x => x.Value).ToList(),
            IndicatorVectors.Expected(root, "expected"));
    }

    [Theory]
    [InlineData("atr-normal.json")]
    [InlineData("atr-boundary.json")]
    public void Atr_matches_talib_vector_with_previous_session_close(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = AverageTrueRange.Series(IndicatorVectors.Bars(root),
            root.GetProperty("previousSessionClose").GetDecimal(),
            root.GetProperty("period").GetInt32());

        IndicatorVectors.AssertMatches("atr", series.Select(x => x.Value).ToList(),
            IndicatorVectors.Expected(root, "expected"));
    }

    [Theory]
    [InlineData("bbands-normal.json")]
    [InlineData("bbands-boundary.json")]
    [InlineData("bbands-subpenny.json")]
    public void BollingerBands_matches_talib_population_sigma_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = BollingerBands.Series(IndicatorVectors.Bars(root),
            root.GetProperty("period").GetInt32(), root.GetProperty("deviations").GetDouble());

        IndicatorVectors.AssertMatches("upper", series.Select(x => x.Upper).ToList(),
            IndicatorVectors.Expected(root, "expectedUpper"));
        IndicatorVectors.AssertMatches("middle", series.Select(x => x.Middle).ToList(),
            IndicatorVectors.Expected(root, "expectedMiddle"));
        IndicatorVectors.AssertMatches("lower", series.Select(x => x.Lower).ToList(),
            IndicatorVectors.Expected(root, "expectedLower"));
    }

    [Theory]
    [InlineData("adx-normal.json")]
    [InlineData("adx-boundary.json")]
    public void AdxAndDmi_match_talib_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = DirectionalMovement.Series(IndicatorVectors.Bars(root), root.GetProperty("period").GetInt32());

        IndicatorVectors.AssertMatches("plusDi", series.Select(x => x.PlusDi).ToList(),
            IndicatorVectors.Expected(root, "expectedPlusDi"));
        IndicatorVectors.AssertMatches("minusDi", series.Select(x => x.MinusDi).ToList(),
            IndicatorVectors.Expected(root, "expectedMinusDi"));
        IndicatorVectors.AssertMatches("adx", series.Select(x => x.Adx).ToList(),
            IndicatorVectors.Expected(root, "expectedAdx"));
    }

    [Theory]
    [InlineData("stoch-normal.json")]
    [InlineData("stoch-boundary.json")]
    public void StochasticSlow_matches_talib_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = StochasticSlow.Series(IndicatorVectors.Bars(root),
            root.GetProperty("fastKPeriod").GetInt32(),
            root.GetProperty("slowKPeriod").GetInt32(),
            root.GetProperty("slowDPeriod").GetInt32());

        IndicatorVectors.AssertMatches("slowK", series.Select(x => x.SlowK).ToList(),
            IndicatorVectors.Expected(root, "expectedSlowK"));
        IndicatorVectors.AssertMatches("slowD", series.Select(x => x.SlowD).ToList(),
            IndicatorVectors.Expected(root, "expectedSlowD"));
    }

    [Theory]
    [InlineData("donchian-normal.json")]
    [InlineData("donchian-boundary.json")]
    public void Donchian_matches_talib_max_min_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = Donchian.Series(IndicatorVectors.Bars(root), root.GetProperty("period").GetInt32());
        var upper = IndicatorVectors.Expected(root, "expectedUpper");
        var lower = IndicatorVectors.Expected(root, "expectedLower");
        var middle = upper.Zip(lower, (u, l) => u is { } a && l is { } b ? (a + b) / 2 : (double?)null).ToArray();

        IndicatorVectors.AssertMatches("upper", series.Select(x => x.Upper).ToList(), upper);
        IndicatorVectors.AssertMatches("lower", series.Select(x => x.Lower).ToList(), lower);
        IndicatorVectors.AssertMatches("middle", series.Select(x => x.Middle).ToList(), middle);
    }

    [Theory]
    [InlineData("vwap-normal.json")]
    [InlineData("vwap-boundary.json")]
    public void Vwap_matches_manual_volume_weighted_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var series = SessionVwap.Series(IndicatorVectors.Bars(root));

        IndicatorVectors.AssertMatches("vwap", series.Select(x => x.Vwap).ToList(),
            IndicatorVectors.Expected(root, "expectedVwap"));
        IndicatorVectors.AssertMatches("stdDev", series.Select(x => x.StdDev).ToList(),
            IndicatorVectors.Expected(root, "expectedStdDev"));
    }

    [Theory]
    [InlineData("aggregate-normal.json")]
    [InlineData("aggregate-boundary.json")]
    public void SessionTimeframe_matches_manual_bucket_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var bars = IndicatorVectors.Bars(root);
        var sessionOpen = IndicatorVectors.Time(root.GetProperty("sessionOpen").GetString()!);

        IndicatorVectors.AssertBarsEqual("5m",
            SessionTimeframe.Aggregate(bars, sessionOpen, SessionTimeframe.FiveMinutes), root.GetProperty("expected5m"));
        IndicatorVectors.AssertBarsEqual("15m",
            SessionTimeframe.Aggregate(bars, sessionOpen, SessionTimeframe.FifteenMinutes), root.GetProperty("expected15m"));
    }

    [Theory]
    [InlineData("tickrule-normal.json")]
    [InlineData("tickrule-boundary.json")]
    public void TickRule_matches_manual_direction_vector(string vector)
    {
        var root = IndicatorVectors.Load(vector);
        var trades = root.GetProperty("prices").EnumerateArray()
            .Select((x, i) => new TickTrade(DateTimeOffset.UnixEpoch.AddSeconds(i), x.GetDecimal()))
            .ToList();
        var expected = root.GetProperty("expected").EnumerateArray()
            .Select(x => Enum.Parse<TickDirection>(x.GetString()!)).ToArray();

        Assert.Equal(expected, TickRule.Series(trades).ToArray());
    }
}
