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

        Assert.Equal("v2c", json.GetProperty("promptVersion").GetString());
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
        Assert.Equal("v2c", news.GetProperty("promptVersion").GetString());
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
