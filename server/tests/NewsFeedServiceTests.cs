using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.News;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>뉴스 증분 수집·우선순위 큐·분류 파이프라인 (#151)</summary>
public sealed class NewsFeedServiceTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    sealed class Harness
    {
        public NewsOptions Options { get; } = new() { Enabled = true, MaxPages = 3, MaxFeedRequestsPerMinute = 3 };
        public FakeNewsFeed Feed { get; } = new();
        public FakeNewsClassifier Classifier { get; } = new();
        public MemoryNewsStore Store { get; } = new();
        public NewsLocalStore Local { get; }
        public NewsRuntimeState State { get; } = new();
        public NewsClock Clock { get; } = new(Start);
        public NewsDiagnostics Diagnostics { get; } = new();
        public NewsFeedService Service { get; }

        public Harness(params WatchItem[] watchlist)
        {
            Local = new NewsLocalStore(watchlist);
            Service = NewsBuilder.Service(Options, Feed, Classifier, Store, Local, State, Clock, Diagnostics);
        }

        public Task PollAsync() => Service.PollAsync(CancellationToken.None);

        public Task PollAsync(CancellationToken ct) => Service.PollAsync(ct);

        public void Page(int page, params NewsFeedItem[] items) => Feed.Pages[page] = items.ToList();

        public IReadOnlyList<NewsRecord> Saved() => Store.Files.TryGetValue("2026-09-12.jsonl", out var lines)
            ? lines.Select(x => JsonSerializer.Deserialize<NewsRecord>(x, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray()
            : [];
    }

    static NewsFeedItem Item(string id, string title, params string[] tickers)
        => NewsBuilder.Item(id, title, Start, tickers);

    static NewsFeedItem Grouped(string id, string title, DateTimeOffset at, string groupId, params string[] tickers)
        => NewsBuilder.Grouped(id, title, at, groupId, tickers);

    [Fact]
    public async Task FirstPollStoresBaselineWithoutClassifying()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "가"), Item("99", "나"));

        await harness.PollAsync();

        Assert.Empty(harness.Classifier.Requests);
        var state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(100, state.LastId);
    }

    [Fact]
    public async Task SecondPollClassifiesOnlyNewerArticles()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "가"));
        await harness.PollAsync();

        harness.Page(1, Item("102", "새 기사"), Item("100", "가"));
        await harness.PollAsync();

        var request = Assert.Single(harness.Classifier.Requests);
        Assert.Equal("새 기사", request.Title);
    }

    [Fact]
    public async Task GuidFeedUsesCreatedAtCursorAcrossPolls()
    {
        var harness = new Harness();
        var first = NewsBuilder.Item("uuid-1", "기준", Start, []);
        harness.Page(1, first);
        await harness.PollAsync();

        var state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(0, state.LastId);
        Assert.Equal(Start, state.LastCreatedAt);

        harness.Page(1, NewsBuilder.Item("uuid-2", "새 기사", Start.AddMinutes(1), []), first);
        await harness.PollAsync();

        Assert.Equal("새 기사", Assert.Single(harness.Classifier.Requests).Title);
        state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(Start.AddMinutes(1), state.LastCreatedAt);
    }

    [Fact]
    public async Task FeedRequestMinuteLimitResetsAfterTimeAdvances()
    {
        var harness = new Harness();
        harness.Options.MaxFeedRequestsPerMinute = 2;
        harness.Options.MaxDailyFeedRequests = 10;
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();
        harness.Page(1, Item("101", "첫 기사"), Item("100", "기준"));
        await harness.PollAsync();
        harness.Page(1, Item("102", "차단 기사"), Item("101", "첫 기사"));
        await harness.PollAsync();
        Assert.Equal(2, harness.Feed.ListCalls.Count);
        harness.Clock.Now = harness.Clock.Now.AddMinutes(1).AddSeconds(1);
        await harness.PollAsync();
        Assert.Equal(3, harness.Feed.ListCalls.Count);
    }

    [Fact]
    public async Task FeedRequestDailyLimitSurvivesServiceRestart()
    {
        var harness = new Harness();
        harness.Options.MaxFeedRequestsPerMinute = 10;
        harness.Options.MaxDailyFeedRequests = 2;
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();
        harness.Page(1, Item("101", "새 기사"), Item("100", "기준"));
        await harness.PollAsync();
        harness.Clock.Now = harness.Clock.Now.AddMinutes(2);

        var restarted = new Harness();
        restarted.Options.MaxFeedRequestsPerMinute = 10;
        restarted.Options.MaxDailyFeedRequests = 2;
        restarted.Store.Texts[NewsFeedService.StateFile] = harness.Store.Texts[NewsFeedService.StateFile];
        restarted.Page(1, Item("102", "재기동 후 기사"), Item("101", "새 기사"));
        await restarted.PollAsync();

        Assert.Empty(restarted.Feed.ListCalls);
    }

    [Fact]
    public async Task AlreadyProcessedArticleIsNotClassifiedTwice()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "가"));
        await harness.PollAsync();
        harness.Page(1, Item("101", "새 기사"), Item("100", "가"));

        await harness.PollAsync();
        await harness.PollAsync();

        Assert.Single(harness.Classifier.Requests);
    }

    [Fact]
    public async Task PageExpandsWhenEveryArticleOnThePageIsNew()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("103", "가"), Item("102", "나"));
        harness.Page(2, Item("101", "다"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal([1, 1, 2], harness.Feed.ListCalls);
        Assert.Equal(3, harness.Classifier.Requests.Count);
    }

    [Fact]
    public async Task PageDoesNotExpandWhenPageAlreadyContainsKnownArticles()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal([1, 1], harness.Feed.ListCalls);
    }

    [Fact]
    public async Task DisabledServiceMakesNoCallAtAll()
    {
        var harness = new Harness();
        harness.Options.Enabled = false;
        harness.Page(1, Item("100", "가"));

        await harness.PollAsync();

        Assert.Empty(harness.Feed.ListCalls);
        Assert.Empty(harness.Classifier.Requests);
        Assert.Empty(harness.Store.Texts);
    }

    [Fact]
    public async Task UnmatchedArticlesAreClassifiedToo()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"));
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "우크라이나 정유시설 타격"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Single(harness.Classifier.Requests);
        Assert.Empty(Assert.Single(harness.Saved()).MatchedSymbols);
    }

    [Fact]
    public async Task MatchedArticlesAreClassifiedBeforeUnmatchedOnes()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"));
        harness.Options.MaxClassificationsPerMinute = 1;
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("102", "$NVDA 신고가"), Item("101", "유가 급등"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal("$NVDA 신고가", Assert.Single(harness.Classifier.Requests).Title);
    }

    [Fact]
    public async Task QueueOverflowDropsUnmatchedArticlesFirst()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"));
        harness.Options.MaxQueue = 2;
        harness.Options.MaxClassificationsPerMinute = 1;
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("103", "유가 하락"), Item("102", "$NVDA 신고가"), Item("101", "곡물 가격"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal(1, harness.State.Dropped);
        Assert.Equal("$NVDA 신고가", Assert.Single(harness.Classifier.Requests).Title);
    }

    [Fact]
    public async Task ClassificationStopsAtThePerMinuteLimitAndResumesNextMinute()
    {
        var harness = new Harness();
        harness.Options.MaxClassificationsPerMinute = 2;
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("104", "가"), Item("103", "나"), Item("102", "다"), Item("101", "라"), Item("100", "기준"));
        await harness.PollAsync();
        Assert.Equal(2, harness.Classifier.Requests.Count);
        Assert.Equal(2, harness.State.Queue);

        harness.Clock.Now = Start.AddSeconds(61);
        await harness.PollAsync();

        Assert.Equal(4, harness.Classifier.Requests.Count);
        Assert.Equal(0, harness.State.Queue);
    }

    [Fact]
    public async Task DetailBodyIsFetchedOnlyForMatchedArticlesWithinBudget()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"), new WatchItem("F", "Ford Motor"), new WatchItem("INTC", "Intel"));
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Feed.Details["101"] = new NewsDetail("", "엔비디아 본문");
        harness.Feed.Details["102"] = new NewsDetail("", "포드 본문");
        harness.Feed.Details["103"] = new NewsDetail("", "인텔 본문");
        harness.Page(1, Item("104", "유가 급등"), Item("103", "$INTC 감산"), Item("102", "$F 리콜"), Item("101", "$NVDA 신고가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal(["101", "102"], harness.Feed.DetailCalls);
        Assert.Equal("엔비디아 본문", harness.Classifier.Requests[0].Body);
        Assert.Equal("목록 요약", harness.Classifier.Requests[2].Body);
    }

    [Fact]
    public async Task AiSummaryIsPreferredOverTheDetailBody()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"));
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Feed.Details["101"] = new NewsDetail("- 요약 첫 줄\n- 요약 둘째 줄", "긴 본문");
        harness.Page(1, Item("101", "$NVDA 신고가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal("- 요약 첫 줄\n- 요약 둘째 줄", Assert.Single(harness.Classifier.Requests).Body);
        Assert.Equal(NewsInputKinds.Summary, Assert.Single(harness.Saved()).InputKind);
    }

    [Fact]
    public async Task EmptyAiSummaryFallsBackToTheDetailBody()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"));
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Feed.Details["101"] = new NewsDetail("", "긴 본문");
        harness.Page(1, Item("101", "$NVDA 신고가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal("긴 본문", Assert.Single(harness.Classifier.Requests).Body);
        Assert.Equal(NewsInputKinds.Body, Assert.Single(harness.Saved()).InputKind);
    }

    [Fact]
    public async Task HeadlineOnlyArticleIsClassifiedFromTheTitleAndAiHeadline()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1,
            new NewsFeedItem("101", "우크라이나, 정유시설 타격", "", "financial-juice", Start, [], "AI 헤드라인", true),
            Item("100", "기준"));
        await harness.PollAsync();

        var request = Assert.Single(harness.Classifier.Requests);
        Assert.Equal("우크라이나, 정유시설 타격\nAI 헤드라인", request.Title);
        Assert.Equal("", request.Body);
        Assert.Equal(NewsInputKinds.Headline, Assert.Single(harness.Saved()).InputKind);
    }

    [Fact]
    public async Task ClassifierTimeoutIsStoredAsUnclassified()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(null, "qwen", 20000, true);
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "가"), Item("100", "기준"));
        await harness.PollAsync();

        var saved = Assert.Single(harness.Saved());
        Assert.Equal(NewsSentiments.Unclassified, saved.Sentiment);
        Assert.Equal(0, saved.Strength);
        Assert.Equal([NewsSymbols.Market], saved.Symbols);
    }

    [Fact]
    public async Task OllamaDownKeepsTheArticleQueuedAndReportsDown()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(null, "qwen", 0, false);
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.False(harness.State.OllamaOk);
        Assert.Equal(1, harness.State.Queue);
        Assert.Empty(harness.Saved());
        Assert.Empty(harness.Diagnostics.Failures);

        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["NVDA"], NewsSentiments.Positive, 3, "회복"), "qwen", 10, true);
        harness.Clock.Now = Start.AddSeconds(61);
        await harness.PollAsync();

        Assert.True(harness.State.OllamaOk);
        Assert.Single(harness.Saved());
    }

    [Fact]
    public async Task FeedTickerTagsWinOverClassifierSymbols()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["TSLA"], NewsSentiments.Negative, 2, "추정"), "qwen", 10, true);
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "포드 리콜", "f"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal(["F"], Assert.Single(harness.Saved()).Symbols);
    }

    [Fact]
    public async Task DailyByteCapStopsWritingWithoutLosingTheClassification()
    {
        var harness = new Harness();
        harness.Options.MaxDailyBytes = 10;
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Empty(harness.Saved());
        Assert.True(harness.State.StorageLimited);
        Assert.Single(harness.State.Recent());
    }

    [Fact]
    public async Task FeedFailureIsReportedAndPersistsConsumedRequest()
    {
        var harness = new Harness();
        harness.Feed.ListError = new HttpRequestException("429");

        await harness.PollAsync();

        Assert.Equal("news-feed", Assert.Single(harness.Diagnostics.Failures).Scope);
        var state = Assert.Single(harness.Store.Texts);
        Assert.Contains("feedRequestTimes", state.Value);
        Assert.NotNull(harness.State.LastPollAt);
    }

    [Fact]
    public async Task NonCancelledFeedTimeoutIsReportedWithoutStoppingPolling()
    {
        var harness = new Harness();
        harness.Feed.ListError = new OperationCanceledException("timeout");

        await harness.PollAsync();

        Assert.Equal("news-feed", Assert.Single(harness.Diagnostics.Failures).Scope);
        Assert.Equal("timeout", harness.State.LastError);
        Assert.Null(harness.State.LastSuccessAt);
    }

    [Fact]
    public async Task HostCancellationPropagatesWithoutRecordingFeedFailure()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.PollAsync(cts.Token));

        Assert.Empty(harness.Diagnostics.Failures);
        Assert.Null(harness.State.LastError);
    }

    [Fact]
    public async Task RestoreReadsTodayFileSoQueriesSurviveRestart()
    {
        var harness = new Harness();
        var record = new NewsRecord("90", "지난 기사", "reuters", Start, [], [], ["NVDA"],
            NewsSentiments.Positive, 3, "이유", "qwen", 10, Start);
        harness.Store.Files["2026-09-12.jsonl"] =
            [JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))];
        harness.Page(1, Item("100", "기준"));

        await harness.PollAsync();

        Assert.Equal("90", Assert.Single(harness.State.Recent()).Id);
    }

    [Fact]
    public async Task SavedRecordCarriesThePromptVersion()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["NVDA"], NewsSentiments.Positive, 3, "회복"), "qwen", 10, true, "v2b");
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal("v2b", Assert.Single(harness.Saved()).PromptVersion);
    }

    [Fact]
    public async Task OnlyTheNewestGroupMemberIsClassified()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1,
            Grouped("103", "속보 3보", Start.AddMinutes(3), "grp"),
            Grouped("102", "속보 2보", Start.AddMinutes(2), "grp"),
            Grouped("101", "속보 1보", Start.AddMinutes(1), "grp"),
            Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal("속보 3보", Assert.Single(harness.Classifier.Requests).Title);
        Assert.Equal(3, harness.Saved().Count);
    }

    [Fact]
    public async Task GroupFollowersCopyTheRepresentativeClassificationWithZeroLatency()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Negative, 3, "경보 발령"), "qwen", 850, true, "v2b");
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1,
            Grouped("102", "리야드 조기 경보 발령 2보", Start.AddMinutes(2), "grp"),
            Grouped("101", "리야드 조기 경보 발령", Start.AddMinutes(1), "grp"),
            Item("100", "기준"));
        await harness.PollAsync();

        var saved = harness.Saved().ToDictionary(x => x.Id);
        Assert.Null(saved["102"].ClassifiedFrom);
        Assert.Equal("102", saved["101"].ClassifiedFrom);
        Assert.Equal(NewsSentiments.Negative, saved["101"].Sentiment);
        Assert.Equal(3, saved["101"].Strength);
        Assert.Equal(0, saved["101"].LatencyMs);
    }

    [Fact]
    public async Task GroupFollowerStillGetsItsOwnFeedTagsForced()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Negative, 3, "경보 발령"), "qwen", 10, true, "v2b");
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1,
            Grouped("102", "리야드 조기 경보 발령 2보", Start.AddMinutes(2), "grp"),
            Grouped("101", "포드 리콜 속보", Start.AddMinutes(1), "grp", "f"),
            Item("100", "기준"));
        await harness.PollAsync();

        var saved = harness.Saved().ToDictionary(x => x.Id);
        Assert.Equal(["F"], saved["101"].Symbols);
    }

    [Fact]
    public async Task UngroupedArticlesWithoutAGroupIdAreClassifiedIndividually()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("102", "가"), Item("101", "나"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal(2, harness.Classifier.Requests.Count);
        Assert.All(harness.Saved(), x => Assert.Null(x.ClassifiedFrom));
    }
}
