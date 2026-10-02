using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain.News;
using Xunit;

namespace Astra.Server.Tests;

public sealed class NewsEvidenceQueryTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);

    static NewsRecord Record(string id, string sentiment, int strength, DateTimeOffset at,
        IReadOnlyList<string> symbols)
        => new(id, "제목 " + id, "Reuters", at, [], [], symbols, sentiment, strength,
            "이유", "qwen", 10, at);

    static JsonElement Json(object value)
        => JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .RootElement.Clone();

    [Fact]
    public void opaque_id_상세가_대표기사의_실제분류입력을_따른다()
    {
        var options = new NewsOptions { Enabled = true };
        var state = new NewsRuntimeState();
        var representative = Record("provider:root/1", NewsSentiments.Positive, 4, Now, ["AAPL"]) with
        {
            ClassificationText = "대표 실제 입력",
            ClassificationSource = "detail_body"
        };
        var followerId = "https://news.example/item/a/b?x=1";
        var follower = Record(followerId, NewsSentiments.Positive, 4, Now.AddMinutes(-1), ["AAPL"]) with
        {
            ClassifiedFrom = representative.Id,
            EvidenceArticleId = representative.Id,
            ClassificationSource = "representative"
        };
        state.Add(representative, options.RecentCapacity);
        state.Add(follower, options.RecentCapacity);
        var query = new NewsQueryService(options, state, new NewsClock(Now));

        var detail = Json(query.DetailByQuery(followerId, "AAPL")!);

        Assert.Equal(followerId, detail.GetProperty("id").GetString());
        Assert.Equal("대표 실제 입력", detail.GetProperty("classificationText").GetString());
        Assert.False(detail.TryGetProperty("classificationTextKo", out _));
        Assert.Equal(representative.Id, detail.GetProperty("evidenceArticleId").GetString());
        Assert.Equal("AAPL", detail.GetProperty("evidenceSymbol").GetString());
    }

    [Fact]
    public void sentiment_snapshot의_top5와_나머지가_score와_전체weight를_재현한다()
    {
        var options = new NewsOptions { Enabled = true, HalfLifeMinutes = 30 };
        var state = new NewsRuntimeState();
        for (var index = 0; index < 7; index++)
        {
            var sentiment = index % 3 == 0 ? NewsSentiments.Negative : NewsSentiments.Positive;
            state.Add(Record(index.ToString(), sentiment, index % 5 + 1, Now.AddMinutes(-index * 4), ["AAPL"]),
                options.RecentCapacity);
        }
        var query = new NewsQueryService(options, state, new NewsClock(Now));

        var root = Json(query.Sentiment());
        var score = Assert.Single(root.GetProperty("symbols").EnumerateArray());
        var top = score.GetProperty("evidence").EnumerateArray().ToArray();
        var contribution = top.Sum(x => x.GetProperty("contribution").GetDouble())
            + score.GetProperty("remainingContribution").GetDouble();
        var weight = top.Sum(x => x.GetProperty("weight").GetDouble())
            + score.GetProperty("remainingWeight").GetDouble();

        Assert.Equal(5, top.Length);
        Assert.Equal(2, score.GetProperty("remainingEvidenceCount").GetInt32());
        Assert.Equal(score.GetProperty("score").GetDouble(), contribution, 5);
        Assert.Equal(score.GetProperty("totalWeight").GetDouble(), weight, 5);
        Assert.Equal(root.GetProperty("snapshotId").GetString(), score.GetProperty("snapshotId").GetString());
        Assert.Equal(root.GetProperty("asOf").GetDateTimeOffset(), score.GetProperty("asOf").GetDateTimeOffset());
    }

    [Fact]
    public void 요청한_종목의_근거만_상세snapshot에_포함한다()
    {
        var options = new NewsOptions { Enabled = true };
        var state = new NewsRuntimeState();
        state.Add(Record("both", NewsSentiments.Positive, 4, Now, ["AAPL", "MSFT"]), options.RecentCapacity);
        state.Add(Record("aapl", NewsSentiments.Negative, 2, Now, ["AAPL"]), options.RecentCapacity);
        state.Add(Record("msft", NewsSentiments.Negative, 5, Now, ["MSFT"]), options.RecentCapacity);
        var query = new NewsQueryService(options, state, new NewsClock(Now));

        var detail = Json(query.Detail("both", "AAPL")!);
        var ids = detail.GetProperty("evidence").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).OfType<string>().ToArray();

        Assert.Equal(["aapl", "both"], ids.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("msft", ids);
    }

    [Fact]
    public void 공급자별_health는_최근성공과_신규기사시각을_실패뒤에도_보존한다()
    {
        var state = new NewsRuntimeState();
        var first = Now.AddMinutes(-1);
        state.CollectionCompleted(first, "ok", true, 2, Now,
            [new NewsProviderFetchStatus("google-rss", "ok", 4, 2)]);
        state.CollectionCompleted(Now, "quota_wait", true, 0, null,
            [new NewsProviderFetchStatus("google-rss", "quota_wait", 0)]);
        state.CollectionCompleted(Now.AddMinutes(1), "quota_wait", false, 0, null, []);

        var provider = Assert.Single(state.Providers);
        Assert.Equal("quota_wait", provider.Status);
        Assert.Equal(Now, provider.LastAttemptAt);
        Assert.Equal(first, provider.LastSuccessAt);
        Assert.Equal(first, provider.LastNewArticleAt);
    }
}
