using System.Net;
using System.Net.Http;
using Astra.Server.Application;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class MarketauxNewsFeedTests
{
    [Fact]
    public async Task 키가_없으면_RSS를호출하고_비XML이면_빈피드를_반환한다()
    {
        var handler = new FixtureHandler("{}");
        var feed = new MarketauxNewsFeed(new NewsOptions(), new HttpClient(handler));
        var rows = await feed.ListAsync(1, CancellationToken.None);
        Assert.Empty(rows);
        Assert.True(handler.Called);
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
        Assert.Contains("limit=3", handler.Request!.QueryAndPath);
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

    [Fact]
    public async Task RSS_description_summary_content를_각각_보존한다()
    {
        var handler = new FixtureHandler("<rss xmlns:content=\"http://purl.org/rss/1.0/modules/content/\"><channel><item><title>제목</title><description>설명 근거</description><summary>요약 근거</summary><content:encoded>본문 근거</content:encoded><link>id-body</link></item></channel></rss>");
        var feed = new MarketauxNewsFeed(new NewsOptions { GoogleNewsUrl = "https://google.test/rss", YahooNewsUrl = "https://google.test/rss" }, new HttpClient(handler));

        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));

        Assert.Contains("설명 근거", row.Summary);
        Assert.Contains("요약 근거", row.Summary);
        Assert.Equal("본문 근거", row.Content);
    }

    [Fact]
    public async Task RSS_HTML을_평문화하고_본문을_제한한다()
    {
        var longText = new string('가', MarketauxNewsFeed.RssTextLimit + 20);
        var handler = new FixtureHandler($"<rss xmlns:content=\"http://purl.org/rss/1.0/modules/content/\"><channel><item><title>제목</title><description>&lt;p&gt;설명 &amp;amp; 근거&lt;/p&gt;</description><content:encoded><![CDATA[<p>{longText}</p>]]></content:encoded><link>id-html</link></item></channel></rss>");
        var feed = new MarketauxNewsFeed(new NewsOptions { GoogleNewsUrl = "https://google.test/rss", YahooNewsUrl = "https://google.test/rss" }, new HttpClient(handler));

        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));

        Assert.Equal("설명 & 근거", row.Summary);
        Assert.Equal(MarketauxNewsFeed.RssTextLimit, row.Content.Length);
        Assert.DoesNotContain("<p>", row.Content);
    }



    [Fact]
    public async Task OneBrokenRssSourceDoesNotHideAnotherHealthySource()
    {
        var handler = new MixedRssHandler();
        var feed = new MarketauxNewsFeed(new NewsOptions
        {
            GoogleNewsUrl = "https://google.test/rss",
            YahooNewsUrl = "https://yahoo.test/rss"
        }, new HttpClient(handler));
        var rows = await feed.ListAsync(1, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal("정상 기사", row.Title);
    }

    [Fact]
    public async Task RSS_부분실패와_전체실패를_구조화상태로_구분한다()
    {
        var partial = new MarketauxNewsFeed(new NewsOptions
        {
            GoogleNewsUrl = "https://google.test/rss",
            YahooNewsUrl = "https://yahoo.test/rss"
        }, new HttpClient(new MixedRssHandler()));
        var failed = new MarketauxNewsFeed(new NewsOptions
        {
            GoogleNewsUrl = "https://google.test/rss",
            YahooNewsUrl = "https://yahoo.test/rss"
        }, new HttpClient(new FixtureHandler("not xml")));

        var partialBatch = await partial.FetchAsync(1, CancellationToken.None);
        var failedBatch = await failed.FetchAsync(1, CancellationToken.None);

        Assert.Equal("partial", partialBatch.Status);
        Assert.Contains(partialBatch.Providers, x => x.Status == "invalid_response");
        Assert.Contains(partialBatch.Providers, x => x.Status == "ok");
        Assert.Equal("failed", failedBatch.Status);
        Assert.All(failedBatch.Providers, x => Assert.Equal("invalid_response", x.Status));
    }

    [Fact]
    public async Task RSS_304는_정상빈응답이고_429는_quota_wait다()
    {
        var notModified = new MarketauxNewsFeed(new NewsOptions(),
            new HttpClient(new ConstantStatusHandler(HttpStatusCode.NotModified)));
        var throttled = new MarketauxNewsFeed(new NewsOptions(),
            new HttpClient(new ConstantStatusHandler(HttpStatusCode.TooManyRequests)));

        var empty = await notModified.FetchAsync(1, CancellationToken.None);
        var quota = await throttled.FetchAsync(1, CancellationToken.None);

        Assert.Equal("empty", empty.Status);
        Assert.All(empty.Providers, x => Assert.Equal("empty", x.Status));
        Assert.Equal("quota_wait", quota.Status);
        Assert.All(quota.Providers, x => Assert.Equal("quota_wait", x.Status));
    }

    [Fact]
    public async Task Marketaux_실패는_RSS예산을_소비하지_않는다()
    {
        var handler = new ConstantStatusHandler(HttpStatusCode.TooManyRequests);
        var feed = new MarketauxNewsFeed(new NewsOptions { MarketauxApiKey = "fixture" }, new HttpClient(handler));

        var batch = await feed.FetchAsync(1, CancellationToken.None);

        Assert.Equal("quota_wait", batch.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("marketaux", Assert.Single(batch.Providers).Provider);
    }

    [Fact]
    public void RSS_요청예산은_실제공급자수로_나눈다()
    {
        var twoSources = new MarketauxNewsFeed(new NewsOptions { RssDailyRequestLimit = 100 });
        var oneSource = new MarketauxNewsFeed(new NewsOptions
        {
            RssDailyRequestLimit = 100,
            YahooNewsUrl = "https://same.test/rss",
            GoogleNewsUrl = "https://same.test/rss"
        });

        Assert.Equal(50, twoSources.DailyRequestLimit);
        Assert.Equal(100, oneSource.DailyRequestLimit);
    }

    [Fact]
    public async Task 원문URL은_http_https만_노출한다()
    {
        var json = """{"data":[{"uuid":"u1","title":"시장 뉴스","description":"설명","source":"Reuters","published_at":"2026-09-20T01:00:00Z","url":"javascript:alert(1)","entities":[]}]}""";
        var marketaux = new MarketauxNewsFeed(new NewsOptions { MarketauxApiKey = "fixture" },
            new HttpClient(new FixtureHandler(json)));
        var rss = new MarketauxNewsFeed(new NewsOptions
        {
            GoogleNewsUrl = "https://same.test/rss",
            YahooNewsUrl = "https://same.test/rss"
        }, new HttpClient(new FixtureHandler("<rss><channel><item><title>기사</title><link>ftp://example.test/a</link></item></channel></rss>")));

        Assert.Null(Assert.Single(await marketaux.ListAsync(1, CancellationToken.None)).Url);
        Assert.Null(Assert.Single(await rss.ListAsync(1, CancellationToken.None)).Url);
    }





    [Fact]
    public async Task OneTimedOutRssSourceDoesNotHideAnotherHealthySource()
    {
        var feed = new MarketauxNewsFeed(new NewsOptions
        {
            GoogleNewsUrl = "https://google.test/rss",
            YahooNewsUrl = "https://yahoo.test/rss"
        }, new HttpClient(new MixedTimeoutRssHandler()));
        var rows = await feed.ListAsync(1, CancellationToken.None);
        Assert.Equal("정상 기사", Assert.Single(rows).Title);
    }
    [Fact]
    public async Task CallerCancellationIsNotSwallowedByRssFallback()
    {
        var feed = new MarketauxNewsFeed(new NewsOptions(), new HttpClient(new CancelHandler()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.ListAsync(1, cts.Token));
    }

    sealed class MixedTimeoutRssHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.StartsWith("google", StringComparison.Ordinal))
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("provider timeout"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss><channel><item><title>정상 기사</title><link>id-2</link><source>Yahoo</source><pubDate>Sun, 20 Sep 2026 01:00:00 GMT</pubDate></item></channel></rss>")
            });
        }
    }

    sealed class CancelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"));
    }

    sealed class MixedRssHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri!.Host.StartsWith("google", StringComparison.Ordinal)
                ? "not xml"
                : "<rss><channel><item><title>정상 기사</title><link>id-1</link><source>Yahoo</source><pubDate>Sun, 20 Sep 2026 01:00:00 GMT</pubDate></item></channel></rss>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
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

    sealed class ConstantStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
    sealed record RequestInfo(string QueryAndPath);
}
