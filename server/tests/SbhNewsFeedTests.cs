using System.Net;
using Astra.Server.Application;
using Astra.Server.Domain.News;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class SbhNewsFeedTests
{
    [Fact]
    public async Task 공개_RSS의_한국어_기사와_정규_URL을_보존한다()
    {
        var handler = new FixtureHandler(HttpStatusCode.OK, """
            <rss><channel><item><guid isPermaLink="true">https://www.sbhnews.com/news/a</guid><title>연준 금리 인상 발표</title><description>국채 금리가 상승했다</description><link>https://www.sbhnews.com/news/a</link><pubDate>Sat, 26 Sep 2026 09:21:01 GMT</pubDate></item></channel></rss>
            """);
        var feed = new SbhNewsFeed(new NewsOptions { SbhNewsRssUrl = "https://sbh.test/feed.xml" }, new HttpClient(handler));

        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));

        Assert.Equal("연준 금리 인상 발표", row.Title);
        Assert.Equal(SbhNewsFeed.SourceName, row.Source);
        Assert.Equal(NewsFeedProviders.SbhNews, row.Provider);
        Assert.Equal("https://www.sbhnews.com/news/a", row.Url);
    }

    [Fact]
    public async Task RSS_전건에_필터를_적용하고_포함_제외_review_통계를_노출한다()
    {
        var handler = new FixtureHandler(HttpStatusCode.OK, """
            <rss><channel>
              <item><guid>market</guid><title>Iran missile strike disrupts oil supply</title></item>
              <item><guid>sports</guid><title>United striker scores goal in football match</title></item>
              <item><guid>review</guid><title>Market reaction remains unclear</title></item>
            </channel></rss>
            """);

        var batch = await new SbhNewsFeed(new NewsOptions(), new HttpClient(handler)).FetchAsync(1, CancellationToken.None);

        Assert.Equal(3, batch.Items.Count);
        Assert.Equal(NewsRelevanceDecisions.Include, batch.Items.Single(x => x.Id == "market").Relevance!.Decision);
        Assert.Equal(NewsRelevanceDecisions.Exclude, batch.Items.Single(x => x.Id == "sports").Relevance!.Decision);
        Assert.Equal(NewsRelevanceDecisions.Review, batch.Items.Single(x => x.Id == "review").Relevance!.Decision);
        var status = Assert.Single(batch.Providers);
        Assert.Equal(1, status.IncludedCount);
        Assert.Equal(1, status.ExcludedCount);
        Assert.Equal(1, status.ReviewCount);
        Assert.Equal(NewsRelevancePolicy.CurrentVersion, status.FilterPolicyVersion);
    }

    [Fact]
    public async Task ETag와_Last_Modified를_다음_요청에_전달한다()
    {
        var handler = new ConditionalHandler();
        var feed = new SbhNewsFeed(new NewsOptions { SbhNewsRssUrl = "https://sbh.test/feed.xml" }, new HttpClient(handler));

        await feed.FetchAsync(1, CancellationToken.None);
        var batch = await feed.FetchAsync(1, CancellationToken.None);

        Assert.Equal("empty", batch.Status);
        Assert.Equal("\"v1\"", handler.IfNoneMatch);
        Assert.NotNull(handler.IfModifiedSince);
    }

    [Fact]
    public void 공개_피드_TTL에_맞춰_15분_간격을_강제한다()
        => Assert.Equal(TimeSpan.FromMinutes(15), new SbhNewsFeed(new NewsOptions()).MinimumInterval);

    [Fact]
    public async Task 두번째_페이지는_외부_요청없이_빈_응답이다()
    {
        var handler = new FixtureHandler(HttpStatusCode.OK, "<rss><channel /></rss>");
        var feed = new SbhNewsFeed(new NewsOptions(), new HttpClient(handler));

        var batch = await feed.FetchAsync(2, CancellationToken.None);

        Assert.Equal("empty", batch.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "quota_wait")]
    [InlineData(HttpStatusCode.NotModified, "empty")]
    [InlineData(HttpStatusCode.InternalServerError, "failed")]
    public async Task HTTP_응답을_공급자_상태로_노출한다(HttpStatusCode status, string expected)
    {
        var batch = await new SbhNewsFeed(new NewsOptions(), new HttpClient(new FixtureHandler(status, ""))).FetchAsync(1, CancellationToken.None);

        Assert.Equal(expected, batch.Status);
        Assert.Equal(expected, Assert.Single(batch.Providers).Status);
    }

    [Fact]
    public async Task Retry_After를_공급자_상태에_보존한다()
    {
        var handler = new RetryHandler();
        var batch = await new SbhNewsFeed(new NewsOptions(), new HttpClient(handler)).FetchAsync(1, CancellationToken.None);

        Assert.Equal("quota_wait", batch.Status);
        Assert.Equal(TimeSpan.FromMinutes(7), Assert.Single(batch.Providers).RetryAfter);
    }

    [Fact]
    public async Task 공개_기사_본문을_분류_입력용으로_추출한다()
    {
        var handler = new ArticleHandler();
        var feed = new SbhNewsFeed(new NewsOptions(), new HttpClient(handler));
        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));

        var detail = await feed.DetailAsync(row.Id, CancellationToken.None);

        Assert.Equal("요약 문장", detail!.Summary);
        Assert.Contains("첫 문단", detail.Body);
        Assert.Contains("둘째 문단", detail.Body);
    }

    sealed class FixtureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    sealed class ConditionalHandler : HttpMessageHandler
    {
        public string? IfNoneMatch { get; private set; }
        public DateTimeOffset? IfModifiedSince { get; private set; }
        int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            IfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            IfModifiedSince = request.Headers.IfModifiedSince;
            if (_calls == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss><channel /></rss>") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            return Task.FromResult(response);
        }
    }

    sealed class ArticleHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath == "/feed.xml"
                ? "<rss><channel><item><guid>article-1</guid><title>Fed raises interest rates</title><link>https://www.sbhnews.com/news/a</link></item></channel></rss>"
                : "<meta name=\"description\" content=\"요약 문장\"><script>dangerouslySetInnerHTML\\\":{\\\"__html\\\":\\\"\\u003cp\\u003e첫 문단\\u003c/p\\u003e\\n\\u003cp\\u003e둘째 문단\\u003c/p\\u003e\\\"}</script>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    sealed class RetryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(7));
            return Task.FromResult(response);
        }
    }
}
