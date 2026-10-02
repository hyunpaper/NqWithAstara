using Astra.Server.Application;
using Astra.Server.Application.Rates;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RatesCollectorTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    sealed class Diagnostics : IMonitorDiagnostics
    {
        public List<(string Scope, Exception Error)> Failures { get; } = [];
        public void PollFailed(string scope, Exception exception) => Failures.Add((scope, exception));
        public void MarketDataFailed(string symbol, string operation, Exception exception) => Failures.Add((symbol, exception));
    }

    sealed class FakeIntraday : IIntradayRateSource
    {
        public Dictionary<TreasuryTenor, Func<IntradayRateQuote>> Responses { get; } = new();
        public Dictionary<string, Func<EtfProxyQuote>> EtfResponses { get; } = new(StringComparer.Ordinal);
        public List<TreasuryTenor> Calls { get; } = [];
        public List<string> EtfCalls { get; } = [];
        public bool Supports(TreasuryTenor tenor) => tenor is TreasuryTenor.Y10 or TreasuryTenor.Y30;
        public Task<IntradayRateQuote> FetchAsync(TreasuryTenor tenor, CancellationToken ct)
        {
            Calls.Add(tenor);
            return Task.FromResult(Responses[tenor]());
        }
        public Task<EtfProxyQuote> FetchEtfAsync(EtfProxyDefinition proxy, CancellationToken ct)
        {
            EtfCalls.Add(proxy.Symbol);
            return Task.FromResult(EtfResponses[proxy.Symbol]());
        }
    }

    sealed class FakeDaily : IDailyRateSource
    {
        public Func<TreasuryTenor, DailyRateSeries> Response { get; set; } = tenor => new DailyRateSeries(tenor, tenor.FredSeries(),
            [new(new DateOnly(2026, 9, 30), 5.0), new(new DateOnly(2026, 10, 1), 5.1)], Now, "fred");
        public List<(TreasuryTenor Tenor, DateOnly Since)> Calls { get; } = [];
        public Task<DailyRateSeries> FetchAsync(TreasuryTenor tenor, DateOnly since, CancellationToken ct)
        {
            Calls.Add((tenor, since));
            return Task.FromResult(Response(tenor));
        }
    }

    sealed class MemoryStore : IRateObservationStore
    {
        public List<RateObservation> Rows { get; } = [];
        public List<RateObservation> Seed { get; } = [];
        public int PruneCalls { get; private set; }
        public bool FailAppend { get; set; }
        public Task AppendAsync(IReadOnlyCollection<RateObservation> observations, CancellationToken ct)
        {
            if (FailAppend) throw new IOException("disk full");
            Rows.AddRange(observations);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<RateObservation>> ReadDayAsync(DateOnly day, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RateObservation>>(Seed.Where(x => DateOnly.FromDateTime(x.At.UtcDateTime) == day).ToArray());
        public Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct)
        {
            PruneCalls++;
            return Task.FromResult(2);
        }
    }

    static IntradayRateQuote Quote(TreasuryTenor tenor, double value, DateTimeOffset? asOf = null)
        => new(tenor, tenor.Key(), value, value + 0.05, asOf ?? Now.AddSeconds(-10), "yahoo:" + tenor.Key());

    static EtfProxyQuote Etf(string symbol, double price, double previousClose, DateTimeOffset? asOf = null)
        => new(RatesOptions.DefaultEtfProxies.Single(x => x.Symbol == symbol).Tenor, symbol, price, previousClose, asOf ?? Now.AddSeconds(-10), "yahoo:" + symbol);

    sealed class Fixture
    {
        public RatesOptions Options { get; } = new() { Enabled = true, RequestSpacingMs = 0 };
        public Clock Clock { get; } = new(Now);
        public Diagnostics Diagnostics { get; } = new();
        public FakeIntraday Intraday { get; } = new();
        public FakeDaily Daily { get; } = new();
        public MemoryStore Store { get; } = new();
        public RatesRuntimeState State { get; }
        public RatesCollector Collector { get; }

        public Fixture()
        {
            State = new RatesRuntimeState(Options);
            Collector = new RatesCollector(Options, Intraday, Daily, Store, State, Diagnostics, Clock);
            Intraday.Responses[TreasuryTenor.Y10] = () => Quote(TreasuryTenor.Y10, 5.237);
            Intraday.Responses[TreasuryTenor.Y30] = () => Quote(TreasuryTenor.Y30, 5.603);
            Intraday.EtfResponses["SHY"] = () => Etf("SHY", 81.1, 80.959);
            Intraday.EtfResponses["IEF"] = () => Etf("IEF", 89.3, 89.0031);
            Intraday.EtfResponses["TLT"] = () => Etf("TLT", 77.71, 77.4684);
        }
    }

    [Fact]
    public async Task 첫_실행은_FRED_세_만기와_실시간_두_만기를_받아_기록한다()
    {
        var f = new Fixture();

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Equal([TreasuryTenor.Y2, TreasuryTenor.Y10, TreasuryTenor.Y30], f.Daily.Calls.Select(x => x.Tenor));
        Assert.All(f.Daily.Calls, x => Assert.Equal(new DateOnly(2026, 9, 2), x.Since));
        Assert.Equal([TreasuryTenor.Y10, TreasuryTenor.Y30], f.Intraday.Calls);
        Assert.Equal(["SHY", "IEF", "TLT"], f.Intraday.EtfCalls);
        Assert.Equal(3, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Daily));
        Assert.Equal(2, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Intraday));
        Assert.Equal(3, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Etf));
        Assert.Equal(1, f.Store.PruneCalls);
        var snapshot = f.State.Snapshot(Now);
        Assert.Equal("ok", snapshot.Status);
        Assert.Equal(5.237, snapshot.Tenors.Single(x => x.Tenor == "10Y").Value);
        Assert.Equal(RateModes.DailyOnly, snapshot.Tenors.Single(x => x.Tenor == "2Y").Mode);
        Assert.Equal(["SHY", "IEF", "TLT"], snapshot.EtfProxies.Select(x => x.Symbol));
        Assert.Equal(89.3, snapshot.EtfProxies.Single(x => x.Symbol == "IEF").Price);
        var health = f.State.Health();
        Assert.Equal(8, health.AppendedToday);
        Assert.Equal(2, health.PrunedFiles);
        Assert.Empty(health.FailedSources);
    }

    [Fact]
    public async Task 같은_날_두_번째_실행은_FRED를_다시_부르지_않고_변하지_않은_실시간_값은_기록하지_않는다()
    {
        var f = new Fixture();
        await f.Collector.RunOnceAsync(CancellationToken.None);
        f.Clock.Now = Now.AddMinutes(1);

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, f.Daily.Calls.Count);
        Assert.Equal(4, f.Intraday.Calls.Count);
        Assert.Equal(2, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Intraday));
        Assert.Equal(1, f.Store.PruneCalls);
    }

    [Fact]
    public async Task 실시간_값이_바뀌면_기록하고_뉴욕_날짜가_바뀌면_FRED를_다시_받는다()
    {
        var f = new Fixture();
        await f.Collector.RunOnceAsync(CancellationToken.None);
        f.Intraday.Responses[TreasuryTenor.Y10] = () => Quote(TreasuryTenor.Y10, 5.250, Now.AddHours(15));
        f.Clock.Now = Now.AddHours(15);

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Equal(6, f.Daily.Calls.Count);
        Assert.Equal(3, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Intraday));
        Assert.Equal(3, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Daily));
        Assert.Equal(2, f.Store.PruneCalls);
    }

    [Fact]
    public async Task 한_만기의_실패는_다른_만기와_기록에_영향을_주지_않고_백오프한다()
    {
        var f = new Fixture();
        f.Intraday.Responses[TreasuryTenor.Y10] = () => throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests);

        await f.Collector.RunOnceAsync(CancellationToken.None);

        var snapshot = f.State.Snapshot(Now);
        Assert.Equal("partial", snapshot.Status);
        Assert.Equal(5.603, snapshot.Tenors.Single(x => x.Tenor == "30Y").Value);
        Assert.Contains("HTTP 429", snapshot.Tenors.Single(x => x.Tenor == "10Y").Reason);
        Assert.Single(f.Store.Rows, x => x.Kind == RateObservationKinds.Intraday);
        Assert.Contains(f.Diagnostics.Failures, x => x.Scope == "rates-intraday:10Y");
        Assert.Contains(f.State.Health().FailedSources, x => x.StartsWith("intraday:10Y"));
        Assert.Equal(Now.AddSeconds(60), f.State.RetryAt(TreasuryTenor.Y10));

        f.Clock.Now = Now.AddSeconds(30);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, f.Intraday.Calls.Count);

        f.Clock.Now = Now.AddSeconds(61);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(5, f.Intraday.Calls.Count);
        Assert.Equal(Now.AddSeconds(61 + 120), f.State.RetryAt(TreasuryTenor.Y10));
    }

    [Fact]
    public async Task 백오프는_상한을_넘지_않고_성공하면_초기화된다()
    {
        var f = new Fixture();
        var fail = true;
        f.Intraday.Responses[TreasuryTenor.Y10] = () => fail ? throw new TimeoutException() : Quote(TreasuryTenor.Y10, 5.2);
        for (var i = 0; i < 12; i++)
        {
            f.Clock.Now = Now.AddHours(i);
            await f.Collector.RunOnceAsync(CancellationToken.None);
        }
        Assert.Equal(Now.AddHours(11).AddMinutes(15), f.State.RetryAt(TreasuryTenor.Y10));

        fail = false;
        f.Clock.Now = Now.AddHours(12);
        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Null(f.State.RetryAt(TreasuryTenor.Y10));
        Assert.Equal(5.2, f.State.Snapshot(f.Clock.Now).Tenors.Single(x => x.Tenor == "10Y").Value);
        Assert.Null(f.State.Health().LastError);
    }

    [Fact]
    public async Task FRED_실패는_격리되고_한_시간_뒤에만_재시도한다()
    {
        var f = new Fixture();
        f.Daily.Response = tenor => tenor == TreasuryTenor.Y2 ? throw new InvalidDataException("DGS2 관측치가 없습니다.") : new DailyRateSeries(tenor, tenor.FredSeries(), [new(new DateOnly(2026, 10, 1), 5.1)], Now, "fred");

        await f.Collector.RunOnceAsync(CancellationToken.None);
        f.Clock.Now = Now.AddMinutes(30);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, f.Daily.Calls.Count);
        Assert.Equal("partial", f.State.Snapshot(f.Clock.Now).Status);
        Assert.Contains(f.State.Health().FailedSources, x => x.StartsWith("daily:2Y"));
        Assert.Equal(RateModes.Unavailable, f.State.Snapshot(f.Clock.Now).Tenors.Single(x => x.Tenor == "2Y").Mode);

        f.Clock.Now = Now.AddMinutes(61);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(4, f.Daily.Calls.Count);
        Assert.Equal(TreasuryTenor.Y2, f.Daily.Calls[^1].Tenor);
    }

    [Fact]
    public async Task 저장_실패는_진단에_남고_수집_상태는_유지된다()
    {
        var f = new Fixture();
        f.Store.FailAppend = true;

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Contains(f.Diagnostics.Failures, x => x.Scope == "rates-store");
        Assert.Equal(5.237, f.State.Snapshot(Now).Tenors.Single(x => x.Tenor == "10Y").Value);
        Assert.Equal(0, f.State.Health().AppendedToday);
    }

    [Fact]
    public async Task 재시작_시_당일_기록에서_마지막_실시간_값을_복원한다()
    {
        var f = new Fixture();
        f.Store.Seed.Add(new RateObservation(Now.AddHours(-1), "10Y", 5.111, "yahoo:^TNX", Now.AddHours(-1).AddSeconds(-5), 5.2, RateObservationKinds.Intraday));
        f.Store.Seed.Add(new RateObservation(Now.AddMinutes(-30), "10Y", 5.222, "yahoo:^TNX", Now.AddMinutes(-30).AddSeconds(-5), 5.2, RateObservationKinds.Intraday));
        f.Store.Seed.Add(new RateObservation(Now.AddMinutes(-30), "2Y", 4.88, "fred:DGS2", Now.AddDays(-1), null, RateObservationKinds.Daily));
        f.Intraday.Responses[TreasuryTenor.Y10] = () => throw new TimeoutException();

        await f.Collector.RunOnceAsync(CancellationToken.None);

        var ten = f.State.Snapshot(Now).Tenors.Single(x => x.Tenor == "10Y");
        Assert.Equal(5.222, ten.Value);
        Assert.Equal("stale", ten.DelayStatus);
        Assert.Contains("TimeoutException", ten.Reason);
    }

    [Fact]
    public async Task 비활성이면_아무_출처도_호출하지_않는다()
    {
        var f = new Fixture();
        f.Options.Enabled = false;

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Empty(f.Daily.Calls);
        Assert.Empty(f.Intraday.Calls);
        Assert.Empty(f.Intraday.EtfCalls);
        Assert.Equal("disabled", f.State.Snapshot(Now).Status);
    }

    [Fact]
    public async Task ETF_비교를_끄면_ETF를_호출하지_않고_스냅샷에도_없다()
    {
        var f = new Fixture();
        f.Options.EtfEnabled = false;

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Empty(f.Intraday.EtfCalls);
        Assert.Empty(f.State.Snapshot(Now).EtfProxies);
        Assert.Equal(5, f.State.Health().AppendedToday);
    }

    [Fact]
    public async Task ETF는_자체_주기로만_다시_받고_변하지_않은_가격은_기록하지_않는다()
    {
        var f = new Fixture();
        await f.Collector.RunOnceAsync(CancellationToken.None);
        f.Clock.Now = Now.AddMinutes(1);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3, f.Intraday.EtfCalls.Count);
        Assert.Equal(4, f.Intraday.Calls.Count);

        f.Clock.Now = Now.AddMinutes(5);
        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Equal(6, f.Intraday.EtfCalls.Count);
        Assert.Equal(3, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Etf));

        f.Intraday.EtfResponses["IEF"] = () => Etf("IEF", 89.4, 89.0031, Now.AddMinutes(10));
        f.Clock.Now = Now.AddMinutes(10);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(4, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Etf));
        Assert.Equal("yahoo:IEF", f.Store.Rows.Last(x => x.Kind == RateObservationKinds.Etf).Source);
        Assert.Equal("10Y", f.Store.Rows.Last(x => x.Kind == RateObservationKinds.Etf).Tenor);
    }

    [Fact]
    public async Task ETF_실패는_격리되어_금리_상태를_바꾸지_않고_자체_주기로_백오프한다()
    {
        var f = new Fixture();
        f.Intraday.EtfResponses["IEF"] = () => throw new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests);

        await f.Collector.RunOnceAsync(CancellationToken.None);

        var snapshot = f.State.Snapshot(Now);
        Assert.Equal("ok", snapshot.Status);
        Assert.Equal(2, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Etf));
        var ief = snapshot.EtfProxies.Single(x => x.Symbol == "IEF");
        Assert.Equal(EtfProxyDivergence.Unknown, ief.Agreement);
        Assert.Contains("HTTP 429", ief.Reason);
        Assert.Equal(EtfProxyDivergence.Agree, snapshot.EtfProxies.Single(x => x.Symbol == "TLT").Agreement);
        Assert.Contains(f.Diagnostics.Failures, x => x.Scope == "rates-etf:IEF");
        Assert.Contains(f.State.Health().FailedSources, x => x == "etf:IEF: HTTP 429");
        Assert.Equal(Now.AddMinutes(5), f.State.EtfRetryAt("IEF"));

        f.Clock.Now = Now.AddMinutes(5);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(Now.AddMinutes(5 + 10), f.State.EtfRetryAt("IEF"));
    }

    [Fact]
    public async Task 재시작_시_당일_기록에서_ETF_가격을_복원하고_주기_전에는_다시_받지_않는다()
    {
        var f = new Fixture();
        f.Store.Seed.Add(new RateObservation(Now.AddMinutes(-2), "10Y", 89.11, "yahoo:IEF", Now.AddMinutes(-2).AddSeconds(-5), 89.0031, RateObservationKinds.Etf));
        f.Store.Seed.Add(new RateObservation(Now.AddMinutes(-1), "10Y", 89.22, "yahoo:IEF", Now.AddMinutes(-1).AddSeconds(-5), 89.0031, RateObservationKinds.Etf));
        f.Store.Seed.Add(new RateObservation(Now.AddMinutes(-1), "30Y", 70.0, "yahoo:UTHY", Now.AddMinutes(-1), 69.0, RateObservationKinds.Etf));

        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["SHY", "TLT"], f.Intraday.EtfCalls);
        Assert.Equal(89.22, f.State.Snapshot(Now).EtfProxies.Single(x => x.Symbol == "IEF").Price);
        Assert.Equal(2, f.Store.Rows.Count(x => x.Kind == RateObservationKinds.Etf));
    }

    [Fact]
    public async Task ETF_괴리가_연속되면_health에_경고가_남고_일치하면_사라진다()
    {
        var f = new Fixture();
        f.Intraday.EtfResponses["IEF"] = () => Etf("IEF", 88.5, 89.0031, f.Clock.Now.AddSeconds(-10));
        for (var i = 0; i < 2; i++)
        {
            f.Clock.Now = Now.AddMinutes(5 * i);
            await f.Collector.RunOnceAsync(CancellationToken.None);
        }
        Assert.Equal(2, f.State.EtfDivergeRuns("IEF"));
        Assert.Empty(f.State.Health().EtfDivergences);
        Assert.Contains(f.State.Snapshot(f.Clock.Now).Warnings, x => x.Contains("IEF") && x.Contains("어긋납니다"));
        Assert.DoesNotContain(f.State.Snapshot(f.Clock.Now).Warnings, x => x.Contains("연속"));

        f.Clock.Now = Now.AddMinutes(10);
        await f.Collector.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, f.State.EtfDivergeRuns("IEF"));
        var warning = Assert.Single(f.State.Health().EtfDivergences);
        Assert.Equal("10Y ETF IEF 괴리가 3회 연속 이어집니다.", warning);
        Assert.Contains(f.State.Snapshot(f.Clock.Now).Warnings, x => x == warning);

        f.Intraday.EtfResponses["IEF"] = () => Etf("IEF", 89.3, 89.0031, f.Clock.Now.AddSeconds(-10));
        f.Clock.Now = Now.AddMinutes(15);
        await f.Collector.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, f.State.EtfDivergeRuns("IEF"));
        Assert.Empty(f.State.Health().EtfDivergences);
    }

    [Theory]
    [InlineData("2026-10-02T13:00:00Z", 60)]
    [InlineData("2026-10-02T20:00:00Z", 60)]
    [InlineData("2026-10-02T22:00:00Z", 600)]
    [InlineData("2026-10-03T15:00:00Z", 600)]
    public void 다음_주기는_뉴욕_평일_주간에만_짧다(string at, int expectedSeconds)
    {
        var f = new Fixture();

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), f.Collector.NextDelay(DateTimeOffset.Parse(at)));
    }
}
