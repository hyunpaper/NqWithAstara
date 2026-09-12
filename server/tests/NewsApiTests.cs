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
        Assert.NotNull(services.GetRequiredService<NewsFeedService>());
        Assert.NotNull(services.GetRequiredService<NewsQueryService>());
    }

    [Fact]
    public void NewsHostedServiceIsRegisteredAndDisabledByDefault()
    {
        Assert.Contains(host.Factory.Services.GetServices<IHostedService>(), s => s is NewsService);
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
}
