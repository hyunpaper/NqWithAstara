using Astra.Server.Application;
using Astra.Server.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using Xunit;

namespace Astra.Server.Tests;

public sealed class MarketMoodQueryServiceTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T15:00:00Z");
    static readonly DateTimeOffset SessionStart = Now.AddHours(-1);

    [Fact]
    public async Task AvailableAssetsUseEqualWeightAndExposeProxyEvidence()
    {
        var gateway = OpenGateway();
        gateway.Prices["GLD"] = (110, Now.AddMinutes(-1));
        gateway.Prices["USO"] = (90, Now.AddMinutes(-1));
        gateway.Prices["QQQ"] = (100, Now.AddMinutes(-1));
        gateway.Prices["IBIT"] = (105, Now.AddMinutes(-1));
        gateway.Prices["TLT"] = (95, Now.AddMinutes(-1));

        var result = await Service(gateway).GetAsync();

        Assert.Equal("available", result.Status);
        Assert.Equal(5, result.AvailableCount);
        Assert.Equal(0, result.Score);
        Assert.Equal("flat", result.Direction);
        Assert.Equal(["gold", "oil", "nasdaq", "bitcoin", "us-treasury"], result.Evidence);
        Assert.All(result.Assets, asset =>
        {
            Assert.True(asset.Proxy);
            Assert.True(asset.IsAvailable);
            Assert.Equal("fresh", asset.DelayStatus);
            Assert.Equal("open", asset.SessionStatus);
        });
        Assert.Contains(result.Limitations, value => value.Contains("ETF 프록시", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingAndStaleAssetsAreExcludedFromTheAggregate()
    {
        var gateway = OpenGateway();
        gateway.Prices["GLD"] = (110, Now.AddMinutes(-1));
        gateway.Prices["USO"] = (80, Now.AddMinutes(-5));
        gateway.Prices.Remove("IBIT");

        var result = await Service(gateway).GetAsync();

        Assert.Equal("partial", result.Status);
        Assert.Equal(3, result.AvailableCount);
        Assert.Equal(3.33, result.Score);
        Assert.Equal(["gold", "nasdaq", "us-treasury"], result.Evidence);
        var stale = Assert.Single(result.Assets, asset => asset.Symbol == "USO");
        Assert.False(stale.IsAvailable);
        Assert.Equal("stale", stale.DelayStatus);
        Assert.NotNull(stale.ChangePercent);
        Assert.Contains("지연", stale.Reason);
        var missing = Assert.Single(result.Assets, asset => asset.Symbol == "IBIT");
        Assert.False(missing.IsAvailable);
        Assert.Equal("unavailable", missing.DelayStatus);
        Assert.Contains("조회", missing.Reason);
    }

    [Fact]
    public async Task ClosedSessionSkipsQuotesAndReturnsStructuredReasons()
    {
        var gateway = OpenGateway();
        gateway.CurrentSession = new(false, "휴장", null, null, null);

        var result = await Service(gateway).GetAsync();

        Assert.Equal("unavailable", result.Status);
        Assert.Null(result.Score);
        Assert.Empty(result.Evidence);
        Assert.Equal(0, gateway.PriceCalls);
        Assert.Equal(0, gateway.DailyCalls);
        Assert.All(result.Assets, asset =>
        {
            Assert.Equal("closed", asset.SessionStatus);
            Assert.Contains("휴장", asset.Reason);
        });
    }

    [Fact]
    public async Task RepeatedReadsWithinOneMinuteReuseTheSnapshot()
    {
        var gateway = OpenGateway();
        var clock = new MoodClock(Now);
        var service = new MarketMoodQueryService(gateway, new MoodCalendar(), clock);

        var first = await service.GetAsync();
        clock.Now = Now.AddSeconds(59);
        var second = await service.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(1, gateway.SessionCalls);
        Assert.Equal(5, gateway.PriceCalls);
        Assert.Equal(5, gateway.DailyCalls);
    }

    [Fact]
    public async Task EconomicCalendarPreservesPublishedAndUnpublishedEvidence()
    {
        var events = new[]
        {
            new EconomicCalendarEvent("cpi", EconomicEventKinds.CPI, "소비자물가지수", Now.AddHours(-1), "published", "2.8", "2.7", "2.6", "%", "risk_off", "unknown", "공식 통계", null),
            new EconomicCalendarEvent("claims", EconomicEventKinds.InitialJoblessClaims, "신규 실업수당 청구", Now.AddMinutes(-30), "published", "245,000", "230,000", "225,000", "건", "risk_off", "unknown", "공식 통계", null),
            new EconomicCalendarEvent("fed", EconomicEventKinds.FOMC, "연준 의장 연설", Now.AddHours(2), "unpublished", null, null, null, null, "unknown", "unknown", "공식 일정", "아직 발표되지 않았습니다.")
        };
        var calendar = new MoodCalendar(new(DateOnly.Parse("2026-09-22"), "America/New_York", "partial", "검증 제공자", Now, null, events));

        var result = await new MarketMoodQueryService(OpenGateway(), calendar, new MoodClock(Now)).GetAsync();

        Assert.Equal("partial", result.EconomicCalendar.Status);
        Assert.Equal("2.8", result.EconomicCalendar.Events[0].Actual);
        Assert.Equal("negative", result.EconomicCalendar.Events[0].BondImpact);
        Assert.Equal("positive", result.EconomicCalendar.Events[1].BondImpact);
        Assert.Null(result.EconomicCalendar.Events[2].Actual);
        Assert.Equal("unknown", result.EconomicCalendar.Events[2].BondImpact);
        Assert.Equal("unpublished", result.EconomicCalendar.Events[2].Status);
    }

    static MarketMoodQueryService Service(MoodGateway gateway) =>
        new(gateway, new MoodCalendar(), new MoodClock(Now));

    static MoodGateway OpenGateway()
    {
        var gateway = new MoodGateway(new(true, "정규장", null, SessionStart, SessionStart.AddHours(6.5)));
        foreach (var symbol in new[] { "GLD", "USO", "QQQ", "IBIT", "TLT" })
        {
            gateway.Prices[symbol] = (100, Now.AddMinutes(-1));
            gateway.Daily[symbol] = [new(SessionStart.AddDays(-1), 100, 100, 100, 100, 1)];
        }
        return gateway;
    }
}

public sealed class MarketMoodApiContractTests
{
    [Fact]
    public async Task GetReturnsCamelCaseProxyAndAggregationContract()
    {
        var root = Directory.CreateTempSubdirectory("astra-market-mood-").FullName;
        var now = DateTimeOffset.Parse("2026-09-22T15:00:00Z");
        var gateway = new MoodGateway(new(true, "정규장", null, now.AddHours(-1), now.AddHours(5.5)));
        foreach (var symbol in new[] { "GLD", "USO", "QQQ", "IBIT", "TLT" })
        {
            gateway.Prices[symbol] = (101, now.AddMinutes(-1));
            gateway.Daily[symbol] = [new(now.AddDays(-1), 100, 100, 100, 100, 1)];
        }

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(root);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMarketDataGateway>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<IMarketDataGateway>(gateway);
                services.AddSingleton<TimeProvider>(new MoodClock(now));
            });
        });

        try
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/market-mood");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rootElement = json.RootElement;
            Assert.Equal("equal_weight_available_only", rootElement.GetProperty("aggregation").GetString());
            Assert.Equal(5, rootElement.GetProperty("availableCount").GetInt32());
            Assert.Equal(5, rootElement.GetProperty("evidence").GetArrayLength());
            Assert.Equal("unsupported", rootElement.GetProperty("economicCalendar").GetProperty("status").GetString());
            Assert.Equal(0, rootElement.GetProperty("economicCalendar").GetProperty("events").GetArrayLength());
            var asset = rootElement.GetProperty("assets")[0];
            Assert.True(asset.GetProperty("proxy").GetBoolean());
            Assert.Equal("fresh", asset.GetProperty("delayStatus").GetString());
            Assert.Equal("open", asset.GetProperty("sessionStatus").GetString());
            Assert.Equal(JsonValueKind.Null, asset.GetProperty("reason").ValueKind);
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

sealed class MoodClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class MoodCalendar(EconomicCalendarSnapshot? snapshot = null) : IEconomicCalendarProvider
{
    public Task<EconomicCalendarSnapshot> GetAsync(DateOnly marketDate, DateTimeOffset asOf, CancellationToken ct) =>
        Task.FromResult(snapshot ?? new EconomicCalendarSnapshot(marketDate, "America/New_York", "unsupported", "미연결", asOf, "미지원", []));
}

sealed class MoodGateway(MarketSession session) : IMarketDataGateway
{
    public MarketSession CurrentSession { get; set; } = session;
    public Dictionary<string, (double Price, DateTimeOffset At)> Prices { get; } = [];
    public Dictionary<string, IReadOnlyList<Candle>> Daily { get; } = [];
    public int SessionCalls { get; private set; }
    public int PriceCalls { get; private set; }
    public int DailyCalls { get; private set; }

    public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct)
    {
        PriceCalls++;
        return Prices.TryGetValue(symbol, out var value)
            ? Task.FromResult(value)
            : Task.FromException<(double, DateTimeOffset)>(new HttpRequestException("missing"));
    }

    public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct)
    {
        DailyCalls++;
        return Task.FromResult(Daily.TryGetValue(symbol, out var value) ? value : (IReadOnlyList<Candle>)[]);
    }

    public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct)
    {
        SessionCalls++;
        return Task.FromResult(CurrentSession);
    }

    public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
    public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("");
    public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossTrade>>([]);
    public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>([]);
    public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) => Task.FromResult<IReadOnlyList<TossAccount>>([]);
    public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossCommission>>([]);
    public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => Task.FromResult(new TossOrderPage([], null, false));
    public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossHolding>>([]);
}
