using Astra.Server.Application.Rates;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RatesEtfProxyTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
    static readonly RatesOptions Options = new() { Enabled = true };
    static readonly EtfProxyDefinition Ief = RatesOptions.DefaultEtfProxies.Single(x => x.Symbol == "IEF");
    static readonly EtfProxyDefinition Shy = RatesOptions.DefaultEtfProxies.Single(x => x.Symbol == "SHY");

    static IntradayTenorState Live(TreasuryTenor tenor, double value, double? previousClose, DateTimeOffset? asOf = null)
        => new(true, new IntradayRateQuote(tenor, tenor.Key(), value, previousClose, asOf ?? Now.AddSeconds(-30), "yahoo"), Now.AddSeconds(-20), null, 0, null);

    static EtfProxyState Etf(EtfProxyDefinition proxy, double price, double? previousClose, DateTimeOffset? asOf = null)
        => new(proxy, new EtfProxyQuote(proxy.Tenor, proxy.Symbol, price, previousClose, asOf ?? Now.AddSeconds(-30), "yahoo:" + proxy.Symbol), Now.AddSeconds(-20), null, 0, null);

    static RatesSnapshotDto Build(Dictionary<TreasuryTenor, IntradayTenorState> intraday, EtfProxyState[] etf, Dictionary<string, int>? runs = null, RatesOptions? options = null)
        => RatesSnapshotBuilder.Build(options ?? Options, Now, "ok", intraday, new Dictionary<TreasuryTenor, DailyRateSeries>(), etf, runs ?? new Dictionary<string, int>(StringComparer.Ordinal));

    [Fact]
    public void ETF_상승과_금리_하락은_방향이_일치한다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) }, [Etf(Ief, 89.3, 89.0031)]);

        var proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Equal("10Y", proxy.Tenor);
        Assert.Equal("IEF", proxy.Symbol);
        Assert.Equal(7.5, proxy.Duration);
        Assert.Equal(0.334, proxy.ReturnPct);
        Assert.Equal(-4.5, proxy.ImpliedChangeBp);
        Assert.Equal(-5.6, proxy.RateChangeBp);
        Assert.Equal(RateDirections.Down, proxy.EtfDirection);
        Assert.Equal(RateDirections.Down, proxy.RateDirection);
        Assert.Equal(EtfProxyDivergence.Agree, proxy.Agreement);
        Assert.Null(proxy.Reason);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void ETF_하락과_금리_하락은_diverge이고_경고를_낸다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) }, [Etf(Ief, 88.5, 89.0031)]);

        var proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Equal(-0.565, proxy.ReturnPct);
        Assert.Equal(7.5, proxy.ImpliedChangeBp);
        Assert.Equal(RateDirections.Up, proxy.EtfDirection);
        Assert.Equal(RateDirections.Down, proxy.RateDirection);
        Assert.Equal(EtfProxyDivergence.Diverge, proxy.Agreement);
        var warning = Assert.Single(snapshot.Warnings);
        Assert.Equal("10Y ETF IEF 당일 수익률(-0.57% ≈ 금리 +7.5bp)이 실시간 금리 변화(-5.6bp)와 방향이 어긋납니다.", warning);
    }

    [Fact]
    public void 괴리가_임계_횟수_이상_이어지면_지속_경고를_추가한다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) }, [Etf(Ief, 88.5, 89.0031)],
            new(StringComparer.Ordinal) { ["IEF"] = 3 });

        Assert.Equal(3, Assert.Single(snapshot.EtfProxies).DivergeRuns);
        Assert.Equal(2, snapshot.Warnings.Count);
        Assert.Contains("10Y ETF IEF 괴리가 3회 연속 이어집니다.", snapshot.Warnings);
    }

    [Fact]
    public void 환산_bp가_임계값_미만이면_보합이라_diverge로_보지_않는다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) }, [Etf(Ief, 88.95, 89.0031)]);

        var proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Equal(RateDirections.Flat, proxy.EtfDirection);
        Assert.Equal(EtfProxyDivergence.Agree, proxy.Agreement);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void 실시간_금리가_없는_만기는_수익률만_계산하고_비교는_unknown이다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y2] = new(false, null, null, null, 0, null) }, [Etf(Shy, 81.1, 80.959)]);

        var proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Equal("2Y", proxy.Tenor);
        Assert.Equal(0.174, proxy.ReturnPct);
        Assert.Equal(-9.2, proxy.ImpliedChangeBp);
        Assert.Null(proxy.RateChangeBp);
        Assert.Equal(RateDirections.Down, proxy.EtfDirection);
        Assert.Equal(RateDirections.Unknown, proxy.RateDirection);
        Assert.Equal(EtfProxyDivergence.Unknown, proxy.Agreement);
        Assert.Contains("실시간 금리가 없어", proxy.Reason);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void ETF_가격이_없거나_전일종가가_없으면_unknown이다()
    {
        var missing = new EtfProxyState(Ief, null, null, "HTTP 429", 2, Now.AddMinutes(5));
        var noPrevious = Etf(Ief, 89.3, null);

        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) }, [missing]);
        var proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Null(proxy.Price);
        Assert.Null(proxy.ReturnPct);
        Assert.Equal(EtfProxyDivergence.Unknown, proxy.Agreement);
        Assert.Contains("HTTP 429", proxy.Reason);

        snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293) }, [noPrevious]);
        proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Equal(89.3, proxy.Price);
        Assert.Null(proxy.ReturnPct);
        Assert.Equal(EtfProxyDivergence.Unknown, proxy.Agreement);
        Assert.Contains("전일 종가", proxy.Reason);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void ETF와_금리의_거래일이_다르면_비교하지_않는다()
    {
        var snapshot = Build(new() { [TreasuryTenor.Y10] = Live(TreasuryTenor.Y10, 5.237, 5.293, new DateTimeOffset(2026, 10, 1, 19, 59, 0, TimeSpan.Zero)) }, [Etf(Ief, 88.5, 89.0031)]);

        var proxy = Assert.Single(snapshot.EtfProxies);
        Assert.Equal(7.5, proxy.ImpliedChangeBp);
        Assert.Equal(-5.6, proxy.RateChangeBp);
        Assert.Equal(EtfProxyDivergence.Unknown, proxy.Agreement);
        Assert.Equal("ETF 데이터 거래일(2026-10-02)과 금리 데이터 거래일(2026-10-01)이 다릅니다.", proxy.Reason);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void 대리변수는_만기_순서로_정렬되고_제한사항에_심볼을_적는다()
    {
        var uten = new EtfProxyDefinition(TreasuryTenor.Y10, "UTEN", 8.5);
        var tlt = RatesOptions.DefaultEtfProxies.Single(x => x.Symbol == "TLT");
        var snapshot = Build(new(), [Etf(tlt, 77.71, 77.4684), Etf(uten, 40.0, 39.9), Etf(Ief, 89.3, 89.0031), Etf(Shy, 81.1, 80.959)]);

        Assert.Equal(["SHY", "IEF", "UTEN", "TLT"], snapshot.EtfProxies.Select(x => x.Symbol));
        Assert.Contains(snapshot.Limitations, x => x.Contains("ETF 대리변수(TLT·UTEN·IEF·SHY)"));
    }

    [Fact]
    public void 설정된_대리변수는_만기_파싱_기본_듀레이션_중복_제거를_거친다()
    {
        var options = new RatesOptions
        {
            EtfProxies =
            [
                new() { Tenor = "10y", Symbol = " uten ", Duration = 8.5 },
                new() { Tenor = "30Y", Symbol = "UTHY" },
                new() { Tenor = "30Y", Symbol = "uthy", Duration = 20 },
                new() { Tenor = "5Y", Symbol = "IEI", Duration = 4.5 },
                new() { Tenor = "2Y", Symbol = " " },
            ],
        };

        var resolved = options.ResolvedEtfProxies();

        Assert.Equal([new EtfProxyDefinition(TreasuryTenor.Y10, "UTEN", 8.5), new EtfProxyDefinition(TreasuryTenor.Y30, "UTHY", 16.5)], resolved);
        Assert.Same(RatesOptions.DefaultEtfProxies, new RatesOptions().ResolvedEtfProxies());
        Assert.Empty(new RatesOptions { EtfEnabled = false }.ResolvedEtfProxies());
        Assert.Equal(TimeSpan.FromMinutes(1), new RatesOptions { EtfIntervalSeconds = 5 }.EtfInterval);
        Assert.Equal(1, new RatesOptions { EtfDivergenceWarnRuns = 0 }.EtfDivergenceRuns);
    }

    [Fact]
    public void 런타임_상태는_설정에서_빠진_대리변수를_정리한다()
    {
        var state = new RatesRuntimeState(Options);
        state.MarkSupport(TreasuryTenor.Y10, true);
        state.IntradaySucceeded(TreasuryTenor.Y10, new IntradayRateQuote(TreasuryTenor.Y10, "^TNX", 5.237, 5.293, Now.AddSeconds(-30), "yahoo:^TNX"), Now);
        state.RegisterEtf(RatesOptions.DefaultEtfProxies);
        state.EtfSucceeded(Ief, new EtfProxyQuote(TreasuryTenor.Y10, "IEF", 88.5, 89.0031, Now.AddSeconds(-30), "yahoo:IEF"), Now);
        state.EtfFailed(Shy, "HTTP 429", Now);
        Assert.Equal(1, state.EtfDivergeRuns("IEF"));
        Assert.Contains("etf:SHY: HTTP 429", state.Health().FailedSources);
        Assert.Equal("partial", state.Snapshot(Now).Status);
        Assert.Equal(Now.AddMinutes(5), state.EtfRetryAt("SHY"));

        state.RegisterEtf([Ief]);

        Assert.Equal(["IEF"], state.Snapshot(Now).EtfProxies.Select(x => x.Symbol));
        Assert.Empty(state.Health().FailedSources);
        Assert.Null(state.EtfRetryAt("SHY"));
    }
}
