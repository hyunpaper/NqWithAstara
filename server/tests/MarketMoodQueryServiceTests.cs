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

        var result = await new MarketMoodQueryService(gateway, new MoodClock(Now)).GetAsync();

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

        var result = await new MarketMoodQueryService(gateway, new MoodClock(Now)).GetAsync();

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

        var result = await new MarketMoodQueryService(gateway, new MoodClock(Now)).GetAsync();

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
        var service = new MarketMoodQueryService(gateway, clock);

        var first = await service.GetAsync();
        clock.Now = Now.AddSeconds(59);
        var second = await service.GetAsync();

        Assert.Same(first, second);
        Assert.Equal(1, gateway.SessionCalls);
        Assert.Equal(5, gateway.PriceCalls);
        Assert.Equal(5, gateway.DailyCalls);
    }

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
