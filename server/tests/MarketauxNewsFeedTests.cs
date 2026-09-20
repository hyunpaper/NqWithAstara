using System.Net;
using System.Net.Http;
using Astra.Server.Application;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class MarketauxNewsFeedTests
{
    [Fact]
    public async Task 키가_없으면_외부호출없이_빈피드를_반환한다()
    {
        var handler = new FixtureHandler("{}");
        var feed = new MarketauxNewsFeed(new NewsOptions(), new HttpClient(handler));
        var rows = await feed.ListAsync(1, CancellationToken.None);
        Assert.Empty(rows);
        Assert.False(handler.Called);
    }

    [Fact]
    public async Task Marketaux_엔터티_스키마를_티커와_섹터로_정규화한다()
    {
        var json = """{"data":[{"uuid":"u1","title":"시장 뉴스","description":"설명","source":"Reuters","published_at":"2026-09-20T01:00:00Z","entities":[{"symbol":"nvda","name":"NVIDIA","industry":"Semiconductors","sentiment_score":0.7,"match_score":0.9}]}]}""";
        var handler = new FixtureHandler(json);
        var feed = new MarketauxNewsFeed(new NewsOptions { MarketauxApiKey = "fixture" }, new HttpClient(handler));
        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));
        Assert.Equal("NVDA", Assert.Single(row.Tickers));
        Assert.Equal("Semiconductors", Assert.Single(row.Entities!).Industry);
        Assert.Contains("limit=100", handler.Request!.QueryAndPath);
    }

    [Fact]
    public async Task 엔터티없는_거시기사를_버리지않는다()
    {
        var handler = new FixtureHandler("""{"data":[{"uuid":"macro-1","title":"금리 동결","description":"시장 전체 영향","source":"Reuters","published_at":"2026-09-20T01:00:00Z","entities":[]}]}""");
        var feed = new MarketauxNewsFeed(new NewsOptions { MarketauxApiKey = "fixture" }, new HttpClient(handler));
        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));
        Assert.Equal("macro-1", row.Id);
        Assert.Empty(row.Tickers);
        Assert.Empty(row.Entities!);
    }

    sealed class FixtureHandler(string body) : HttpMessageHandler
    {
        public bool Called { get; private set; }
        public RequestInfo? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Called = true; Request = new RequestInfo(request.RequestUri!.Query + request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    sealed record RequestInfo(string QueryAndPath);
}
