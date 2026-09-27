using System.Net;
using System.Text;
using Astra.Server.Application;
using Astra.Server.Domain.News;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class OllamaNewsRelevanceAdjudicatorTests
{
    [Fact]
    public async Task SBH_review의_정확한_근거가_있는_JSON만_승인한다()
    {
        var json = "{\"response\":\"{\\\"decision\\\":\\\"include\\\",\\\"eventKind\\\":\\\"monetary_policy\\\",\\\"actor\\\":\\\"Fed\\\",\\\"action\\\":\\\"raises\\\",\\\"targetId\\\":\\\"MARKET\\\",\\\"targetKind\\\":\\\"market\\\",\\\"evidence\\\":\\\"Fed raises rates\\\",\\\"reason\\\":\\\"explicit_event\\\"}\"}";
        var adjudicator = Build(json);

        var result = await adjudicator.AdjudicateAsync(Item("Fed raises rates after meeting"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("sbh-relevance-adjudication-v1", result.Classifier);
    }

    [Theory]
    [InlineData("{\"response\":\"not-json\"}")]
    [InlineData("{\"response\":\"{\\\"decision\\\":\\\"include\\\",\\\"eventKind\\\":\\\"monetary_policy\\\",\\\"actor\\\":\\\"Fed\\\",\\\"action\\\":\\\"raises\\\",\\\"targetId\\\":\\\"MARKET\\\",\\\"targetKind\\\":\\\"market\\\",\\\"evidence\\\":\\\"invented evidence\\\",\\\"reason\\\":\\\"x\\\"}\"}")]
    public async Task malformed나_본문밖_근거는_pending을_위해_null을_반환한다(string response)
    {
        Assert.Null(await Build(response).AdjudicateAsync(Item("Fed raises rates"), CancellationToken.None));
    }

    [Fact]
    public async Task 비활성이나_SBH외_공급자는_HTTP를_호출하지_않는다()
    {
        var handler = new CountingHandler("{}");
        var disabled = new OllamaNewsRelevanceAdjudicator(new NewsOptions(), new HttpClient(handler));
        var enabled = new OllamaNewsRelevanceAdjudicator(new NewsOptions { SbhRelevanceAdjudicationEnabled = true }, new HttpClient(handler));

        Assert.Null(await disabled.AdjudicateAsync(Item("Fed raises rates"), CancellationToken.None));
        Assert.Null(await enabled.AdjudicateAsync(Item("Fed raises rates") with { Provider = "other" }, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    static OllamaNewsRelevanceAdjudicator Build(string response)
        => new(new NewsOptions { SbhRelevanceAdjudicationEnabled = true }, new HttpClient(new CountingHandler(response)));

    static NewsFeedItem Item(string title) => new("id", title, "", "SBH", DateTimeOffset.UtcNow, [],
        Provider: NewsFeedProviders.SbhNews,
        Relevance: new NewsRelevanceAssessment(NewsRelevancePolicy.CurrentVersion, NewsRelevanceDecisions.Review,
            "unknown", "", "", [], title, "insufficient_actor_action_target_context"));

    sealed class CountingHandler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
