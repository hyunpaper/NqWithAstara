using Astra.Server.Application.Rates;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RatesSnapshotBuilderTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
    static readonly RatesOptions Options = new() { Enabled = true };

    static IntradayTenorState Live(TreasuryTenor tenor, double value, double? previousClose, DateTimeOffset? fetchedAt = null)
        => new(true, new IntradayRateQuote(tenor, tenor.Key(), value, previousClose, Now.AddSeconds(-30), "yahoo"), fetchedAt ?? Now.AddSeconds(-20), null, 0, null);

    static DailyRateSeries Daily(TreasuryTenor tenor, double previous, double latest, DateOnly? latestDate = null)
    {
        var date = latestDate ?? new DateOnly(2026, 10, 1);
        return new DailyRateSeries(tenor, tenor.FredSeries(), [new(date.AddDays(-1), previous), new(date, latest)], Now.AddHours(-2), "fred");
    }

    static RatesSnapshotDto Build(Dictionary<TreasuryTenor, IntradayTenorState> intraday, Dictionary<TreasuryTenor, DailyRateSeries> daily, RatesOptions? options = null)
        => RatesSnapshotBuilder.Build(options ?? Options, Now, "ok", intraday, daily);

    [Fact]
    public void 실시간_만기는_bp_변화와_신선도를_계산하고_2Y는_FRED_전일값만_표시한다()
    {
        var snapshot = Build(new()
        {
            [TreasuryTenor.Y2] = new(false, null, null, null, 0, null),
            [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293),
            [TreasuryTenor.Y30] = Live(TreasuryTenor.Y30, 5.603, 5.638),
        }, new()
        {
            [TreasuryTenor.Y2] = Daily(TreasuryTenor.Y2, 4.89, 4.88),
            [TreasuryTenor.Y10] = Daily(TreasuryTenor.Y10, 5.26, 5.29),
        });

        var two = snapshot.Tenors.Single(x => x.Tenor == "2Y");
        var ten = snapshot.Tenors.Single(x => x.Tenor == "10Y");
        var thirty = snapshot.Tenors.Single(x => x.Tenor == "30Y");
        Assert.Equal(RateModes.DailyOnly, two.Mode);
        Assert.Equal(4.88, two.Value);
        Assert.Equal(-1.0, two.ChangeBp);
        Assert.Equal("fresh", two.DelayStatus);
        Assert.Equal(RateModes.Intraday, ten.Mode);
        Assert.Equal("open", ten.SessionStatus);
        Assert.Equal("closed", two.SessionStatus);
        Assert.Equal(-5.6, ten.ChangeBp);
        Assert.Equal(30, ten.DelaySeconds);
        Assert.Equal(5.29, ten.Daily!.Value);
        Assert.Equal(3.0, ten.Daily.ChangeBp);
        Assert.Equal(-3.5, thirty.ChangeBp);
        Assert.Null(thirty.Daily);
        Assert.Equal(["2Y", "10Y", "30Y"], snapshot.Tenors.Select(x => x.Tenor));
    }

    [Fact]
    public void 커브는_혼합_모드와_실시간_모드를_구분한다()
    {
        var snapshot = Build(new()
        {
            [TreasuryTenor.Y2] = new(false, null, null, null, 0, null),
            [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293),
            [TreasuryTenor.Y30] = Live(TreasuryTenor.Y30, 5.603, 5.638),
        }, new() { [TreasuryTenor.Y2] = Daily(TreasuryTenor.Y2, 4.89, 4.88) });

        var twoTen = snapshot.Spreads.Single(x => x.Key == "2s10s");
        var tenThirty = snapshot.Spreads.Single(x => x.Key == "10s30s");
        Assert.Equal(35.7, twoTen.ValueBp);
        Assert.Equal(RateModes.Mixed, twoTen.Mode);
        Assert.Null(twoTen.ChangeBp);
        Assert.NotNull(twoTen.Reason);
        Assert.Equal(36.6, tenThirty.ValueBp);
        Assert.Equal(RateModes.Intraday, tenThirty.Mode);
        Assert.Equal(2.1, tenThirty.ChangeBp);
    }

    [Fact]
    public void 값이_없는_만기는_unavailable이고_커브도_계산하지_않는다()
    {
        var snapshot = Build(new()
        {
            [TreasuryTenor.Y2] = new(false, null, null, null, 0, null),
            [TreasuryTenor.Y10] = new(true, null, null, "HTTP 429", 3, Now.AddMinutes(5)),
            [TreasuryTenor.Y30] = new(true, null, null, null, 0, null),
        }, new());

        Assert.All(snapshot.Tenors, x => Assert.Equal(RateModes.Unavailable, x.Mode));
        Assert.All(snapshot.Tenors, x => Assert.Equal("unavailable", x.DelayStatus));
        Assert.Contains("HTTP 429", snapshot.Tenors.Single(x => x.Tenor == "10Y").Reason);
        Assert.All(snapshot.Spreads, x => Assert.Equal(RateModes.Unavailable, x.Mode));
        Assert.All(snapshot.DirectionChecks, x => Assert.Equal("unknown", x.Agreement));
    }

    [Fact]
    public void 오래된_실시간_값은_stale로_표시하고_경고를_낸다()
    {
        var snapshot = Build(new()
        {
            [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293, fetchedAt: Now.AddMinutes(-20)),
        }, new());

        var ten = snapshot.Tenors.Single(x => x.Tenor == "10Y");
        Assert.Equal("stale", ten.DelayStatus);
        Assert.Contains(snapshot.Warnings, x => x.Contains("10Y 실시간 값이 20분째"));
    }

    [Fact]
    public void 데이터_시각이_직전_거래일이면_세션은_closed다()
    {
        var closed = new IntradayTenorState(true, new IntradayRateQuote(TreasuryTenor.Y10, "^TNX", 5.237, 5.293, new DateTimeOffset(2026, 10, 1, 18, 59, 54, TimeSpan.Zero), "yahoo"), Now.AddSeconds(-20), null, 0, null);

        var snapshot = Build(new() { [TreasuryTenor.Y10] = closed }, new());

        var ten = snapshot.Tenors.Single(x => x.Tenor == "10Y");
        Assert.Equal("closed", ten.SessionStatus);
        Assert.Equal("fresh", ten.DelayStatus);
        Assert.Equal(-5.6, ten.ChangeBp);
    }

    [Fact]
    public void 방향_검사는_실시간_변화와_FRED_전일값_기준_변화가_같으면_agree다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) },
            new() { [TreasuryTenor.Y10] = Daily(TreasuryTenor.Y10, 5.26, 5.29) });

        var check = Assert.Single(snapshot.DirectionChecks);
        Assert.Equal("10Y", check.Tenor);
        Assert.Equal(RateDirections.Down, check.IntradayDirection);
        Assert.Equal(RateDirections.Down, check.DailyBaselineDirection);
        Assert.Equal(-5.6, check.ChangeBp);
        Assert.Equal(-5.3, check.ChangeVsDailyBp);
        Assert.Equal(0.3, check.BaselineGapBp);
        Assert.Equal("agree", check.Agreement);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void 방향_검사는_어긋나면_diverge와_경고를_내고_기준값_괴리도_경고한다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.30, 5.35) },
            new() { [TreasuryTenor.Y10] = Daily(TreasuryTenor.Y10, 5.20, 5.26) });

        var check = Assert.Single(snapshot.DirectionChecks);
        Assert.Equal(RateDirections.Down, check.IntradayDirection);
        Assert.Equal(RateDirections.Up, check.DailyBaselineDirection);
        Assert.Equal("diverge", check.Agreement);
        Assert.Equal(9.0, check.BaselineGapBp);
        Assert.Contains(snapshot.Warnings, x => x.Contains("어긋납니다"));
        Assert.Contains(snapshot.Warnings, x => x.Contains("+9.0bp"));
    }

    [Fact]
    public void FRED_최신값이_직전_거래일이_아니면_방향_검사는_unknown이다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) },
            new() { [TreasuryTenor.Y10] = Daily(TreasuryTenor.Y10, 5.26, 5.29, new DateOnly(2026, 9, 20)) });

        var check = Assert.Single(snapshot.DirectionChecks);
        Assert.Equal(RateDirections.Down, check.IntradayDirection);
        Assert.Equal(RateDirections.Unknown, check.DailyBaselineDirection);
        Assert.Equal("unknown", check.Agreement);
        Assert.Contains("2026-09-20", check.Reason);
    }

    [Fact]
    public void 임계값_미만의_변화는_보합이라_diverge로_보지_않는다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.30, 5.31) },
            new() { [TreasuryTenor.Y10] = Daily(TreasuryTenor.Y10, 5.20, 5.29) });

        var check = Assert.Single(snapshot.DirectionChecks);
        Assert.Equal(RateDirections.Flat, check.IntradayDirection);
        Assert.Equal("agree", check.Agreement);
    }

    [Fact]
    public void 스냅샷은_출처_설명과_제한사항을_담고_비활성이면_status가_disabled다()
    {
        var state = new RatesRuntimeState(new RatesOptions { Enabled = false });

        var snapshot = state.Snapshot(Now);

        Assert.False(snapshot.Enabled);
        Assert.Equal("disabled", snapshot.Status);
        Assert.Contains("Yahoo", snapshot.IntradaySource);
        Assert.Contains("FRED", snapshot.DailySource);
        Assert.Contains(snapshot.Limitations, x => x.Contains("2Y"));
        Assert.Contains(snapshot.Limitations, x => x.Contains("ETF 대리변수"));
        Assert.Equal(60, snapshot.RefreshSeconds);
    }
}
