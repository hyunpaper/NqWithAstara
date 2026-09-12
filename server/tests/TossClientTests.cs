using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Astra.Server;
using Xunit;

namespace Astra.Server.Tests;

[Collection("TossClient serial")]
public sealed class TossClientTests : IDisposable
{
    readonly string _credentials = Path.GetTempFileName();
    public TossClientTests() { File.WriteAllText(_credentials, "API Key : test-id\nSecret Key : test-secret"); Environment.SetEnvironmentVariable("TOSS_CREDENTIALS_PATH", _credentials); }
    public void Dispose() { Environment.SetEnvironmentVariable("TOSS_CREDENTIALS_PATH", null); File.Delete(_credentials); }

    [Fact]
    public async Task ConcurrentCallsIssueOneOAuthToken()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"token_type\":\"Bearer\",\"expires_in\":3600}")
            : Json("{\"result\":[{\"symbol\":\"AAPL\",\"name\":\"애플\",\"englishName\":\"APPLE INC\",\"currency\":\"USD\",\"status\":\"ACTIVE\"}]}"));
        var client = Client(handler);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.Stocks("AAPL", CancellationToken.None)));
        Assert.Equal(1, handler.Paths.Count(x => x == "/oauth2/token"));
    }

    [Fact]
    public async Task HolidayCalendarUsesNextBusinessDayOpen()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":{\"today\":{\"date\":\"2026-07-03\",\"regularMarket\":null},\"nextBusinessDay\":{\"regularMarket\":{\"startTime\":\"2026-07-06T22:30:00+09:00\",\"endTime\":\"2026-07-07T05:00:00+09:00\"}}}}"));
        var session = await Client(handler).Session(DateTimeOffset.Parse("2026-07-03T23:00:00+09:00"), CancellationToken.None);
        Assert.False(session.IsOpen); Assert.Equal("휴장", session.Label); Assert.Equal(DateTimeOffset.Parse("2026-07-06T22:30:00+09:00"), session.NextOpen);
    }

    [Fact]
    public async Task ForbiddenTokenReturnsSanitizedIpGuidance()
    {
        var handler = new FixtureHandler((request, count) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") });
        var error = await Assert.ThrowsAsync<TossAuthException>(() => Client(handler).GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("허용 IP", error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.Equal("https://developers.tossinvest.com/docs", error.GuideUrl);
    }

    [Fact]
    public async Task TradesParsesSubPennyPriceAndTimestamp()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":[{\"price\":\"332.5576\",\"volume\":\"8\",\"timestamp\":\"2026-09-12T08:59:56.000+09:00\",\"currency\":\"USD\"}]}"));
        var trades = await Client(handler).Trades("AAPL", 50, CancellationToken.None);
        var trade = Assert.Single(trades);
        Assert.Equal("AAPL", trade.Symbol);
        Assert.Equal(332.5576m, trade.Price);
        Assert.Equal(8m, trade.Volume);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T08:59:56.000+09:00"), trade.Timestamp);
        Assert.Equal("USD", trade.Currency);
    }

    [Fact]
    public async Task StockInfosParsesNullableLeverageAndSharesOutstanding()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":[{\"symbol\":\"AAPL\",\"name\":\"애플\",\"englishName\":\"APPLE INC\",\"securityType\":\"STOCK\",\"market\":\"NASDAQ\",\"isCommonShare\":true,\"status\":\"ACTIVE\",\"leverageFactor\":null,\"sharesOutstanding\":\"15000000000\"}]}"));
        var infos = await Client(handler).StockInfos("AAPL", CancellationToken.None);
        var info = Assert.Single(infos);
        Assert.Equal("AAPL", info.Symbol);
        Assert.Equal("APPLE INC", info.Name);
        Assert.Equal("STOCK", info.SecurityType);
        Assert.Equal("NASDAQ", info.Market);
        Assert.True(info.IsCommonShare);
        Assert.Equal("ACTIVE", info.Status);
        Assert.Null(info.LeverageFactor);
        Assert.Equal(15000000000m, info.SharesOutstanding);
    }

    [Fact]
    public async Task AccountsParsesSeqAndTypeWithoutAccountNumber()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":[{\"accountSeq\":1,\"accountType\":\"COMMISSION_FREE\",\"accountNo\":\"1234567890\"}]}"));
        var accounts = await Client(handler).Accounts(CancellationToken.None);
        var account = Assert.Single(accounts);
        Assert.Equal(1, account.AccountSeq);
        Assert.Equal("COMMISSION_FREE", account.AccountType);
    }

    [Fact]
    public async Task CommissionsParsesRateAndOptionalEndDate()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":[{\"marketCountry\":\"US\",\"commissionRate\":\"0.001\",\"startDate\":\"2026-01-01\",\"endDate\":\"2026-09-13\"}]}"));
        var commissions = await Client(handler).Commissions(1, CancellationToken.None);
        var commission = Assert.Single(commissions);
        Assert.Equal("US", commission.MarketCountry);
        Assert.Equal(0.001m, commission.Rate);
        Assert.Equal(new DateOnly(2026, 1, 1), commission.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 13), commission.EndDate);
    }

    [Fact]
    public async Task CommissionsRequestSendsAccountHeader()
    {
        string? accountHeader = null;
        var handler = new FixtureHandler((request, count) =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token") return Json("{\"access_token\":\"fixture\",\"expires_in\":3600}");
            accountHeader = request.Headers.TryGetValues("X-Tossinvest-Account", out var values) ? values.First() : null;
            return Json("{\"result\":[]}");
        });
        await Client(handler).Commissions(42, CancellationToken.None);
        Assert.Equal("42", accountHeader);
    }

    [Fact]
    public async Task ClosedOrdersParsesExecutionAndPaginationCursor()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":{\"orders\":[{\"orderId\":\"o-1\",\"symbol\":\"AAPL\",\"side\":\"BUY\",\"orderType\":\"LIMIT\",\"status\":\"CLOSED\",\"orderedAt\":\"2026-09-10T05:00:00.000+09:00\",\"execution\":{\"filledQuantity\":\"3\",\"averageFilledPrice\":\"150.25\",\"filledAmount\":\"450.75\",\"commission\":\"0.45\",\"filledAt\":\"2026-09-10T05:00:01.000+09:00\"}}],\"nextCursor\":\"cur-2\",\"hasNext\":true}}"));
        var page = await Client(handler).ClosedOrders(1, null, null, null, 100, CancellationToken.None);
        var order = Assert.Single(page.Orders);
        Assert.Equal("o-1", order.OrderId);
        Assert.NotNull(order.Execution);
        Assert.Equal(3m, order.Execution!.FilledQuantity);
        Assert.Equal(150.25m, order.Execution.AverageFilledPrice);
        Assert.Equal("cur-2", page.NextCursor);
        Assert.True(page.HasNext);
    }

    [Fact]
    public async Task ClosedOrdersWithoutExecutionLeavesItNull()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":{\"orders\":[{\"orderId\":\"o-2\",\"symbol\":\"AAPL\",\"side\":\"SELL\",\"orderType\":\"MARKET\",\"status\":\"CLOSED\",\"orderedAt\":\"2026-09-10T05:00:00.000+09:00\"}],\"nextCursor\":null,\"hasNext\":false}}"));
        var page = await Client(handler).ClosedOrders(1, null, null, null, 100, CancellationToken.None);
        var order = Assert.Single(page.Orders);
        Assert.Null(order.Execution);
        Assert.Null(page.NextCursor);
        Assert.False(page.HasNext);
    }

    [Fact]
    public async Task HoldingsParsesItemsWithoutAggregatedSummary()
    {
        var handler = new FixtureHandler((request, count) => request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json("{\"result\":{\"items\":[{\"symbol\":\"AAPL\",\"quantity\":\"10\",\"averagePurchasePrice\":\"140.5\",\"lastPrice\":\"150.25\"}],\"totalValuationAmount\":\"1502.5\"}}"));
        var holdings = await Client(handler).Holdings(1, CancellationToken.None);
        var holding = Assert.Single(holdings);
        Assert.Equal("AAPL", holding.Symbol);
        Assert.Equal(10m, holding.Quantity);
        Assert.Equal(140.5m, holding.AveragePurchasePrice);
        Assert.Equal(150.25m, holding.LastPrice);
    }

    static TossClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://openapi.tossinvest.com/") });
    static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    sealed class FixtureHandler(Func<HttpRequestMessage, int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        int _count; public ConcurrentBag<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Paths.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(reply(request, Interlocked.Increment(ref _count))); }
    }
}

[CollectionDefinition("TossClient serial", DisableParallelization = true)] public sealed class TossClientCollection;
