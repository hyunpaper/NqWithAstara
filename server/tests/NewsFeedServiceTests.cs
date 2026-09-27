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
        public NewsTranslationQueue? TranslationQueue { get; }
        public NewsFeedService Service { get; }

        public Harness(params WatchItem[] watchlist) : this(null, null, watchlist) { }

        public Harness(INewsTranslator? translator, params WatchItem[] watchlist) : this(translator, null, watchlist) { }

        public Harness(INewsRelevanceAdjudicator adjudicator, Action<NewsOptions> configure)
        {
            configure(Options);
            Local = new NewsLocalStore();
            Service = new NewsFeedService(Options, Feed, Classifier, Store, Local, State, Diagnostics, Clock,
                null, adjudicator);
        }

        public Harness(INewsTranslator? translator, INewsRelevanceAdjudicator? adjudicator, params WatchItem[] watchlist)
        {
            Local = new NewsLocalStore(watchlist);
            TranslationQueue = translator is null ? null
                : new NewsTranslationQueue(Options, translator, Store, State, Diagnostics, Clock);
            Service = new NewsFeedService(Options, Feed, Classifier, Store, Local, State, Diagnostics, Clock,
                TranslationQueue, adjudicator);
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
    public async Task 공급자_전환_정리는_이미_대기중인_레거시_분류도_폐기한다()
    {
        var harness = new Harness();
        harness.Options.MaxClassificationsPerMinute = 1;
        harness.Feed.Name = "legacy-feed";
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();
        harness.Clock.Now = Start.AddMinutes(1);
        harness.Page(1, new NewsFeedItem("102", "구형 기사 1", new string('x', 5000), "legacy-source",
            harness.Clock.GetUtcNow(), []), Item("101", "구형 기사 2"), Item("100", "기준"));
        await harness.PollAsync();
        Assert.Equal(1, harness.Service.QueueDepth);

        harness.Options.UseSbhNews = true;
        harness.State.CollectionCompleted(harness.Clock.GetUtcNow(), "ok", true, 1, null,
            [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "ok", 1)]);
        var oldBytes = await harness.Store.SizeAsync("2026-09-12.jsonl", CancellationToken.None);
        Assert.True(oldBytes > 1000, $"Expected legacy bytes before cleanup, got {oldBytes}; saved={harness.Saved().Count}");
        var migration = new NewsStorageMigrationService(harness.Options, harness.State, harness.Store,
            feedService: harness.Service);
        await migration.ExecuteAsync(await migration.PlanAsync(CancellationToken.None), CancellationToken.None);
        harness.Options.MaxDailyBytes = 1500;
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Clock.Now = Start.AddMinutes(20);
        harness.Page(1, new NewsFeedItem("103", "새 SBH 기사", "내용", NewsFeedProviders.SbhNewsSource,
            harness.Clock.GetUtcNow(), [], Provider: NewsFeedProviders.SbhNews));

        await harness.PollAsync();

        Assert.Equal(0, harness.Service.QueueDepth);
        Assert.Empty(harness.Diagnostics.Failures);
        Assert.Contains(harness.Classifier.Requests, x => x.Title == "새 SBH 기사");
        Assert.Equal("103", Assert.Single(harness.Saved()).Id);
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
    public async Task GuidFeedUsesIdTieBreakerWhenArticlesShareTimestamp()
    {
        var harness = new Harness();
        var first = NewsBuilder.Item("00000000-0000-0000-0000-000000000001", "기준", Start, []);
        harness.Page(1, first);
        await harness.PollAsync();

        var second = NewsBuilder.Item("00000000-0000-0000-0000-000000000002", "동일 시각 신규", Start, []);
        harness.Page(1, second, first);
        await harness.PollAsync();

        Assert.Equal("동일 시각 신규", Assert.Single(harness.Classifier.Requests).Title);
    }

    [Fact]
    public async Task LateArticleOlderThanWatermarkIsAcceptedOnceAndSurvivesRestart()
    {
        var harness = new Harness();
        var baseline = NewsBuilder.Item("100", "기준", Start, []);
        harness.Page(1, baseline);
        await harness.PollAsync();

        var latest = NewsBuilder.Item("300", "최신", Start.AddMinutes(3), []);
        harness.Page(1, latest, baseline);
        await harness.PollAsync();

        var late = NewsBuilder.Item("200", "지연 도착", Start.AddMinutes(1), []);
        harness.Page(1, late, latest, baseline);
        await harness.PollAsync();
        Assert.Contains(harness.Classifier.Requests, x => x.Title == "지연 도착");

        var restarted = new Harness();
        restarted.Store.Texts[NewsFeedService.StateFile] = harness.Store.Texts[NewsFeedService.StateFile];
        restarted.Page(1, late, latest, baseline);
        await restarted.PollAsync();
        Assert.Empty(restarted.Classifier.Requests);
    }

    [Fact]
    public async Task 공급자를_SBHNews로_바꾸면_첫_피드는_기준점만_저장하고_재기동뒤_신규만_분류한다()
    {
        var harness = new Harness();
        harness.Feed.Name = "fox-news-rss";
        harness.Page(1, Item("fox-1", "기존 기사") with { Provider = "fox-news-rss" });
        await harness.PollAsync();

        harness.Feed.Name = NewsFeedProviders.SbhNews;
        var sbhBaseline = Item("sbh-1", "SBH 기존 기사") with { Provider = NewsFeedProviders.SbhNews };
        harness.Page(1, sbhBaseline);
        await harness.PollAsync();

        Assert.Empty(harness.Classifier.Requests);
        var state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(NewsFeedProviders.SbhNews, state.BaselinedProviders!);

        var restarted = new Harness();
        restarted.Store.Texts[NewsFeedService.StateFile] = harness.Store.Texts[NewsFeedService.StateFile];
        restarted.Clock.Now = Start.AddMinutes(1);
        restarted.Feed.Name = NewsFeedProviders.SbhNews;
        restarted.Page(1, Item("sbh-2", "SBH 신규 기사") with { Provider = NewsFeedProviders.SbhNews }, sbhBaseline);
        await restarted.PollAsync();

        Assert.Equal("SBH 신규 기사", Assert.Single(restarted.Classifier.Requests).Title);
    }

    [Fact]
    public async Task 첫_실패_뒤_성공한_SBHNews_피드는_기준점만_저장한다()
    {
        var harness = new Harness();
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Feed.BatchStatus = "failed";
        harness.Feed.ProviderStatuses = [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "failed", 0)];
        await harness.PollAsync();

        var baseline = Item("sbh-1", "성공 기준") with { Provider = NewsFeedProviders.SbhNews };
        harness.Feed.BatchStatus = null;
        harness.Feed.ProviderStatuses = null;
        harness.Page(1, baseline);
        await harness.PollAsync();
        Assert.Empty(harness.Classifier.Requests);

        harness.Clock.Now = harness.Clock.Now.AddMinutes(1);
        harness.Page(1, Item("sbh-2", "신규") with { Provider = NewsFeedProviders.SbhNews }, baseline);
        await harness.PollAsync();

        Assert.Equal("신규", Assert.Single(harness.Classifier.Requests).Title);
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
    public async Task 공급자_Retry_After는_저장돼_다음_폴링_호출을_막는다()
    {
        var harness = new Harness();
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Feed.BatchStatus = "quota_wait";
        harness.Feed.ProviderStatuses = [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "quota_wait", 0, RetryAfter: TimeSpan.FromMinutes(20))];
        harness.Page(1, Item("sbh-1", "기준") with { Provider = NewsFeedProviders.SbhNews });
        await harness.PollAsync();
        harness.Clock.Now = harness.Clock.Now.AddMinutes(16);
        await harness.PollAsync();

        Assert.Single(harness.Feed.ListCalls);
        var state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(Start.AddMinutes(20), state.FeedRetryAfterUntil);
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
        Assert.Equal(NewsInputKinds.Summary, harness.Saved().Last().InputKind);
    }

    [Fact]
    public async Task 피드본문과_상세본문을_분류입력에_포함한다()
    {
        var harness = new Harness(new WatchItem("NVDA", "NVIDIA"));
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();
        harness.Page(1, new NewsFeedItem("101", "제목", "목록 요약", "Reuters", Start.AddMinutes(1), ["NVDA"], Content: "RSS 본문"), Item("100", "기준"));

        await harness.PollAsync();

        var body = Assert.Single(harness.Classifier.Requests).Body;
        Assert.Contains("RSS 본문", body);
        Assert.Contains("목록 요약", body);
        var saved = Assert.Single(harness.Saved());
        Assert.Equal("목록 요약", saved.Summary);
        Assert.Equal("RSS 본문", saved.Content);
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
        var saved = Assert.Single(harness.Saved());
        Assert.Equal(NewsInputKinds.Body, saved.InputKind);
        Assert.Equal("detail_body", saved.ClassificationSource);
        Assert.Contains("긴 본문", saved.ClassificationText);
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
        Assert.Equal("internal_error", harness.State.LastError);
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
            new NewsClassification(["NVDA"], NewsSentiments.Positive, 3, "회복"), "qwen", 10, true, "v2c");
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();

        harness.Page(1, Item("101", "가"), Item("100", "기준"));
        await harness.PollAsync();

        Assert.Equal("v2c", Assert.Single(harness.Saved()).PromptVersion);
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
            new NewsClassification(["MARKET"], NewsSentiments.Negative, 3, "경보 발령"), "qwen", 850, true, "v2c");
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
        Assert.Equal(saved["102"].ClassificationText, saved["101"].ClassificationText);
        Assert.Equal("representative", saved["101"].ClassificationSource);
        Assert.Equal("102", saved["101"].EvidenceArticleId);
    }

    [Fact]
    public async Task GroupFollowerStillGetsItsOwnFeedTagsForced()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Negative, 3, "경보 발령"), "qwen", 10, true, "v2c");
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
    [Fact]
    public async Task RestoreRequeuesOnlyLimitedArticlesMissingClassificationFields()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Neutral, 1, "영향 제한", "시장 주요 기업 기사", "야후 파이낸스", new Dictionary<string, int> { ["MARKET"] = 10 }),
            "qwen", 10, true, "v2c");
        var firstCollectedAt = Start.AddHours(-2);
        var record = new NewsRecord("90", "영문 기사", "Yahoo Finance", Start, [], [], ["MARKET"],
            NewsSentiments.Unclassified, 0, "", "qwen", 60000, Start, CollectedAt: firstCollectedAt);
        harness.Store.Files["2026-09-12.jsonl"] =
            [JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))];
        harness.Page(1, Item("100", "기준"));

        await harness.PollAsync();

        Assert.Single(harness.Classifier.Requests);
        Assert.Equal(NewsSentiments.Neutral, harness.Saved().Last().Sentiment);
        Assert.Equal("시장 주요 기업 기사", harness.Saved().Last().TitleKo);
        Assert.Equal("야후 파이낸스", harness.Saved().Last().SourceKo);
        Assert.Equal(10, harness.Saved().Last().ImpactScores!["MARKET"]);
        Assert.Equal(firstCollectedAt, harness.Saved().Last().CollectedAt);
    }

    [Fact]
    public async Task 재분류가_저장된_요약과_본문을_복원한다()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Neutral, 1, "본문 확인", "제목", "출처", new Dictionary<string, int> { ["MARKET"] = 1 }),
            "qwen", 10, true, "v2c");
        var record = new NewsRecord("90", "저장 제목", "출처", Start, [], [], ["MARKET"], NewsSentiments.Unclassified, 0, "", "qwen", 10, Start,
            NewsInputKinds.Body, "v2c", null, null, null, null, null, "저장 요약", "저장 본문");
        harness.Store.Files["2026-09-12.jsonl"] = [JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))];
        harness.Page(1, Item("100", "기준"));

        await harness.PollAsync();

        var request = Assert.Single(harness.Classifier.Requests);
        Assert.Contains("저장 요약", request.Body);
        Assert.Contains("저장 본문", request.Body);
        var saved = harness.Saved().Last();
        Assert.Equal("저장 요약", saved.Summary);
        Assert.Equal("저장 본문", saved.Content);
    }

    [Fact]
    public async Task 재분류가_저장된_InputKind를_유지한다()
    {
        var harness = new Harness();
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Neutral, 1, "요약 확인"), "qwen", 10, true, "v2c");
        var record = new NewsRecord("91", "저장 제목", "출처", Start, [], [], ["MARKET"], NewsSentiments.Unclassified, 0, "", "qwen", 10, Start,
            NewsInputKinds.Summary, "v2c", null, null, null, null, null, "저장 요약", "");
        harness.Store.Files["2026-09-12.jsonl"] = [JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))];
        harness.Page(1, Item("100", "기준"));

        await harness.PollAsync();

        Assert.Equal(NewsInputKinds.Summary, harness.Saved().Last().InputKind);
    }

    [Fact]
    public async Task ReclassificationContinuesOnLaterPollsAfterPerPollLimit()
    {
        var harness = new Harness();
        harness.Options.ReclassifyUnclassifiedPerPoll = 3;
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Neutral, 1, "영향 제한", "한국어 제목", "한국어 출처", new Dictionary<string, int> { ["MARKET"] = 5 }),
            "qwen", 10, true, "v2c");
        var records = Enumerable.Range(90, 5).Select(id => new NewsRecord(id.ToString(), $"기사 {id}", "Yahoo Finance", Start, [], [], ["MARKET"], NewsSentiments.Unclassified, 0, "", "qwen", 60000, Start));
        harness.Store.Files["2026-09-12.jsonl"] = records.Select(x => JsonSerializer.Serialize(x, new JsonSerializerOptions(JsonSerializerDefaults.Web))).ToList();
        harness.Page(1, Item("100", "기준"));

        await harness.PollAsync();
        await harness.PollAsync();

        Assert.Equal(5, harness.Classifier.Requests.Count);
    }


    [Fact]
    public async Task PapagoFailureKeepsOllamaTranslationFields()
    {
        var harness = new Harness(new FakeNewsTranslator());
        harness.Classifier.Respond = _ => new NewsClassificationResult(
            new NewsClassification(["MARKET"], NewsSentiments.Neutral, 1, "영향 제한", "Ollama 번역", "Ollama 출처", new Dictionary<string, int> { ["MARKET"] = 4 }),
            "qwen", 10, true, "v2c");
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();
        harness.Page(1, Item("101", "영문 기사"), Item("100", "기준"));
        await harness.PollAsync();
        Assert.Equal("Ollama 번역", harness.Saved().Single().TitleKo);
        Assert.Equal("Ollama 출처", harness.Saved().Single().SourceKo);
    }

    [Fact]
    public async Task 재기동은_pending_번역을_백그라운드큐에_복구한다()
    {
        var translator = new FakeNewsTranslator { TextRespond = text => "번역:" + text };
        var harness = new Harness(translator);
        var record = new NewsRecord("pending", "English", "한국어 출처", Start, [], [], ["MARKET"],
            NewsSentiments.Neutral, 1, "이유", "qwen", 10, Start,
            ImpactScores: new Dictionary<string, int> { ["MARKET"] = 1 }, TranslationStatus: "pending");
        harness.Store.Files["2026-09-12.jsonl"] =
            [JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))];
        harness.Page(1, Item("100", "기준"));

        await harness.PollAsync();

        Assert.Equal(1, harness.TranslationQueue!.QueueDepth);
        Assert.True(await harness.TranslationQueue.ProcessNextAsync());
        Assert.Equal("번역:English", harness.State.Find("pending")!.TitleKo);
    }

    [Fact]
    public async Task 미처리_inbox가_재기동후_한번만_분류된다()
    {
        var harness = new Harness();
        harness.Page(1, Item("100", "기준"));
        await harness.PollAsync();
        harness.Classifier.Respond = _ => new NewsClassificationResult(null, "qwen", 0, false);
        harness.Page(1, Item("101", "복구 기사"), Item("100", "기준"));
        await harness.PollAsync();
        Assert.Empty(harness.Saved());

        var restarted = new Harness();
        restarted.Store.Texts[NewsFeedService.StateFile] = harness.Store.Texts[NewsFeedService.StateFile];
        restarted.Page(1, Item("101", "복구 기사"), Item("100", "기준"));
        await restarted.PollAsync();
        await restarted.PollAsync();

        Assert.Single(restarted.Classifier.Requests);
        Assert.Single(restarted.Saved());
        var state = JsonSerializer.Deserialize<NewsFeedState>(restarted.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.All(state.Inbox!, entry => Assert.True(entry.Processed));
    }

    [Fact]
    public async Task UTC자정뒤_도착한_전날기사가_재기동에도_중복없이_복원된다()
    {
        var harness = new Harness();
        harness.Clock.Now = new DateTimeOffset(2026, 9, 12, 23, 59, 0, TimeSpan.Zero);
        var baseline = NewsBuilder.Item("base", "기준", harness.Clock.Now, []);
        harness.Page(1, baseline);
        await harness.PollAsync();
        harness.Clock.Now = new DateTimeOffset(2026, 9, 13, 0, 1, 0, TimeSpan.Zero);
        var late = NewsBuilder.Item("late", "전날 지연 기사", harness.Clock.Now.AddMinutes(-3), []);
        harness.Page(1, late, baseline);
        await harness.PollAsync();
        Assert.Single(harness.Store.Files["2026-09-13.jsonl"]);

        var restarted = new Harness();
        restarted.Clock.Now = harness.Clock.Now.AddMinutes(1);
        restarted.Store.Texts[NewsFeedService.StateFile] = harness.Store.Texts[NewsFeedService.StateFile];
        foreach (var file in harness.Store.Files)
            restarted.Store.Files[file.Key] = file.Value.ToList();
        restarted.Page(1, late, baseline);
        await restarted.PollAsync();

        Assert.Empty(restarted.Classifier.Requests);
        Assert.Equal("late", Assert.Single(restarted.State.Recent()).Id);
    }

    [Fact]
    public async Task 추적파라미터만_다른_URL은_공급자ID가_달라도_중복수집하지_않는다()
    {
        var harness = new Harness();
        harness.Page(1, new NewsFeedItem("first", "기준", "요약", "Reuters", Start, [],
            Url: "https://EXAMPLE.com/article?id=7&utm_source=feed", Provider: "one"));
        await harness.PollAsync();
        harness.Page(1, new NewsFeedItem("second", "중복", "요약", "Reuters", Start.AddMinutes(1), [],
            Url: "https://example.com/article?utm_medium=rss&id=7#fragment", Provider: "two"));

        await harness.PollAsync();

        Assert.Empty(harness.Classifier.Requests);
        Assert.Equal("https://example.com/article?id=7", NewsFeedService.CanonicalUrl(
            "https://EXAMPLE.com/article?utm_source=feed&id=7#part"));
    }

    [Fact]
    public async Task 쿼터대기는_성공수집과_구분해_health에_남긴다()
    {
        var harness = new Harness();
        harness.Feed.BatchStatus = "quota_wait";
        harness.Feed.ProviderStatuses = [new NewsProviderFetchStatus("marketaux", "quota_wait", 0)];

        await harness.PollAsync();

        Assert.Equal("quota_wait", harness.State.FeedStatus);
        Assert.Null(harness.State.LastSuccessAt);
        Assert.NotNull(harness.State.LastFetchAt);
        Assert.Equal("marketaux", Assert.Single(harness.State.Providers).Provider);
    }

    [Fact]
    public async Task 제외와_review는_cursor에_영속되지만_분류큐와_사용자뉴스에는_들어가지_않는다()
    {
        var harness = new Harness();
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Page(1, Relevant("base", "Fed raises interest rates", NewsRelevanceDecisions.Include));
        await harness.PollAsync();
        harness.Page(1,
            Relevant("sports", "United striker scores a goal", NewsRelevanceDecisions.Exclude),
            Relevant("unclear", "Market reaction remains unclear", NewsRelevanceDecisions.Review),
            Relevant("base", "Fed raises interest rates", NewsRelevanceDecisions.Include));

        await harness.PollAsync();

        Assert.Empty(harness.Classifier.Requests);
        Assert.Empty(harness.Saved());
        var savedState = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains("sports", savedState.SeenIds!);
        Assert.Contains("unclear", savedState.SeenIds!);
        Assert.True(savedState.Inbox!.Single(x => x.Item.Id == "sports").Processed);
        Assert.False(savedState.Inbox!.Single(x => x.Item.Id == "unclear").Processed);

        var restarted = new Harness();
        restarted.Feed.Name = NewsFeedProviders.SbhNews;
        restarted.Store.Texts[NewsFeedService.StateFile] = harness.Store.Texts[NewsFeedService.StateFile];
        restarted.Page(1,
            Relevant("sports", "United striker scores a goal", NewsRelevanceDecisions.Exclude),
            Relevant("unclear", "Market reaction remains unclear", NewsRelevanceDecisions.Review));
        await restarted.PollAsync();
        Assert.Empty(restarted.Classifier.Requests);
    }

    [Fact]
    public async Task 상위_page가_제외기사뿐이어도_다음_page의_관련기사를_수집한다()
    {
        var harness = new Harness();
        harness.Options.MaxPages = 2;
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Page(1, Relevant("base", "Fed raises interest rates", NewsRelevanceDecisions.Include));
        await harness.PollAsync();
        harness.Page(1,
            Relevant("sports-2", "Football match result", NewsRelevanceDecisions.Exclude),
            Relevant("sports-1", "Premier League goal", NewsRelevanceDecisions.Exclude));
        harness.Page(2, Relevant("market-2", "Iran strike disrupts oil supply", NewsRelevanceDecisions.Include));

        await harness.PollAsync();

        Assert.Equal("Iran strike disrupts oil supply", Assert.Single(harness.Classifier.Requests).Title);
        Assert.Equal("market-2", Assert.Single(harness.Saved()).Id);
    }

    static NewsFeedItem Relevant(string id, string title, string decision)
        => Item(id, title) with
        {
            Provider = NewsFeedProviders.SbhNews,
            Relevance = new NewsRelevanceAssessment(NewsRelevancePolicy.CurrentVersion, decision, "test", "actor",
                "action", [new("MARKET", "market", "direct", "evidence")], "evidence", "test")
        };

    [Fact]
    public async Task review는_poll을_막지_않고_pending으로_영속되며_worker_성공만_반영한다()
    {
        var adjudicator = new FakeRelevanceAdjudicator(new NewsRelevanceAssessment(
            NewsRelevancePolicy.CurrentVersion, NewsRelevanceDecisions.Include, "macro_policy", "Fed", "raises",
            [new("MARKET", "market", "direct", "Fed raises rates")], "Fed raises rates", "ollama_adjudicated"));
        var harness = new Harness(null, adjudicator);
        harness.Options.SbhRelevanceAdjudicationEnabled = true;
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Page(1, Relevant("base", "base", NewsRelevanceDecisions.Include));
        await harness.PollAsync();
        harness.Page(1, Relevant("review", "Fed discusses policy", NewsRelevanceDecisions.Review));

        await harness.PollAsync();
        var pending = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(pending.Inbox!.Single(x => x.Item.Id == "review").Processed);

        using var stop = new CancellationTokenSource();
        var worker = harness.Service.RunRelevanceAdjudicationWorkerAsync(stop.Token);
        await adjudicator.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        var saved = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(NewsRelevanceDecisions.Include, saved.Inbox!.Single(x => x.Item.Id == "review").Item.Relevance!.Decision);
    }

    [Fact]
    public async Task 재시작은_pending_inbox를_재큐하고_실패시_pending을_유지한다()
    {
        var first = new Harness();
        first.Feed.Name = NewsFeedProviders.SbhNews;
        first.Page(1, Relevant("base", "base", NewsRelevanceDecisions.Include));
        await first.PollAsync();
        first.Page(1, Relevant("review", "unclear", NewsRelevanceDecisions.Review));
        await first.PollAsync();

        var unavailable = new FakeRelevanceAdjudicator(null);
        var restarted = new Harness(null, unavailable);
        restarted.Options.SbhRelevanceAdjudicationEnabled = true;
        restarted.Feed.Name = NewsFeedProviders.SbhNews;
        restarted.Store.Texts[NewsFeedService.StateFile] = first.Store.Texts[NewsFeedService.StateFile];
        restarted.Page(1, Relevant("review", "unclear", NewsRelevanceDecisions.Review));
        await restarted.PollAsync();
        using var stop = new CancellationTokenSource();
        var worker = restarted.Service.RunRelevanceAdjudicationWorkerAsync(stop.Token);
        await unavailable.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        var state = JsonSerializer.Deserialize<NewsFeedState>(restarted.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(state.Inbox!.Single(x => x.Item.Id == "review").Processed);
        Assert.Equal(NewsRelevanceDecisions.Review, state.Inbox.Single(x => x.Item.Id == "review").Item.Relevance!.Decision);
    }

    [Fact]
    public async Task 재심_queue는_설정용량을_넘지_않고_inbox_pending을_보존한다()
    {
        var harness = new Harness(null, new FakeRelevanceAdjudicator(null));
        harness.Options.SbhRelevanceAdjudicationEnabled = true;
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Page(1, Relevant("base", "base", NewsRelevanceDecisions.Include));
        await harness.PollAsync();
        harness.Page(1, Enumerable.Range(1, 33)
            .Select(x => Relevant($"review-{x}", $"unclear {x}", NewsRelevanceDecisions.Review)).ToArray());

        await harness.PollAsync();

        Assert.Equal("queue_full", harness.State.RelevanceAdjudicationStatus);
        Assert.Equal(32, harness.State.RelevanceAdjudicationQueue);
        var state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(33, state.Inbox!.Count(x => x.Item.Id.StartsWith("review-") && !x.Processed));
    }

    [Fact]
    public async Task worker_timeout은_poll을_막지_않고_review_pending과_사유를_유지한다()
    {
        var blocking = new BlockingRelevanceAdjudicator();
        var harness = new Harness(blocking, options =>
        {
            options.SbhRelevanceAdjudicationEnabled = true;
            options.SbhRelevanceAdjudicationTimeoutSeconds = 1;
        });
        harness.Feed.Name = NewsFeedProviders.SbhNews;
        harness.Page(1, Relevant("base", "base", NewsRelevanceDecisions.Include));
        await harness.PollAsync();
        harness.Page(1, Relevant("review", "unclear", NewsRelevanceDecisions.Review));

        await harness.PollAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        using var stop = new CancellationTokenSource();
        var worker = harness.Service.RunRelevanceAdjudicationWorkerAsync(stop.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(1200);

        var state = JsonSerializer.Deserialize<NewsFeedState>(harness.Store.Texts[NewsFeedService.StateFile],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(state.Inbox!.Single(x => x.Item.Id == "review").Processed);
        Assert.Equal(NewsRelevanceDecisions.Review, state.Inbox.Single(x => x.Item.Id == "review").Item.Relevance!.Decision);
        Assert.Equal("pending", harness.State.RelevanceAdjudicationStatus);
        Assert.Equal("timeout_offline_or_invalid_response", harness.State.RelevanceAdjudicationReason);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    sealed class FakeRelevanceAdjudicator(NewsRelevanceAssessment? result) : INewsRelevanceAdjudicator
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NewsRelevanceAssessment?> AdjudicateAsync(NewsFeedItem item, CancellationToken ct)
        {
            Called.TrySetResult();
            return Task.FromResult(result);
        }
    }

    sealed class BlockingRelevanceAdjudicator : INewsRelevanceAdjudicator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NewsRelevanceAssessment?> AdjudicateAsync(NewsFeedItem item, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
    }

}
