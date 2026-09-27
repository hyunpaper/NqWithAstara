using System.Net;
using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData("bad_kind", "market", "Fed", "raises")]
    [InlineData("monetary_policy", "bad_target", "Fed", "raises")]
    [InlineData("monetary_policy", "market", "MARKET", "raises")]
    [InlineData("monetary_policy", "market", "Fed", "OIL")]
    public async Task include는_enum과_actor_action_exact_substring을_엄격히_검증한다(
        string eventKind, string targetKind, string actor, string action)
    {
        var decision = JsonSerializer.Serialize(new { decision = "include", eventKind, actor, action,
            targetId = "MARKET", targetKind, evidence = "Fed raises rates", reason = "x" });
        var response = JsonSerializer.Serialize(new { response = decision });

        Assert.Null(await Build(response).AdjudicateAsync(Item("Fed raises rates"), CancellationToken.None));
    }

    [Fact]
    public async Task unknown_field와_잘못된_exclude_schema는_거부한다()
    {
        var unknown = "{\"response\":\"{\\\"decision\\\":\\\"exclude\\\",\\\"eventKind\\\":\\\"unknown\\\",\\\"actor\\\":\\\"\\\",\\\"action\\\":\\\"\\\",\\\"targetId\\\":\\\"\\\",\\\"targetKind\\\":\\\"\\\",\\\"evidence\\\":\\\"Fed raises rates\\\",\\\"reason\\\":\\\"x\\\",\\\"extra\\\":1}\"}";
        var invalidTarget = "{\"response\":\"{\\\"decision\\\":\\\"exclude\\\",\\\"eventKind\\\":\\\"unknown\\\",\\\"actor\\\":\\\"\\\",\\\"action\\\":\\\"\\\",\\\"targetId\\\":\\\"MARKET\\\",\\\"targetKind\\\":\\\"market\\\",\\\"evidence\\\":\\\"Fed raises rates\\\",\\\"reason\\\":\\\"x\\\"}\"}";

        Assert.Null(await Build(unknown).AdjudicateAsync(Item("Fed raises rates"), CancellationToken.None));
        Assert.Null(await Build(invalidTarget).AdjudicateAsync(Item("Fed raises rates"), CancellationToken.None));
    }

    [Fact]
    public async Task prompt는_본문전체를_보내지_않고_제목과_bounded_evidence만_보낸다()
    {
        var handler = new CountingHandler("{\"response\":\"not-json\"}");
        var adjudicator = new OllamaNewsRelevanceAdjudicator(
            new NewsOptions { SbhRelevanceAdjudicationEnabled = true }, new HttpClient(handler));
        var item = Item("Fed discusses policy") with { Content = "FULL_ARTICLE_SECRET",
            Relevance = Item("x").Relevance! with { EvidenceSpan = new string('E', 400) } };

        await adjudicator.AdjudicateAsync(item, CancellationToken.None);

        Assert.DoesNotContain("FULL_ARTICLE_SECRET", handler.LastBody);
        Assert.Contains(new string('E', 320), handler.LastBody);
        Assert.DoesNotContain(new string('E', 321), handler.LastBody);
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
        public string LastBody { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
