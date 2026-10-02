using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Astra.Server.Domain;
using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>증분 상태(#151 §1). 첫 기동은 기준점만 저장하고 과거 기사를 분류하지 않는다.</summary>
public sealed record NewsInboxEntry(
    IReadOnlyList<string> Keys,
    NewsFeedItem Item,
    DateTimeOffset CollectedAt,
    bool Processed,
    int ReviewAttempts = 0,
    DateTimeOffset? ReviewDeadline = null,
    DateTimeOffset? ReviewLastAttemptAt = null);

/// <summary>재심 미완료 review 기사의 확정 제외 사유(§A R1, #310).</summary>
public static class NewsRelevanceReasons
{
    public const string ReviewUnadjudicatedDisabled = "review_unadjudicated_disabled";
    public const string ReviewUnadjudicatedTimeout = "review_unadjudicated_timeout";
}

public sealed record NewsFeedState(long LastId, DateTimeOffset? LastCreatedAt,
    IReadOnlyList<DateTimeOffset>? FeedRequestTimes = null, string? LastKey = null,
    IReadOnlyList<string>? SeenIds = null,
    IReadOnlyList<NewsInboxEntry>? Inbox = null,
    string? RequestProvider = null,
    DateTimeOffset? LastFeedRequestAt = null,
    IReadOnlyList<string>? BaselinedProviders = null,
    DateTimeOffset? FeedRetryAfterUntil = null);

/// <summary>
/// 뉴스 수집·큐·분류 파이프라인(#151 §1·§3·§5). 호스트 타이머가 <see cref="PollAsync"/>만 호출한다.
/// 사용자 요구로 새 기사는 매칭 여부와 무관하게 모두 분류하고, 관심종목 매칭 기사가 큐에서 앞선다.
/// </summary>
public sealed class NewsFeedService(
    NewsOptions options,
    INewsFeed feed,
    INewsClassifier classifier,
    INewsStore store,
    ILocalStore localStore,
    NewsRuntimeState state,
    IMonitorDiagnostics diagnostics,
    TimeProvider clock,
    INewsRelevanceAdjudicator? relevanceAdjudicator = null,
    INewsRelevancePolicy? relevancePolicy = null,
    INewsRelevanceContextSource? relevanceContext = null)
{
    public const string StateFile = "state.json";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly LinkedList<QueuedArticle> _matched = new();
    readonly LinkedList<QueuedArticle> _other = new();
    readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    readonly Queue<DateTimeOffset> _classifications = new();
    readonly Queue<DateTimeOffset> _dailyFeedRequests = new();
    readonly Queue<DateTimeOffset> _minuteFeedRequests = new();
    readonly Channel<string> _relevanceReviews = Channel.CreateBounded<string>(new BoundedChannelOptions(
        Math.Clamp(options.SbhRelevanceAdjudicationQueueCapacity, 1, 500))
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    readonly HashSet<string> _queuedRelevanceReviews = new(StringComparer.Ordinal);

    /// <summary>대표 기사 id -> 같은 사건 그룹의 나머지 기사(#171). 대표가 분류되면 함께 저장한다.</summary>
    readonly Dictionary<string, List<QueuedArticle>> _pendingFollowers = new(StringComparer.Ordinal);

    NewsFeedState? _state;
    bool _stateLoaded;
    bool _restored;
    string? _dayFile;
    long _dayBytes;

    sealed record QueuedArticle(NewsArticle Article, IReadOnlyList<string> MatchedSymbols,
        DateTimeOffset CollectedAt, NewsRecord? Prior = null);
    sealed record CollectionResult(int Budget, string Status, bool Fetched, int NewCount,
        DateTimeOffset? LatestPublishedAt, IReadOnlyList<NewsProviderFetchStatus> Providers);

    public int QueueDepth { get { lock (_queued) return _matched.Count + _other.Count; } }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        state.PollStarted(clock.GetUtcNow());
        await _gate.WaitAsync(ct);
        try
        {
            await RestoreAsync(ct);
            QueueMissingReclassifications();
            var collection = await CollectAsync(Math.Max(1, options.MaxFeedRequestsPerMinute), ct);
            await DrainAsync(collection.Budget, ct);
            state.QueueDepth(QueueDepth);
            state.CollectionCompleted(clock.GetUtcNow(), collection.Status, collection.Fetched,
                collection.NewCount, collection.LatestPublishedAt, collection.Providers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            diagnostics.PollFailed("news-feed", new InvalidOperationException("뉴스 수집 처리에 실패했습니다."));
            state.PollFailed(clock.GetUtcNow(), "internal_error");
        }
        catch (Exception) { diagnostics.PollFailed("news-feed", new InvalidOperationException("뉴스 수집 처리에 실패했습니다.")); state.PollFailed(clock.GetUtcNow(), "internal_error"); }
        finally { _gate.Release(); }
    }

    public async Task<TResult> RunExclusiveMaintenanceAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await operation(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>공급자 전환 정리 뒤 디스크와 메모리의 inbox 상태를 SBHNews로 동기화한다.</summary>
    public async Task PreserveSbhStateAsync(CancellationToken ct)
    {
        var current = await LoadStateAsync(ct);
        if (current is null) return;
        var inbox = current.Inbox?.Where(x => string.Equals(x.Item.Provider, NewsFeedProviders.SbhNews,
            StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        var baselines = (current.BaselinedProviders ?? []).Append(NewsFeedProviders.SbhNews)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await SaveStateAsync(current with
        {
            Inbox = inbox,
            RequestProvider = NewsFeedProviders.SbhNews,
            BaselinedProviders = baselines
        }, ct);
    }

    /// <summary>공급자 전환 정리 시 분류 대기 중인 구형 기사와 사건 팔로어를 폐기한다.</summary>
    public void DiscardLegacyPendingWork()
    {
        bool IsSbh(QueuedArticle item) => string.Equals(item.Article.Source, NewsFeedProviders.SbhNewsSource,
            StringComparison.Ordinal);
        lock (_queued)
        {
            foreach (var item in _matched.Where(x => !IsSbh(x)).Concat(_other.Where(x => !IsSbh(x))).ToArray())
                _queued.Remove(item.Article.Id);
            RemoveLegacy(_matched, IsSbh);
            RemoveLegacy(_other, IsSbh);
            foreach (var key in _pendingFollowers.Keys.ToArray())
            {
                if (!_pendingFollowers.TryGetValue(key, out var followers)) continue;
                var kept = followers.Where(IsSbh).ToList();
                if (kept.Count == 0) _pendingFollowers.Remove(key);
                else _pendingFollowers[key] = kept;
            }
        }

        static void RemoveLegacy(LinkedList<QueuedArticle> queue, Func<QueuedArticle, bool> isSbh)
        {
            var node = queue.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!isSbh(node.Value)) queue.Remove(node);
                node = next;
            }
        }
    }

    /// <summary>뉴스 정리 뒤 당일 저장량을 다시 읽어 용량 판단을 최신화한다.</summary>
    public async Task RefreshDailyStorageAccountingAsync(CancellationToken ct)
    {
        _dayFile = DayFile(clock.GetUtcNow());
        _dayBytes = await store.SizeAsync(_dayFile, ct);
    }

    /// <summary>재기동 후에도 당일 판정을 조회에 보이게 today jsonl을 한 번만 되읽는다.</summary>
    async Task RestoreAsync(CancellationToken ct)
    {
        if (_restored) return;
        _restored = true;
        try
        {
            var now = clock.GetUtcNow();
            var files = new[] { DayFile(now.AddDays(-1)), DayFile(now) };
            var lines = new List<string>();
            foreach (var file in files) lines.AddRange(await store.ReadLinesAsync(file, ct));
            foreach (var line in lines.TakeLast(options.RecentCapacity * 2))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = JsonSerializer.Deserialize<NewsRecord>(line, Json);
                if (record is null) continue;
                state.Add(record, options.RecentCapacity);
            }
        }
        catch (Exception exception) { diagnostics.PollFailed("news-restore", exception); }
    }

    static bool NeedsReclassification(NewsRecord record)
        => string.Equals(record.Sentiment, NewsSentiments.Unclassified, StringComparison.OrdinalIgnoreCase)
            || record.ImpactScores is null;

    void QueueMissingReclassifications()
    {
        var queued = 0;
        foreach (var record in state.Recent())
        {
            if (queued >= Math.Max(0, options.ReclassifyUnclassifiedPerPoll)) break;
            if (!NeedsReclassification(record) || IsQueued(record.Id)) continue;
            var headlineOnly = string.Equals(record.InputKind, NewsInputKinds.Headline, StringComparison.OrdinalIgnoreCase);
            var article = new NewsArticle(record.Id, record.Title, record.Summary, record.Source, record.CreatedAt,
                record.Tickers, headlineOnly ? record.Title : "", headlineOnly, null, record.Entities, record.Content, record.InputKind, record.Url,
                record.Relevance);
            Enqueue(article, record.MatchedSymbols, record.CollectedAt ?? record.ClassifiedAt, record);
            queued++;
        }
    }

    bool IsQueued(string id)
    {
        lock (_queued) return _queued.Contains(id);
    }

    async Task<CollectionResult> CollectAsync(int budget, CancellationToken ct)
    {
        if (budget <= 0) return new CollectionResult(budget, "quota_wait", false, 0, null, []);
        var watchlist = await WatchlistAsync(ct);
        var known = await LoadStateAsync(ct);
        if (known?.Inbox is { Count: > 0 } pendingInbox)
        {
            var swept = SweepReviews(pendingInbox, clock.GetUtcNow());
            if (!ReferenceEquals(swept, pendingInbox)) known = known with { Inbox = swept };
        }
        QueuePendingInbox((known?.Inbox ?? []).Where(x => string.IsNullOrWhiteSpace(x.Item.Provider)
            || string.Equals(x.Item.Provider, feed.Name, StringComparison.OrdinalIgnoreCase)).ToArray(), watchlist);

        var fresh = new List<NewsInboxEntry>();
        var page = 1;
        var maxId = known?.LastId ?? 0;
        DateTimeOffset? maxCreatedAt = known?.LastCreatedAt;
        var maxKey = known?.LastKey;
        var seenIds = new HashSet<string>(known?.SeenIds ?? [], StringComparer.Ordinal);
        var inbox = new List<NewsInboxEntry>(known?.Inbox ?? []);
        var baselinedProviders = new HashSet<string>(known?.BaselinedProviders ?? [], StringComparer.OrdinalIgnoreCase);
        var providerBaseline = !baselinedProviders.Contains(feed.Name);
        var retryAfterUntil = known?.FeedRetryAfterUntil;
        var seenKeys = new HashSet<string>(inbox.SelectMany(x => x.Keys), StringComparer.Ordinal);
        var providerStatuses = new List<NewsProviderFetchStatus>();
        NewsRelevanceContext? gateContext = null;
        var status = "empty";
        var fetched = false;
        DateTimeOffset? latestPublishedAt = null;

        while (page <= Math.Max(1, options.MaxPages) && budget > 0)
        {
            var requestedAt = clock.GetUtcNow();
            if (retryAfterUntil is not null && requestedAt < retryAfterUntil)
            {
                status = "quota_wait";
                providerStatuses.Add(new NewsProviderFetchStatus(feed.Name, "quota_wait", 0, RetryAfter: retryAfterUntil - requestedAt));
                break;
            }
            if (!ReserveFeedRequest(requestedAt))
            {
                status = "quota_wait";
                break;
            }
            // 공급자 호출은 빈 응답이나 예외에서도 쿼터를 소비할 수 있다.
            // 호출 직후 시각을 저장해 재기동으로 일일 한도를 우회하지 않게 한다.
            await SaveStateAsync(new NewsFeedState(maxId, maxCreatedAt, _dailyFeedRequests.ToArray(), maxKey,
                seenIds.ToArray(), inbox, feed.Name, requestedAt, baselinedProviders.ToArray(), retryAfterUntil), ct);
            NewsFeedBatch batch;
            try { batch = await feed.FetchAsync(page, ct); }
            finally { budget--; }
            fetched = true;
            status = batch.Status;
            var retry = batch.Providers.Where(x => x.RetryAfter is { } value && value > TimeSpan.Zero)
                .Select(x => x.RetryAfter!.Value).DefaultIfEmpty().Max();
            if (retry > TimeSpan.Zero) retryAfterUntil = requestedAt + retry;
            var items = batch.Items;

            if (items.Count == 0)
            {
                providerStatuses.AddRange(batch.Providers);
                break;
            }
            var observedThisPage = 0;
            var newByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                seenIds.Add(item.Id);
                var id = ParseId(item.Id);
                if (IsAfter(item.CreatedAt, id, item.Id, maxCreatedAt, maxId, maxKey))
                { maxId = id; maxCreatedAt = item.CreatedAt; maxKey = item.Id; }
                if (item.CreatedAt != DateTimeOffset.MinValue
                    && (latestPublishedAt is null || item.CreatedAt > latestPublishedAt)) latestPublishedAt = item.CreatedAt;
            }

            var baseline = known is null || providerBaseline;
            var gated = new Dictionary<string, GateCounts>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in items)
            {
                var keys = IdentityKeys(raw, feed.Name);
                var alreadySeen = keys.Any(seenKeys.Contains)
                    || (known?.Inbox is null && known?.SeenIds?.Contains(raw.Id, StringComparer.Ordinal) == true);
                if (alreadySeen) continue;
                observedThisPage++;
                var item = raw;
                if (relevancePolicy is not null && item.Relevance is null)
                {
                    gateContext ??= await GateContextAsync(ct);
                    item = item with { Relevance = relevancePolicy.Evaluate(item, gateContext) };
                    var gateProvider = string.IsNullOrWhiteSpace(item.Provider) ? feed.Name : item.Provider;
                    gated[gateProvider] = gated.GetValueOrDefault(gateProvider).Count(item.Relevance!.Decision);
                }
                var eligible = item.Relevance?.Included ?? true;
                var review = IsSbhReview(item);
                var adjudicate = review && AdjudicationAvailable;
                // 베이스라인은 과거 기사를 분류하지 않지만, 최근 창 안의 include 기사까지 버리지는 않는다.
                var recentInclude = baseline && item.Relevance?.Included == true
                    && item.CreatedAt != DateTimeOffset.MinValue && requestedAt - item.CreatedAt <= BaselineRecentWindow;
                var entry = new NewsInboxEntry(keys, item, requestedAt,
                    (baseline && !recentInclude) || (!eligible && !adjudicate));
                if (item.Relevance is not null) state.RelevanceObserved(item.Relevance, item.Title, requestedAt);
                if (review && !adjudicate)
                {
                    entry = Unadjudicated(entry, NewsRelevanceReasons.ReviewUnadjudicatedDisabled);
                    state.RelevanceUnadjudicated(item.Title, NewsRelevanceReasons.ReviewUnadjudicatedDisabled, requestedAt);
                }
                else if (adjudicate) entry = entry with { ReviewDeadline = requestedAt + ReviewTtl };
                inbox.Add(entry);
                if (adjudicate && !entry.Processed) TryQueueReview(item.Id);
                foreach (var key in keys) seenKeys.Add(key);
                if (eligible && (!baseline || recentInclude))
                {
                    fresh.Add(entry);
                    var provider = string.IsNullOrWhiteSpace(item.Provider) ? feed.Name : item.Provider;
                    newByProvider[provider] = newByProvider.GetValueOrDefault(provider) + 1;
                }
            }
            providerStatuses.AddRange(batch.Providers.Select(x =>
            {
                var next = x with { NewCount = newByProvider.GetValueOrDefault(x.Provider) };
                if (x.FilterPolicyVersion is not null || relevancePolicy is null || !gated.TryGetValue(x.Provider, out var counts))
                    return next;
                return next with
                {
                    IncludedCount = counts.Included, ExcludedCount = counts.Excluded, ReviewCount = counts.Review,
                    FilterPolicyVersion = NewsRelevancePolicy.ComposeVersion(relevancePolicy.Version, gateContext ?? NewsRelevanceContext.Empty)
                };
            }));
            if (known is null) break;
            if (observedThisPage < items.Count) break;
            var cutoff = requestedAt - TimeSpan.FromHours(24);
            if (!items.Any(x => x.CreatedAt == DateTimeOffset.MinValue || x.CreatedAt >= cutoff)) break;
            page++;
        }

        var now = clock.GetUtcNow();
        if (fetched && status is "ok" or "empty" or "partial") baselinedProviders.Add(feed.Name);
        var inboxCutoff = now - TimeSpan.FromHours(Math.Max(24, options.InboxRetentionHours));
        inbox = TrimInbox(inbox.Where(x => !x.Processed || x.CollectedAt >= inboxCutoff).ToList(),
            Math.Max(1, options.InboxCapacity));
        if (maxId > 0 || maxCreatedAt is not null || inbox.Count > 0 || fetched)
            await SaveStateAsync(new NewsFeedState(maxId, maxCreatedAt, _dailyFeedRequests.ToArray(), maxKey,
                seenIds.TakeLast(Math.Max(options.InboxCapacity, 1)).ToArray(), inbox, feed.Name,
                fetched ? now : known?.LastFeedRequestAt, baselinedProviders.ToArray(), retryAfterUntil), ct);
        var providerSummary = providerStatuses.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NewsProviderFetchStatus(group.Key, group.Last().Status,
                group.Sum(x => x.Count), group.Sum(x => x.NewCount), group.Last().RetryAfter,
                group.Sum(x => x.IncludedCount), group.Sum(x => x.ExcludedCount), group.Sum(x => x.ReviewCount),
                group.Select(x => x.FilterPolicyVersion).LastOrDefault(x => x is not null))).ToArray();
        var wasBaseline = known is null || providerBaseline;
        var baselineStatus = fetched && status is "ok" or "empty" ? "baseline" : status;
        if (fresh.Count == 0)
            return new CollectionResult(budget, wasBaseline ? baselineStatus : status, fetched, 0, latestPublishedAt, providerSummary);

        state.SeenArticles(fresh.Count);
        QueueInboxArticles(fresh, watchlist);
        state.QueueDepth(QueueDepth);
        return new CollectionResult(budget, wasBaseline ? baselineStatus : status == "empty" ? "ok" : status, fetched, fresh.Count,
            latestPublishedAt, providerSummary);
    }

    readonly record struct GateCounts(int Included, int Excluded, int Review)
    {
        public GateCounts Count(string decision) => decision switch
        {
            NewsRelevanceDecisions.Include => this with { Included = Included + 1 },
            NewsRelevanceDecisions.Review => this with { Review = Review + 1 },
            _ => this with { Excluded = Excluded + 1 },
        };
    }

    TimeSpan BaselineRecentWindow => TimeSpan.FromHours(Math.Max(0, options.BaselineRecentHours));

    async Task<NewsRelevanceContext> GateContextAsync(CancellationToken ct)
    {
        if (relevanceContext is null) return NewsRelevanceContext.Empty;
        try { return await relevanceContext.GetAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return NewsRelevanceContext.Empty; }
    }

    int ReviewQueueCapacity => Math.Clamp(options.SbhRelevanceAdjudicationQueueCapacity, 1, 500);

    /// <summary>상한 초과 시 오래된 처리 완료 항목부터 자른다. 미처리 항목은 자르지 않는다(§A 개선, #310).</summary>
    static List<NewsInboxEntry> TrimInbox(List<NewsInboxEntry> inbox, int capacity)
    {
        var excess = inbox.Count - capacity;
        if (excess <= 0) return inbox;
        var drop = inbox.Select((entry, index) => (entry, index)).Where(x => x.entry.Processed)
            .OrderBy(x => x.entry.CollectedAt).ThenBy(x => x.index).Take(excess)
            .Select(x => x.index).ToHashSet();
        return inbox.Where((_, index) => !drop.Contains(index)).ToList();
    }

    bool AdjudicationAvailable => options.SbhRelevanceAdjudicationEnabled && relevanceAdjudicator is not null
        && string.Equals(feed.Name, NewsFeedProviders.SbhNews, StringComparison.OrdinalIgnoreCase);
    TimeSpan ReviewTtl => TimeSpan.FromMinutes(Math.Max(1, options.SbhRelevanceReviewTtlMinutes));
    int ReviewMaxAttempts => Math.Max(1, options.SbhRelevanceAdjudicationMaxAttempts);
    TimeSpan ReviewBackoff => TimeSpan.FromSeconds(Math.Max(0, options.SbhRelevanceRetryBackoffSeconds));

    /// <summary>미처리 review를 시도 횟수·TTL로 종결하고 backoff가 지난 항목만 재큐한다(§A R1·R2, #310).</summary>
    IReadOnlyList<NewsInboxEntry> SweepReviews(IReadOnlyList<NewsInboxEntry> inbox, DateTimeOffset now)
    {
        var changed = false;
        var requeue = new List<string>();
        var budget = Math.Max(0, options.SbhRelevanceAdjudicationMaxPerPoll);
        var result = new NewsInboxEntry[inbox.Count];
        for (var i = 0; i < inbox.Count; i++)
        {
            var entry = inbox[i];
            if (entry.Processed || !IsSbhReview(entry.Item)) { result[i] = entry; continue; }
            var next = ReviewProgress(entry, now);
            changed |= !ReferenceEquals(next, entry);
            if (next.Processed) state.RelevanceUnadjudicated(next.Item.Title, next.Item.Relevance!.Reason, now);
            if (!next.Processed && requeue.Count < budget
                && (next.ReviewLastAttemptAt is not { } last || now - last >= ReviewBackoff))
                requeue.Add(next.Item.Id);
            result[i] = next;
        }
        foreach (var id in requeue) TryQueueReview(id);
        return changed ? result : inbox;
    }

    NewsInboxEntry ReviewProgress(NewsInboxEntry entry, DateTimeOffset now)
    {
        if (!AdjudicationAvailable) return Unadjudicated(entry, NewsRelevanceReasons.ReviewUnadjudicatedDisabled);
        var current = entry.ReviewDeadline is null ? entry with { ReviewDeadline = entry.CollectedAt + ReviewTtl } : entry;
        return current.ReviewAttempts >= ReviewMaxAttempts || now > current.ReviewDeadline
            ? Unadjudicated(current, NewsRelevanceReasons.ReviewUnadjudicatedTimeout)
            : current;
    }

    static NewsInboxEntry Unadjudicated(NewsInboxEntry entry, string reason)
        => entry with
        {
            Processed = true,
            Item = entry.Item with
            {
                Relevance = entry.Item.Relevance! with { Decision = NewsRelevanceDecisions.Exclude, Reason = reason }
            }
        };

    void TryQueueReview(string id)
    {
        bool queued;
        lock (_queuedRelevanceReviews)
        {
            if (!_queuedRelevanceReviews.Add(id)) return;
            queued = _relevanceReviews.Reader.Count < ReviewQueueCapacity && _relevanceReviews.Writer.TryWrite(id);
            if (!queued) _queuedRelevanceReviews.Remove(id);
        }
        state.RelevanceAdjudication(queued ? "pending" : "queue_full",
            queued ? "awaiting_worker" : "bounded_queue_full", _relevanceReviews.Reader.Count);
    }

    public async Task RunRelevanceAdjudicationWorkerAsync(CancellationToken ct)
    {
        await foreach (var id in _relevanceReviews.Reader.ReadAllAsync(ct))
        {
            NewsFeedItem? item;
            await _gate.WaitAsync(ct);
            try { item = (await LoadStateAsync(ct))?.Inbox?.FirstOrDefault(x => !x.Processed && x.Item.Id == id)?.Item; }
            finally { _gate.Release(); }
            if (item is null || !IsSbhReview(item) || relevanceAdjudicator is null)
            {
                lock (_queuedRelevanceReviews) _queuedRelevanceReviews.Remove(id);
                continue;
            }
            state.RelevanceAdjudication("running", "ollama_adjudication", _relevanceReviews.Reader.Count);
            NewsRelevanceAssessment? assessment = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.SbhRelevanceAdjudicationTimeoutSeconds, 1, 30)));
                assessment = await relevanceAdjudicator.AdjudicateAsync(item, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception exception) { diagnostics.PollFailed("sbh-relevance-adjudication", exception); }
            if (assessment is null)
            {
                await _gate.WaitAsync(ct);
                try { await RecordFailedReviewAsync(id, ct); }
                finally
                {
                    _gate.Release();
                    lock (_queuedRelevanceReviews) _queuedRelevanceReviews.Remove(id);
                }
                continue;
            }
            await _gate.WaitAsync(ct);
            try
            {
                var known = await LoadStateAsync(ct);
                if (known?.Inbox is null) continue;
                var inbox = known.Inbox.Select(x => x.Item.Id == id && !x.Processed && IsSbhReview(x.Item)
                    ? x with { Item = x.Item with { Relevance = assessment }, Processed = assessment.Decision == NewsRelevanceDecisions.Exclude }
                    : x).ToArray();
                await SaveStateAsync(known with { Inbox = inbox }, ct);
                if (assessment.Decision == NewsRelevanceDecisions.Exclude)
                    state.RelevanceObserved(assessment, item.Title, clock.GetUtcNow(), countDecision: false);
                state.RelevanceAdjudication("completed", assessment.Reason, _relevanceReviews.Reader.Count);
            }
            finally
            {
                _gate.Release();
                lock (_queuedRelevanceReviews) _queuedRelevanceReviews.Remove(id);
            }
        }
    }

    async Task RecordFailedReviewAsync(string id, CancellationToken ct)
    {
        var known = await LoadStateAsync(ct);
        var now = clock.GetUtcNow();
        var finalized = false;
        if (known?.Inbox is not null)
        {
            var inbox = known.Inbox.Select(x =>
            {
                if (x.Processed || x.Item.Id != id || !IsSbhReview(x.Item)) return x;
                var next = ReviewProgress(x with { ReviewAttempts = x.ReviewAttempts + 1, ReviewLastAttemptAt = now }, now);
                finalized = next.Processed;
                if (finalized) state.RelevanceUnadjudicated(next.Item.Title, next.Item.Relevance!.Reason, now);
                return next;
            }).ToArray();
            await SaveStateAsync(known with { Inbox = inbox }, ct);
        }
        state.RelevanceAdjudication(finalized ? "completed" : "pending",
            finalized ? NewsRelevanceReasons.ReviewUnadjudicatedTimeout : "timeout_offline_or_invalid_response",
            _relevanceReviews.Reader.Count);
    }

    static bool IsSbhReview(NewsFeedItem item)
        => string.Equals(item.Provider, NewsFeedProviders.SbhNews, StringComparison.OrdinalIgnoreCase)
            && item.Relevance?.Decision == NewsRelevanceDecisions.Review;

    void QueuePendingInbox(IReadOnlyList<NewsInboxEntry> entries, IReadOnlyList<NewsWatchSymbol> watchlist)
        => QueueInboxArticles(entries.Where(x => !x.Processed).ToArray(), watchlist);

    void QueueInboxArticles(IReadOnlyList<NewsInboxEntry> entries, IReadOnlyList<NewsWatchSymbol> watchlist)
    {
        var queued = entries.Where(x => !IsSbhReview(x.Item))
            .OrderBy(x => x.Item.CreatedAt).ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Select(x => (Entry: x, Article: new NewsArticle(x.Item.Id, x.Item.Title, x.Item.Summary, x.Item.Source,
                x.Item.CreatedAt, x.Item.Tickers, x.Item.Headline, x.Item.HeadlineOnly, x.Item.GroupId,
                x.Item.Entities, x.Item.Content, Url: x.Item.Url, Relevance: x.Item.Relevance))).ToArray();
        var followerIds = GroupFollowerArticles(queued, watchlist);
        foreach (var item in queued)
        {
            if (followerIds.Contains(item.Article.Id)) continue;
            Enqueue(item.Article, NewsMatcher.Match(item.Article, watchlist).Symbols, item.Entry.CollectedAt);
        }
    }

    /// <summary>
    /// 사건 그룹 단위 1회 분류(#171 §3). <see cref="NewsArticle.GroupId"/>가 같은 신규 기사 중
    /// 그룹 내 최신 <see cref="NewsArticle.CreatedAt"/> 1건만 대표로 큐에 넣고, 나머지는
    /// <see cref="_pendingFollowers"/>에 쌓아 두었다가 대표가 분류되면 결과를 복사해 저장한다.
    /// </summary>
    HashSet<string> GroupFollowerArticles(
        IReadOnlyList<(NewsInboxEntry Entry, NewsArticle Article)> articles,
        IReadOnlyList<NewsWatchSymbol> watchlist)
    {
        var followerIds = new HashSet<string>(StringComparer.Ordinal);
        var groups = articles
            .Where(a => !string.IsNullOrWhiteSpace(a.Article.GroupId))
            .GroupBy(a => a.Article.GroupId!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var members = group.ToArray();
            if (members.Length < 2) continue;

            var representative = members
                .OrderByDescending(a => a.Article.CreatedAt)
                .ThenByDescending(a => a.Article.Id, StringComparer.Ordinal)
                .First();
            var followers = members.Where(a => a.Article.Id != representative.Article.Id).ToArray();
            foreach (var follower in followers) followerIds.Add(follower.Article.Id);

            _pendingFollowers[representative.Article.Id] = followers
                .Select(a => new QueuedArticle(a.Article, NewsMatcher.Match(a.Article, watchlist).Symbols, a.Entry.CollectedAt))
                .ToList();
        }
        return followerIds;
    }

    /// <summary>큐 상한 초과 시 비매칭 기사부터 버린다(사용자 요구: 매칭 우선).</summary>
    void Enqueue(NewsArticle article, IReadOnlyList<string> matchedSymbols, DateTimeOffset collectedAt, NewsRecord? prior = null)
    {
        lock (_queued)
        {
            if (!_queued.Add(article.Id)) return;
            var entry = new QueuedArticle(article, matchedSymbols, collectedAt, prior);
            if (matchedSymbols.Count > 0) _matched.AddLast(entry); else _other.AddLast(entry);

            var cap = Math.Max(1, options.MaxQueue);
            var dropped = 0;
            while (_matched.Count + _other.Count > cap)
            {
                var victim = _other.First ?? _matched.First;
                if (victim is null) break;
                if (_other.First is not null) _other.RemoveFirst(); else _matched.RemoveFirst();
                _queued.Remove(victim.Value.Article.Id);
                _pendingFollowers.Remove(victim.Value.Article.Id);
                dropped++;
            }
            if (dropped > 0) state.Drop(dropped);
        }
    }

    QueuedArticle? Dequeue()
    {
        lock (_queued)
        {
            var node = _matched.First ?? _other.First;
            if (node is null) return null;
            if (_matched.First is not null) _matched.RemoveFirst(); else _other.RemoveFirst();
            _queued.Remove(node.Value.Article.Id);
            return node.Value;
        }
    }

    void Requeue(QueuedArticle entry)
    {
        lock (_queued)
        {
            if (!_queued.Add(entry.Article.Id)) return;
            if (entry.MatchedSymbols.Count > 0) _matched.AddFirst(entry); else _other.AddFirst(entry);
        }
    }

    async Task DrainAsync(int feedBudget, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Allowed())
        {
            var entry = Dequeue();
            if (entry is null) break;

            var article = entry.Article;
            var body = JoinEvidence(article.Summary, article.Content);
            var inputKind = string.IsNullOrWhiteSpace(entry.Article.InputKind)
                ? NewsInputKinds.Body
                : entry.Article.InputKind;
            var classificationSource = !string.IsNullOrWhiteSpace(article.Content) ? "feed_body"
                : !string.IsNullOrWhiteSpace(article.Summary) ? "feed_excerpt" : "headline";
            // 상세는 관심종목 매칭 기사에만, 남은 피드 요청 예산 안에서 받는다(#151 §1).
            if (entry.MatchedSymbols.Count > 0 && feedBudget > 0)
            {
                feedBudget--;
                try
                {
                    var detail = await feed.DetailAsync(entry.Article.Id, ct);
                    // AI 요약 계약은 유지한다. 요약이 없을 때 상세 본문이 제목보다 우선하고,
                    // 상세가 없으면 피드의 description/content/summary를 함께 사용한다.
                    if (!string.IsNullOrWhiteSpace(detail?.Summary))
                    {
                        body = detail!.Summary;
                        article = article with { Summary = detail.Summary };
                        inputKind = NewsInputKinds.Summary;
                        classificationSource = "detail_summary";
                    }
                    else if (!string.IsNullOrWhiteSpace(detail?.Body))
                    {
                        body = detail!.Body;
                        article = article with { Content = detail.Body };
                        classificationSource = "detail_body";
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception exception) { diagnostics.PollFailed("news-detail", exception); }
            }
            if (inputKind == NewsInputKinds.Body && (entry.Article.HeadlineOnly || string.IsNullOrWhiteSpace(body)))
            {
                inputKind = NewsInputKinds.Headline;
                body = "";
                classificationSource = "headline";
            }

            var title = string.IsNullOrWhiteSpace(article.Headline)
                ? article.Title
                : article.Title + "\n" + article.Headline;

            NewsClassificationResult result;
            try
            {
                result = await classifier.ClassifyAsync(
                    new NewsClassificationRequest(title, body, article.Tickers), ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = new NewsClassificationResult(null, options.Model, 0, true);
            }
            catch (Exception exception)
            {
                diagnostics.PollFailed("news-classify", exception);
                result = new NewsClassificationResult(null, options.Model, 0, false);
            }

            if (!result.Available)
            {
                // Ollama에 닿지 못했다. 기사를 소비하지 않고 다음 주기로 미룬다.
                state.Ollama(false);
                Requeue(entry);
                break;
            }

            state.Ollama(true);
            _classifications.Enqueue(clock.GetUtcNow());
            var classifiedEntry = entry with { Article = article };
            var classificationText = string.IsNullOrWhiteSpace(body) ? title : title + "\n" + body;
            var record = Compose(classifiedEntry, result, inputKind, classificationText, classificationSource);
            await SaveAsync(record, ct);
            await SaveGroupFollowersAsync(article.Id, result, record, ct);
        }
        state.QueueDepth(QueueDepth);
    }

    static string JoinEvidence(params string?[] values)
        => string.Join("\n", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));

    /// <summary>대표가 분류되면 같은 그룹의 나머지 기사에 결과를 복사해 저장한다(#171 §3).</summary>
    async Task SaveGroupFollowersAsync(string representativeId, NewsClassificationResult result,
        NewsRecord representative, CancellationToken ct)
    {
        if (!_pendingFollowers.Remove(representativeId, out var followers)) return;
        var copied = result with { LatencyMs = 0 };
        foreach (var follower in followers)
        {
            var inputKind = follower.Article.HeadlineOnly || string.IsNullOrWhiteSpace(follower.Article.Summary)
                ? NewsInputKinds.Headline
                : NewsInputKinds.Body;
            await SaveAsync(Compose(follower, copied, inputKind, representative.ClassificationText,
                "representative", representativeId), ct);
        }
    }

    NewsRecord Compose(QueuedArticle entry, NewsClassificationResult result, string inputKind,
        string classificationText = "", string classificationSource = "", string? classifiedFrom = null)
    {
        var classification = result.Classification;
        // 피드 `tickers` 태그가 있으면 그것을 심볼로 쓰고, 없을 때만 LLM 판정을 쓴다.
        var symbols = entry.Article.Tickers.Count > 0
            ? entry.Article.Tickers.Select(x => x.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray()
            : classification?.Symbols.ToArray() ?? [];
        if (symbols.Length == 0) symbols = [NewsSymbols.Market];
        var relevance = entry.Article.Relevance ?? entry.Prior?.Relevance;
        if (classification?.Irrelevant == true && relevance is not { Included: true })
            relevance = new NewsRelevanceAssessment(result.PromptVersion, NewsRelevanceDecisions.Exclude, "non_market", "", "", [],
                classificationText[..Math.Min(320, classificationText.Length)], "llm_irrelevant", "llm", result.LatencyMs);

        return new NewsRecord(
            entry.Article.Id,
            entry.Article.Title,
            entry.Article.Source,
            entry.Article.CreatedAt,
            entry.Article.Tickers,
            entry.MatchedSymbols,
            symbols,
            classification?.Sentiment ?? NewsSentiments.Unclassified,
            classification?.Strength ?? 0,
            classification?.Reason ?? "",
            result.Model,
            result.LatencyMs,
            clock.GetUtcNow(),
            inputKind,
            result.PromptVersion,
            classifiedFrom, entry.Article.Entities, ImpactScores: classification?.ImpactScores,
            Summary: entry.Article.Summary, Content: entry.Article.Content,
            Url: entry.Article.Url,
            CollectedAt: entry.Prior?.CollectedAt ?? entry.CollectedAt,
            EvidenceSource: classifiedFrom is not null ? "representative" : inputKind == NewsInputKinds.Headline ? "headline"
                : classificationSource.Contains("summary", StringComparison.Ordinal) || classificationSource.Contains("excerpt", StringComparison.Ordinal)
                    ? "excerpt" : "body",
            ClassificationText: classificationText, ClassificationSource: classificationSource, EvidenceArticleId: classifiedFrom ?? entry.Article.Id,
            PublishedAtStatus: entry.Article.CreatedAt == DateTimeOffset.MinValue ? "unknown" : entry.Article.CreatedAt > clock.GetUtcNow().AddMinutes(5) ? "future" : "known",
            Relevance: relevance);
    }

    async Task SaveAsync(NewsRecord record, CancellationToken ct)
    {
        var file = DayFile(clock.GetUtcNow());
        if (_dayFile != file)
        {
            _dayFile = file;
            _dayBytes = await store.SizeAsync(file, ct);
        }
        var line = JsonSerializer.Serialize(record, Json);
        var size = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        if (_dayBytes + size > options.MaxDailyBytes)
        {
            state.Add(record, options.RecentCapacity);
            state.Limited(true);
            return;
        }
        await store.AppendAsync(file, line, ct);
        _dayBytes += size;
        state.Add(record, options.RecentCapacity);
        state.Limited(false);
        await MarkInboxProcessedAsync(record.Id, ct);
    }

    bool Allowed()
    {
        var now = clock.GetUtcNow();
        while (_classifications.Count > 0 && now - _classifications.Peek() >= TimeSpan.FromMinutes(1)) _classifications.Dequeue();
        return _classifications.Count < Math.Max(1, options.MaxClassificationsPerMinute);
    }

    async Task<NewsFeedState?> LoadStateAsync(CancellationToken ct)
    {
        if (_stateLoaded) return _state;
        _stateLoaded = true;
        try
        {
            var text = await store.ReadTextAsync(StateFile, ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _state = JsonSerializer.Deserialize<NewsFeedState>(text, Json);
                RestoreFeedRequestTimes(_state?.FeedRequestTimes);
            }
        }
        catch (Exception exception) { diagnostics.PollFailed("news-state", exception); }
        return _state;
    }

    async Task SaveStateAsync(NewsFeedState next, CancellationToken ct)
    {
        _state = next;
        await store.WriteTextAsync(StateFile, JsonSerializer.Serialize(next, Json), ct);
    }

    async Task MarkInboxProcessedAsync(string id, CancellationToken ct)
    {
        if (_state?.Inbox is null) return;
        var changed = false;
        var inbox = _state.Inbox.Select(x =>
        {
            if (!x.Processed && string.Equals(x.Item.Id, id, StringComparison.Ordinal))
            {
                changed = true;
                return x with { Processed = true };
            }
            return x;
        }).ToArray();
        if (changed) await SaveStateAsync(_state with { Inbox = inbox }, ct);
    }

    async Task<IReadOnlyList<NewsWatchSymbol>> WatchlistAsync(CancellationToken ct)
    {
        _ = ct;
        var items = await localStore.Read("watchlist.json", new List<WatchItem>());
        return items.Select(x => new NewsWatchSymbol(x.Symbol, x.Name ?? "")).ToArray();
    }

    static string DayFile(DateTimeOffset at) => at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";

    static long ParseId(string id)
        => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    static bool IsAfter(DateTimeOffset createdAt, long id, string key,
        DateTimeOffset? watermarkAt, long watermarkId, string? watermarkKey)
    {
        if (watermarkAt is null) return true;
        var time = createdAt.CompareTo(watermarkAt.Value);
        if (time != 0) return time > 0;
        if (id != 0 && watermarkId != 0 && id != watermarkId) return id > watermarkId;
        return string.CompareOrdinal(key, watermarkKey ?? string.Empty) > 0;
    }

    static IReadOnlyList<string> IdentityKeys(NewsFeedItem item, string fallbackProvider)
    {
        var provider = string.IsNullOrWhiteSpace(item.Provider) ? fallbackProvider : item.Provider;
        var keys = new List<string> { "provider:" + provider.Trim().ToLowerInvariant() + ":" + item.Id.Trim() };
        var canonicalUrl = CanonicalUrl(item.Url);
        if (canonicalUrl is not null) keys.Add("url:" + canonicalUrl);
        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static string? CanonicalUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length > 0 && !x[0].StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                && !x[0].Equals("fbclid", StringComparison.OrdinalIgnoreCase)
                && !x[0].Equals("gclid", StringComparison.OrdinalIgnoreCase))
            .Select(x => string.Join("=", x)).Order(StringComparer.Ordinal).ToArray();
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Fragment = "",
            Query = string.Join("&", query)
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    bool ReserveFeedRequest(DateTimeOffset now)
    {
        Trim(_dailyFeedRequests, now, TimeSpan.FromHours(24));
        Trim(_minuteFeedRequests, now, TimeSpan.FromMinutes(1));
        if (_state?.RequestProvider == feed.Name && _state.LastFeedRequestAt is { } last
            && now - last < feed.MinimumInterval) return false;
        var dailyLimit = Math.Min(Math.Max(1, options.MaxDailyFeedRequests), Math.Max(1, feed.DailyRequestLimit));
        if (_dailyFeedRequests.Count >= dailyLimit
            || _minuteFeedRequests.Count >= Math.Max(1, options.MaxFeedRequestsPerMinute)) return false;
        _dailyFeedRequests.Enqueue(now);
        _minuteFeedRequests.Enqueue(now);
        return true;
    }

    void RestoreFeedRequestTimes(IReadOnlyList<DateTimeOffset>? times)
    {
        if (times is null) return;
        if (_state?.RequestProvider is { } provider && !string.Equals(provider, feed.Name, StringComparison.Ordinal)) return;
        var now = clock.GetUtcNow();
        foreach (var time in times.Order())
        {
            if (now - time < TimeSpan.FromHours(24)) _dailyFeedRequests.Enqueue(time);
            if (now - time < TimeSpan.FromMinutes(1)) _minuteFeedRequests.Enqueue(time);
        }
    }

    static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now, TimeSpan window)
    {
        while (queue.Count > 0 && now - queue.Peek() >= window) queue.Dequeue();
    }
}
