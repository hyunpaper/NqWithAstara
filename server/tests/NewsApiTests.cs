using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.News;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>뉴스 조회·health 응답 계약 (#151)</summary>
public sealed class NewsQueryServiceTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    static (NewsQueryService Query, NewsRuntimeState State, NewsOptions Options) Build()
    {
        var options = new NewsOptions { Enabled = true, HalfLifeMinutes = 30 };
        var state = new NewsRuntimeState();
        return (new NewsQueryService(options, state, new NewsClock(Now)), state, options);
    }

    static NewsRecord Record(string id, string sentiment, int strength, DateTimeOffset at, params string[] symbols)
        => new(id, "제목 " + id, "reuters", at, [], [], symbols, sentiment, strength, "이유", "qwen", 12, at);

    static JsonElement Serialize(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    [Fact]
    public void ArticlesReturnCamelCaseFieldsNewestFirst()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Positive, 3, Now.AddMinutes(-10), "NVDA"), options.RecentCapacity);
        state.Add(Record("2", NewsSentiments.Negative, 2, Now, "F"), options.RecentCapacity);

        var json = Serialize(query.Articles(null, null));

        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        var first = json.GetProperty("articles")[0];
        Assert.Equal("2", first.GetProperty("id").GetString());
        foreach (var name in new[] { "title", "source", "createdAt", "tickers", "matchedSymbols", "symbols", "sentiment", "strength", "reason", "model", "latencyMs", "classifiedAt" })
            Assert.True(first.TryGetProperty(name, out _), name);
    }

    [Fact]
    public void ArticlesFilterBySymbolIgnoringCaseAndDollarPrefix()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Positive, 3, Now, "NVDA"), options.RecentCapacity);
        state.Add(Record("2", NewsSentiments.Negative, 2, Now, "F"), options.RecentCapacity);

        var json = Serialize(query.Articles("$nvda", null));

        Assert.Equal("1", Assert.Single(json.GetProperty("articles").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public void ArticlesRespectTheLimit()
    {
        var (query, state, options) = Build();
        for (var i = 0; i < 5; i++) state.Add(Record(i.ToString(), NewsSentiments.Neutral, 1, Now, "F"), options.RecentCapacity);

        Assert.Equal(2, Serialize(query.Articles(null, 2)).GetProperty("count").GetInt32());
    }

    [Fact]
    public void SentimentSeparatesTheMarketPseudoSymbol()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Positive, 4, Now, "NVDA"), options.RecentCapacity);
        state.Add(Record("2", NewsSentiments.Negative, 3, Now, NewsSymbols.Market), options.RecentCapacity);

        var json = Serialize(query.Sentiment());

        Assert.Equal(-3, json.GetProperty("market").GetProperty("score").GetDouble(), 3);
        var symbol = Assert.Single(json.GetProperty("symbols").EnumerateArray());
        Assert.Equal("NVDA", symbol.GetProperty("symbol").GetString());
        Assert.Equal(4, symbol.GetProperty("score").GetDouble(), 3);
    }

    [Fact]
    public void SentimentIgnoresUnclassifiedArticles()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Unclassified, 0, Now, "NVDA"), options.RecentCapacity);

        var json = Serialize(query.Sentiment());

        Assert.Equal(JsonValueKind.Null, json.GetProperty("market").ValueKind);
        Assert.Empty(json.GetProperty("symbols").EnumerateArray());
    }

    [Fact]
    public void SentimentExposesWeightAlongsideCount()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Positive, 3, Now, "NVDA"), options.RecentCapacity);
        state.Add(Record("2", NewsSentiments.Negative, 2, Now.AddMinutes(-30), "NVDA"), options.RecentCapacity);

        var symbol = Assert.Single(Serialize(query.Sentiment()).GetProperty("symbols").EnumerateArray());

        Assert.Equal(2, symbol.GetProperty("count").GetInt32());
        Assert.Equal(1.5, symbol.GetProperty("weight").GetDouble(), 2);
    }

    [Fact]
    public void SentimentIncludesSymbolsOutsideTheWatchlist()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Positive, 2, Now, "RGTI"), options.RecentCapacity);

        Assert.Equal("RGTI", Assert.Single(Serialize(query.Sentiment()).GetProperty("symbols").EnumerateArray())
            .GetProperty("symbol").GetString());
    }

    [Fact]
    public void HealthExposesQueueDropAndOllamaState()
    {
        var (query, state, _) = Build();
        state.QueueDepth(7);
        state.Drop(3);
        state.Ollama(false);
        state.PollCompleted(Now);

        var json = Serialize(query.Health());

        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, json.GetProperty("queue").GetInt32());
        Assert.Equal(3, json.GetProperty("dropped").GetInt64());
        Assert.Equal("down", json.GetProperty("ollama").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("lastPollAt").ValueKind);
    }

    [Fact]
    public void HealthExposesThePromptVersion()
    {
        var (query, _, _) = Build();

        var json = Serialize(query.Health());

        Assert.Equal("v2d", json.GetProperty("promptVersion").GetString());
    }

    [Fact]
    public void Detail은_ScoreCore_필드를_노출하지_않고_관련성_판정을_그대로_반환한다()
    {
        var (query, state, options) = Build();
        var relevance = new NewsRelevanceAssessment("sbh-relevance-v2+entities:abcdef12", NewsRelevanceDecisions.Include,
            "company_contract", "Dell", "signs", [new("DELL", "company", "direct", "entity:Dell")],
            "Dell signs contract", "company_event_confirmed");
        state.Add(Record("1", NewsSentiments.Positive, 3, Now, "DELL") with { Relevance = relevance }, options.RecentCapacity);

        var json = Serialize(query.Detail("1")!);

        Assert.False(json.TryGetProperty("scoreCore", out _));
        var payload = json.GetProperty("relevance");
        Assert.Equal("sbh-relevance-v2+entities:abcdef12", payload.GetProperty("policyVersion").GetString());
        Assert.Equal("include", payload.GetProperty("decision").GetString());
        Assert.Equal("DELL", payload.GetProperty("targets")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Health는_관련성_필터_버전과_누적건수와_최근제외_표본을_노출한다()
    {
        var (query, state, _) = Build();
        var version = "sbh-relevance-v2+entities:abcdef12";
        NewsRelevanceAssessment Assessment(string decision, string reason)
            => new(version, decision, "unknown", "", "", [], "", reason);
        state.RelevanceObserved(Assessment(NewsRelevanceDecisions.Include, "macro_event_confirmed"), "포함", Now);
        state.RelevanceObserved(Assessment(NewsRelevanceDecisions.Review, "insufficient_actor_action_target_context"), "보류", Now);
        for (var i = 0; i < 6; i++)
            state.RelevanceObserved(Assessment(NewsRelevanceDecisions.Exclude, "non_market_context"), "제외 " + i, Now.AddSeconds(i));
        state.RelevanceUnadjudicated("보류", NewsRelevanceReasons.ReviewUnadjudicatedTimeout, Now.AddMinutes(1));

        var filter = Serialize(query.Health()).GetProperty("relevanceFilter");

        Assert.Equal(version, filter.GetProperty("policyVersion").GetString());
        Assert.Equal(NewsRelevanceLexicon.Version, filter.GetProperty("lexiconVersion").GetString());
        Assert.Equal(1, filter.GetProperty("included").GetInt64());
        Assert.Equal(6, filter.GetProperty("excluded").GetInt64());
        Assert.Equal(1, filter.GetProperty("review").GetInt64());
        Assert.Equal(1, filter.GetProperty("reviewUnadjudicated").GetInt64());
        var recent = filter.GetProperty("recentExcluded");
        Assert.Equal(5, recent.GetArrayLength());
        Assert.Equal("보류", recent[0].GetProperty("title").GetString());
        Assert.Equal(NewsRelevanceReasons.ReviewUnadjudicatedTimeout, recent[0].GetProperty("reason").GetString());
    }

    [Fact]
    public void Health는_관측전에도_기본_정책과_사전_버전을_노출한다()
    {
        var (query, _, _) = Build();

        var filter = Serialize(query.Health()).GetProperty("relevanceFilter");

        Assert.Equal(NewsRelevancePolicy.CurrentVersion, filter.GetProperty("policyVersion").GetString());
        Assert.Equal(0, filter.GetProperty("recentExcluded").GetArrayLength());
    }

    [Fact]
    public void HealthExposesSbhRelevanceFilterCountsAndPolicyVersion()
    {
        var (query, state, _) = Build();
        state.CollectionCompleted(Now, "ok", true, 2, Now,
            [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "ok", 7, 2,
                IncludedCount: 2, ExcludedCount: 4, ReviewCount: 1,
                FilterPolicyVersion: NewsRelevancePolicy.CurrentVersion)]);

        var provider = Serialize(query.Health()).GetProperty("providers")[0];

        Assert.Equal(2, provider.GetProperty("includedCount").GetInt32());
        Assert.Equal(4, provider.GetProperty("excludedCount").GetInt32());
        Assert.Equal(1, provider.GetProperty("reviewCount").GetInt32());
        Assert.Equal(NewsRelevancePolicy.CurrentVersion, provider.GetProperty("filterPolicyVersion").GetString());
    }

    [Fact]
    public void HealthPreservesLastSbhFilterSnapshotAfterNotModified()
    {
        var state = new NewsRuntimeState();
        var now = DateTimeOffset.UtcNow;
        state.CollectionCompleted(now, "ok", true, 2, now,
            [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "ok", 7, 2,
                IncludedCount: 2, ExcludedCount: 4, ReviewCount: 1,
                FilterPolicyVersion: NewsRelevancePolicy.CurrentVersion)]);
        state.CollectionCompleted(now.AddMinutes(15), "empty", true, 0, null,
            [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "empty", 0)]);

        var provider = Assert.Single(state.Providers);
        Assert.Equal(2, provider.IncludedCount);
        Assert.Equal(4, provider.ExcludedCount);
        Assert.Equal(1, provider.ReviewCount);
        Assert.Equal(NewsRelevancePolicy.CurrentVersion, provider.FilterPolicyVersion);
    }

    [Fact]
    public void ArticlesExposeThePromptVersion()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Positive, 3, Now, "NVDA") with { PromptVersion = "v2c" },
            options.RecentCapacity);

        var first = Serialize(query.Articles(null, null)).GetProperty("articles")[0];

        Assert.Equal("v2c", first.GetProperty("promptVersion").GetString());
    }

    [Fact]
    public void ArticlesExposeClassifiedFrom()
    {
        var (query, state, options) = Build();
        state.Add(Record("1", NewsSentiments.Negative, 3, Now, "MARKET") with { ClassifiedFrom = "2" },
            options.RecentCapacity);

        var first = Serialize(query.Articles(null, null)).GetProperty("articles")[0];

        Assert.Equal("2", first.GetProperty("classifiedFrom").GetString());
    }

    [Fact]
    public void Fox_RSS_설정은_상태에_선택된_공급자를_노출한다()
    {
        var (query, _, options) = Build();
        Assert.False(options.UseFoxNewsRss);
        Assert.Equal("rss", Serialize(query.Health()).GetProperty("feed").GetString());
        options.UseSaveTicker = true;
        options.UseFoxNewsRss = true;

        var health = Serialize(query.Health());

        Assert.Equal(NewsFeedProviders.FoxNewsRss, health.GetProperty("feed").GetString());
    }

    [Fact]
    public void SBHNews_설정은_다른_뉴스_공급자보다_우선한다()
    {
        var (query, _, options) = Build();
        options.UseSaveTicker = true;
        options.UseFoxNewsRss = true;
        options.UseSbhNews = true;

        Assert.Equal(NewsFeedProviders.SbhNews, Serialize(query.Health()).GetProperty("feed").GetString());
    }

    static NewsRelevanceAssessment Decided(string decision, string reason = "test")
        => new(NewsRelevancePolicy.CurrentVersion, decision, "test", "", "", [], "", reason);

    static NewsRecord Legacy(string id, string title, string url, string sentiment, int strength)
        => new(id, title, "Fox News", Now, [], [], ["MARKET"], sentiment, strength, "이유", "qwen", 12, Now, Url: url);

    [Fact]
    public void 관련성_exclude_기사는_목록과_감성점수에서_빠진다()
    {
        var (query, state, options) = Build();
        state.Add(Record("in", NewsSentiments.Negative, 3, Now, "MARKET") with { Relevance = Decided(NewsRelevanceDecisions.Include) }, options.RecentCapacity);
        state.Add(Record("out", NewsSentiments.Positive, 5, Now, "MARKET") with { Relevance = Decided(NewsRelevanceDecisions.Exclude, "non_market_section") }, options.RecentCapacity);
        state.Add(Record("llm", NewsSentiments.Neutral, 0, Now, "MARKET") with { Relevance = Decided(NewsRelevanceDecisions.Exclude, "llm_irrelevant") }, options.RecentCapacity);

        var articles = Serialize(query.Articles(null, null)).GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        var market = Serialize(query.Sentiment()).GetProperty("market");

        Assert.Equal(["in"], articles);
        Assert.Equal(1, market.GetProperty("count").GetInt32());
        Assert.Equal(-3, market.GetProperty("score").GetDouble(), 3);
    }

    [Fact]
    public void 판정이_없는_저장기사는_조회시점에_정책으로_걸러낸다()
    {
        var options = new NewsOptions { Enabled = true, HalfLifeMinutes = 30 };
        var state = new NewsRuntimeState();
        var query = new NewsQueryService(options, state, new NewsClock(Now), null, new NewsRelevancePolicy());
        state.Add(Legacy("sweeney", "Jealous media attack Sydney Sweeney's sports ad while her $2B valuation proves she's the ultimate boss",
            "https://www.foxnews.com/outkick-sports/jealous-columnists-attack-sydney-sweeney-sports-ad", NewsSentiments.Positive, 3), options.RecentCapacity);
        state.Add(Legacy("fed", "Fed raises interest rates by a quarter point as inflation stays high",
            "https://www.foxnews.com/politics/fed-raises-rates", NewsSentiments.Negative, 3), options.RecentCapacity);

        var articles = Serialize(query.Articles(null, null)).GetProperty("articles").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        var market = Serialize(query.Sentiment()).GetProperty("market");

        Assert.Equal(["fed"], articles);
        Assert.Equal(1, market.GetProperty("count").GetInt32());
        Assert.Equal(-3, market.GetProperty("score").GetDouble(), 3);
        Assert.NotNull(query.Detail("sweeney"));
    }

}

public sealed class NewsHostContractTests(AstraHostFixture host) : IClassFixture<AstraHostFixture>
{
    [Fact]
    public void NewsServicesResolveFromTheRealHost()
    {
        var services = host.Factory.Services;

        Assert.NotNull(services.GetRequiredService<NewsOptions>());
        Assert.NotNull(services.GetRequiredService<INewsFeed>());
        Assert.NotNull(services.GetRequiredService<INewsClassifier>());
        Assert.NotNull(services.GetRequiredService<INewsStore>());
        Assert.NotNull(services.GetRequiredService<NewsRuntimeState>());
        Assert.NotNull(services.GetRequiredService<NewsTranslationQueue>());
        Assert.NotNull(services.GetRequiredService<NewsFeedService>());
        Assert.NotNull(services.GetRequiredService<NewsQueryService>());
    }

    [Fact]
    public void NewsHostedServiceIsRegisteredAndDisabledByDefault()
    {
        Assert.Contains(host.Factory.Services.GetServices<IHostedService>(), s => s is NewsService);
        Assert.Contains(host.Factory.Services.GetServices<IHostedService>(), s => s is NewsTranslationService);
        Assert.False(host.Factory.Services.GetRequiredService<NewsOptions>().Enabled);
    }

    [Fact]
    public async Task HealthCarriesTheNewsSection()
    {
        using var client = host.Factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/api/health"));

        var news = json.RootElement.GetProperty("news");
        Assert.False(news.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, news.GetProperty("queue").GetInt32());
        Assert.Equal("ok", news.GetProperty("ollama").GetString());
        Assert.Equal(JsonValueKind.Null, news.GetProperty("lastPollAt").ValueKind);
        Assert.Equal("v2d", news.GetProperty("promptVersion").GetString());
    }

    [Fact]
    public async Task NewsEndpointsAnswerWhileTheFeatureIsDisabled()
    {
        using var client = host.Factory.CreateClient();

        using var articles = JsonDocument.Parse(await client.GetStringAsync("/api/news?symbol=NVDA&limit=5"));
        Assert.False(articles.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Empty(articles.RootElement.GetProperty("articles").EnumerateArray());

        using var sentiment = JsonDocument.Parse(await client.GetStringAsync("/api/news/sentiment"));
        Assert.Equal(30d, sentiment.RootElement.GetProperty("halfLifeMinutes").GetDouble());
        Assert.Empty(sentiment.RootElement.GetProperty("symbols").EnumerateArray());
    }

    [Fact]
    public async Task NewsDetailEndpointAcceptsOpaqueQueryIdsAndReturns404ForUnknownIds()
    {
        using var isolated = new AstraHostFixture();
        var id = "https://provider.example/news/a/b?x=1";
        var state = isolated.Factory.Services.GetRequiredService<NewsRuntimeState>();
        state.Add(new NewsRecord(id, "상세 기사", "Reuters", DateTimeOffset.UtcNow, [], [], ["AAPL"],
            NewsSentiments.Positive, 3, "이유", "qwen", 10, DateTimeOffset.UtcNow), 300);
        using var client = isolated.Factory.CreateClient();

        using var found = await client.GetAsync($"/api/news/detail?id={Uri.EscapeDataString(id)}&symbol=AAPL");
        using var missing = await client.GetAsync("/api/news/detail?id=missing-opaque-id");

        Assert.True(found.IsSuccessStatusCode);
        using var json = JsonDocument.Parse(await found.Content.ReadAsStringAsync());
        Assert.Equal(id, json.RootElement.GetProperty("id").GetString());
        Assert.Equal("AAPL", json.RootElement.GetProperty("evidenceSymbol").GetString());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 뉴스_정리_실행_API는_원격_요청을_거부한다()
    {
        using var client = host.Factory.CreateClient();

        using var preview = await client.PostAsync("/api/news/migration/preview", null);
        using var execute = await client.PostAsync("/api/news/migration/execute", null);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, execute.StatusCode);
    }
}
