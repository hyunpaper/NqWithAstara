using System.Net;
using Astra.Server.Application;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class FoxNewsRssFeedTests
{
    [Fact]
    public async Task 공식_RSS의_제목_요약_원문_URL_출처를_보존한다()
    {
        var xml = """
            <rss><channel><item>
              <guid>fox-1</guid><title>Market headline</title>
              <description><![CDATA[<p>Market &amp; earnings summary</p>]]></description>
              <content:encoded xmlns:content="http://purl.org/rss/1.0/modules/content/">Full feed body</content:encoded>
              <link>https://www.foxnews.com/market/story</link>
              <pubDate>Sat, 26 Sep 2026 00:19:02 -0400</pubDate>
            </item></channel></rss>
            """;
        var handler = new FixtureHandler(HttpStatusCode.OK, xml);
        var feed = new FoxNewsRssFeed(new NewsOptions
        {
            FoxNewsRssUrl = "https://fox.test/latest.xml"
        }, new HttpClient(handler));

        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));

        Assert.Equal("fox-1", row.Id);
        Assert.Equal("Market headline", row.Title);
        Assert.Equal("Market & earnings summary", row.Summary);
        Assert.Equal("Fox News", row.Source);
        Assert.Equal("https://www.foxnews.com/market/story", row.Url);
        Assert.Equal(NewsFeedProviders.FoxNewsRss, row.Provider);
        Assert.Empty(row.Content);
        Assert.Equal("https://fox.test/latest.xml", handler.RequestUri);
        Assert.Null(await feed.DetailAsync(row.Id, CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task 중복_ID는_한_건만_반환한다()
    {
        var xml = """
            <rss><channel>
              <item><guid>same-id</guid><title>첫 기사</title><link>https://www.foxnews.com/a</link></item>
              <item><guid>same-id</guid><title>중복 기사</title><link>https://www.foxnews.com/b</link></item>
            </channel></rss>
            """;
        var feed = Feed(HttpStatusCode.OK, xml);

        var row = Assert.Single(await feed.ListAsync(1, CancellationToken.None));

        Assert.Equal("첫 기사", row.Title);
    }

    [Fact]
    public async Task 잘못된_XML은_invalid_response_상태다()
    {
        var batch = await Feed(HttpStatusCode.OK, "not xml").FetchAsync(1, CancellationToken.None);

        Assert.Empty(batch.Items);
        Assert.Equal("invalid_response", batch.Status);
        var provider = Assert.Single(batch.Providers);
        Assert.Equal(NewsFeedProviders.FoxNewsRss, provider.Provider);
        Assert.Equal("invalid_response", provider.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "quota_wait")]
    [InlineData(HttpStatusCode.NotModified, "empty")]
    public async Task HTTP_응답을_공급자_상태로_노출한다(HttpStatusCode code, string expected)
    {
        var batch = await Feed(code, "").FetchAsync(1, CancellationToken.None);

        Assert.Empty(batch.Items);
        Assert.Equal(expected, batch.Status);
        Assert.Equal(expected, Assert.Single(batch.Providers).Status);
    }

    [Fact]
    public async Task HTTP_예외는_failed_상태다()
    {
        var feed = new FoxNewsRssFeed(new NewsOptions(), new HttpClient(new ThrowingHandler()));

        var batch = await feed.FetchAsync(1, CancellationToken.None);

        Assert.Equal("failed", batch.Status);
        Assert.Equal("failed", Assert.Single(batch.Providers).Status);
    }

    static FoxNewsRssFeed Feed(HttpStatusCode status, string body)
        => new(new NewsOptions(), new HttpClient(new FixtureHandler(status, body)));

    sealed class FixtureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("network failure"));
    }
}
