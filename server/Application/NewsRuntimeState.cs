using System.Text.Json.Serialization;
using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>`News` 설정 섹션(#151 §7). Enabled 기본 false이며 false면 어떤 외부 호출도 하지 않는다. 피드는 SBHNews 하나다(#339).</summary>
public sealed class NewsOptions
{
    public bool Enabled { get; set; }
    /// <summary>SBH 공개 데이터(정책금리) 연결 여부. 뉴스 피드 자체는 항상 SBHNews다(#339).</summary>
    public bool UseSbhNews { get; set; }
    public string SbhNewsRssUrl { get; set; } = "https://www.sbhnews.com/feed.xml";
    public int PollSeconds { get; set; } = 60;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public bool SbhRelevanceAdjudicationEnabled { get; set; }
    public int SbhRelevanceAdjudicationMaxPerPoll { get; set; } = 3;
    public int SbhRelevanceAdjudicationConcurrency { get; set; } = 1;
    public int SbhRelevanceAdjudicationTimeoutSeconds { get; set; } = 8;
    public int SbhRelevanceAdjudicationQueueCapacity { get; set; } = 32;
    public int SbhRelevanceAdjudicationMaxAttempts { get; set; } = 3;
    public int SbhRelevanceReviewTtlMinutes { get; set; } = 30;
    public int SbhRelevanceRetryBackoffSeconds { get; set; } = 60;
    /// <summary>공급자 베이스라인(첫 fetch)에서도 이 창 안의 include 기사는 분류한다.</summary>
    public int BaselineRecentHours { get; set; } = 2;
    public int MaxClassificationsPerMinute { get; set; } = 12;
    public double HalfLifeMinutes { get; set; } = NewsSentimentDecay.DefaultHalfLifeMinutes;
    public string KeepAlive { get; set; } = "30m";

    public int RssDailyRequestLimit { get; set; } = 1440;
    public int InboxRetentionHours { get; set; } = 168;
    public int InboxCapacity { get; set; } = 5000;

    /// <summary>목록 확장 상한. 신규가 한 페이지를 넘칠 때만 다음 페이지를 본다.</summary>
    public int MaxPages { get; set; } = 1;

    /// <summary>목록·상세를 합한 피드 요청 예산(#151 §1 "분당 요청 ≤3").</summary>
    public int MaxFeedRequestsPerMinute { get; set; } = 1;

    /// <summary>외부 피드의 24시간 요청 상한. 공급자 쿼터를 넘지 않도록 보수적으로 제한한다.</summary>
    public int MaxDailyFeedRequests { get; set; } = 1440;

    public int MaxQueue { get; set; } = 100;

    /// <summary>재기동 시 영향도 누락·미분류 기사에 대한 분류 재시도 상한.</summary>
    public int ReclassifyUnclassifiedPerPoll { get; set; } = 3;

    /// <summary>일자 파일 상한(바이트). 넘으면 더 쓰지 않되 기존 기록은 지우지 않는다.</summary>
    public long MaxDailyBytes { get; set; } = 5 * 1024 * 1024;

    public int RecentCapacity { get; set; } = 300;
}

/// <summary>
/// 저장·조회 공용 기사 레코드(#151 §5). <see cref="ClassifiedFrom"/>은 사건 그룹 대표의 판정을
/// 복사해 저장한 팔로워 기사에만 대표 기사 id로 채워진다(#171). 그룹이 없거나 자신이 대표면 null이다.
/// <c>*Ko</c>·<c>*TranslationStatus</c>·<see cref="TranslationContentHash"/>는 구 번역 레코드 읽기 호환용이며 새 레코드에는 쓰지 않는다(#339).
/// </summary>
public sealed record NewsRecord(
    string Id,
    string Title,
    string Source,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> MatchedSymbols,
    IReadOnlyList<string> Symbols,
    string Sentiment,
    int Strength,
    string Reason,
    string Model,
    long LatencyMs,
    DateTimeOffset ClassifiedAt,
    string InputKind = NewsInputKinds.Body,
    string PromptVersion = "",
    string? ClassifiedFrom = null,
    IReadOnlyList<NewsEntity>? Entities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TitleKo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceKo = null,
    IReadOnlyDictionary<string, int>? ImpactScores = null,
    string Summary = "",
    string Content = "",
    string? Url = null,
    DateTimeOffset? CollectedAt = null,
    string EvidenceSource = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TranslationStatus = null,
    string ClassificationText = "",
    string ClassificationSource = "",
    string? EvidenceArticleId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SummaryKo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentKo = null,
    string PublishedAtStatus = "known",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TitleTranslationStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SummaryTranslationStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentTranslationStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassificationTranslationStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassificationTextKo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TranslationContentHash = null,
    NewsRelevanceAssessment? Relevance = null);

public sealed record NewsProviderRuntimeStatus(
    string Provider,
    string Status,
    int Count,
    int NewCount,
    DateTimeOffset LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastNewArticleAt,
    TimeSpan? RetryAfter = null,
    int IncludedCount = 0,
    int ExcludedCount = 0,
    int ReviewCount = 0,
    string? FilterPolicyVersion = null);

public sealed record NewsRelevanceExclusionSample(string Title, string Reason, DateTimeOffset At);

/// <summary>SBH 관련성 필터 누적 통계와 최근 제외 표본(§A 개선, #310).</summary>
public sealed record NewsRelevanceFilterStats(string? PolicyVersion, long Included, long Excluded, long Review,
    long ReviewUnadjudicated, IReadOnlyList<NewsRelevanceExclusionSample> RecentExcluded);

/// <summary>health·조회가 함께 보는 뉴스 런타임 상태(#151 §6). 스레드 안전하다.</summary>
public sealed class NewsRuntimeState
{
    readonly object _gate = new();
    readonly LinkedList<NewsRecord> _recent = new();
    readonly Dictionary<string, LinkedListNode<NewsRecord>> _index = new(StringComparer.Ordinal);

    public DateTimeOffset? LastPollAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public DateTimeOffset? LastFetchAt { get; private set; }
    public DateTimeOffset? LastNewArticleAt { get; private set; }
    public DateTimeOffset? LatestPublishedAt { get; private set; }
    public string? LastError { get; private set; }
    public int Queue { get; private set; }
    public long Dropped { get; private set; }
    public long Seen { get; private set; }
    public long Classified { get; private set; }
    public bool OllamaOk { get; private set; } = true;
    public bool StorageLimited { get; private set; }
    public string FeedStatus { get; private set; } = "idle";
    public string RelevanceAdjudicationStatus { get; private set; } = "idle";
    public string RelevanceAdjudicationReason { get; private set; } = "";
    public int RelevanceAdjudicationQueue { get; private set; }
    public IReadOnlyList<NewsProviderRuntimeStatus> Providers { get; private set; } = [];

    const int RecentExclusionCapacity = 5;
    readonly LinkedList<NewsRelevanceExclusionSample> _recentExclusions = new();
    string? _relevancePolicyVersion;
    long _relevanceIncluded, _relevanceExcluded, _relevanceReview, _relevanceUnadjudicated;

    public NewsRelevanceFilterStats RelevanceFilter
    {
        get
        {
            lock (_gate)
                return new NewsRelevanceFilterStats(_relevancePolicyVersion, _relevanceIncluded, _relevanceExcluded,
                    _relevanceReview, _relevanceUnadjudicated, _recentExclusions.ToArray());
        }
    }

    public void RelevanceObserved(NewsRelevanceAssessment assessment, string title, DateTimeOffset at, bool countDecision = true)
    {
        lock (_gate)
        {
            _relevancePolicyVersion = assessment.PolicyVersion;
            if (countDecision)
            {
                if (assessment.Decision == NewsRelevanceDecisions.Include) _relevanceIncluded++;
                else if (assessment.Decision == NewsRelevanceDecisions.Review) _relevanceReview++;
                else _relevanceExcluded++;
            }
            if (assessment.Decision == NewsRelevanceDecisions.Exclude) AddExclusion(title, assessment.Reason, at);
        }
    }

    public void RelevanceUnadjudicated(string title, string reason, DateTimeOffset at)
    {
        lock (_gate)
        {
            _relevanceUnadjudicated++;
            AddExclusion(title, reason, at);
        }
    }

    void AddExclusion(string title, string reason, DateTimeOffset at)
    {
        _recentExclusions.AddFirst(new NewsRelevanceExclusionSample(title, reason, at));
        while (_recentExclusions.Count > RecentExclusionCapacity) _recentExclusions.RemoveLast();
    }

    public void PollStarted(DateTimeOffset at) { lock (_gate) LastAttemptAt = at; }
    public void PollCompleted(DateTimeOffset at) => CollectionCompleted(at, "ok", true, 0, null, []);
    public void CollectionCompleted(DateTimeOffset at, string status, bool fetched, int newCount,
        DateTimeOffset? latestPublishedAt, IReadOnlyList<NewsProviderFetchStatus> providers)
    {
        lock (_gate)
        {
            LastPollAt = at;
            FeedStatus = status;
            LastError = status is "failed" or "invalid_response" ? "provider_failed" : null;
            if (fetched) LastFetchAt = at;
            if (status is "ok" or "empty" or "partial" or "baseline") LastSuccessAt = at;
            if (newCount > 0) LastNewArticleAt = at;
            if (latestPublishedAt is not null && latestPublishedAt != DateTimeOffset.MinValue
                && (LatestPublishedAt is null || latestPublishedAt > LatestPublishedAt))
                LatestPublishedAt = latestPublishedAt;
            if (providers.Count == 0) return;
            var previous = Providers.ToDictionary(x => x.Provider, StringComparer.OrdinalIgnoreCase);
            Providers = providers.Select(x =>
            {
                previous.TryGetValue(x.Provider, out var prior);
                var succeeded = x.Status is "ok" or "empty" or "partial";
                var preserveFilterSnapshot = x.FilterPolicyVersion is null && prior?.FilterPolicyVersion is not null;
                return new NewsProviderRuntimeStatus(x.Provider, x.Status, x.Count, x.NewCount, at,
                    succeeded ? at : prior?.LastSuccessAt,
                    x.NewCount > 0 ? at : prior?.LastNewArticleAt, x.RetryAfter,
                    preserveFilterSnapshot ? prior!.IncludedCount : x.IncludedCount,
                    preserveFilterSnapshot ? prior!.ExcludedCount : x.ExcludedCount,
                    preserveFilterSnapshot ? prior!.ReviewCount : x.ReviewCount,
                    preserveFilterSnapshot ? prior!.FilterPolicyVersion : x.FilterPolicyVersion);
            }).ToArray();
        }
    }
    public void PollFailed(DateTimeOffset at, string error) { lock (_gate) { LastPollAt = at; LastError = error; FeedStatus = "failed"; } }
    public void QueueDepth(int depth) { lock (_gate) Queue = depth; }
    public void ClearLegacyRecords()
    {
        lock (_gate)
        {
            var retained = _recent.Where(x => string.Equals(x.Source, NewsFeedProviders.SbhNewsSource, StringComparison.Ordinal)).ToArray();
            _recent.Clear();
            _index.Clear();
            foreach (var record in retained.Reverse())
            {
                var node = _recent.AddFirst(record);
                _index[record.Id] = node;
            }
        }
    }
    public void Drop(int count) { lock (_gate) Dropped += count; }
    public void SeenArticles(int count) { lock (_gate) Seen += count; }
    public void Ollama(bool ok) { lock (_gate) OllamaOk = ok; }
    public void RelevanceAdjudication(string status, string reason, int queue)
    { lock (_gate) { RelevanceAdjudicationStatus = status; RelevanceAdjudicationReason = reason; RelevanceAdjudicationQueue = queue; } }
    public void Limited(bool limited) { lock (_gate) StorageLimited = limited; }

    public void Add(NewsRecord record, int capacity)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(record.Id, out var existing)) { _recent.Remove(existing); _index.Remove(record.Id); }
            else Classified++;
            var node = _recent.AddFirst(record);
            _index[record.Id] = node;
            while (_recent.Count > Math.Max(1, capacity))
            {
                var last = _recent.Last!;
                _recent.RemoveLast();
                _index.Remove(last.Value.Id);
            }
        }
    }

    public IReadOnlyList<NewsRecord> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    public NewsRecord? Find(string id)
    {
        lock (_gate) return _index.TryGetValue(id, out var node) ? node.Value : null;
    }
}
