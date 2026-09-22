using System.Globalization;
using System.Text.Json;
using Astra.Server.Domain;
using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>증분 상태(#151 §1). 첫 기동은 기준점만 저장하고 과거 기사를 분류하지 않는다.</summary>
public sealed record NewsInboxEntry(
    IReadOnlyList<string> Keys,
    NewsFeedItem Item,
    DateTimeOffset CollectedAt,
    bool Processed);

public sealed record NewsFeedState(long LastId, DateTimeOffset? LastCreatedAt,
    IReadOnlyList<DateTimeOffset>? FeedRequestTimes = null, string? LastKey = null,
    IReadOnlyList<string>? SeenIds = null,
    IReadOnlyList<NewsInboxEntry>? Inbox = null,
    string? RequestProvider = null,
    DateTimeOffset? LastFeedRequestAt = null);

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
    NewsTranslationQueue? translationQueue = null)
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
                if (record.TranslationStatus is "pending" or "failed" or "quota_wait" or "not_configured")
                    translationQueue?.Enqueue(record);
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
                record.Tickers, headlineOnly ? record.Title : "", headlineOnly, null, record.Entities, record.Content, record.InputKind, record.Url);
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
        QueuePendingInbox(known?.Inbox ?? [], watchlist);

        var fresh = new List<NewsInboxEntry>();
        var page = 1;
        var maxId = known?.LastId ?? 0;
        DateTimeOffset? maxCreatedAt = known?.LastCreatedAt;
        var maxKey = known?.LastKey;
        var seenIds = new HashSet<string>(known?.SeenIds ?? [], StringComparer.Ordinal);
        var inbox = new List<NewsInboxEntry>(known?.Inbox ?? []);
        var seenKeys = new HashSet<string>(inbox.SelectMany(x => x.Keys), StringComparer.Ordinal);
        var providerStatuses = new List<NewsProviderFetchStatus>();
        var status = "empty";
        var fetched = false;
        DateTimeOffset? latestPublishedAt = null;

        while (page <= Math.Max(1, options.MaxPages) && budget > 0)
        {
            var requestedAt = clock.GetUtcNow();
            if (!ReserveFeedRequest(requestedAt))
            {
                status = "quota_wait";
                break;
            }
            // 공급자 호출은 빈 응답이나 예외에서도 쿼터를 소비할 수 있다.
            // 호출 직후 시각을 저장해 재기동으로 일일 한도를 우회하지 않게 한다.
            await SaveStateAsync(new NewsFeedState(maxId, maxCreatedAt, _dailyFeedRequests.ToArray(), maxKey,
                seenIds.ToArray(), inbox, feed.Name, requestedAt), ct);
            NewsFeedBatch batch;
            try { batch = await feed.FetchAsync(page, ct); }
            finally { budget--; }
            fetched = true;
            status = batch.Status;
            var items = batch.Items;

            if (items.Count == 0)
            {
                providerStatuses.AddRange(batch.Providers);
                break;
            }
            var freshBefore = fresh.Count;
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

            foreach (var item in items)
            {
                var keys = IdentityKeys(item, feed.Name);
                var alreadySeen = keys.Any(seenKeys.Contains)
                    || (known?.Inbox is null && known?.SeenIds?.Contains(item.Id, StringComparer.Ordinal) == true);
                if (alreadySeen) continue;
                var entry = new NewsInboxEntry(keys, item, requestedAt, known is null);
                inbox.Add(entry);
                foreach (var key in keys) seenKeys.Add(key);
                if (known is not null)
                {
                    fresh.Add(entry);
                    var provider = string.IsNullOrWhiteSpace(item.Provider) ? feed.Name : item.Provider;
                    newByProvider[provider] = newByProvider.GetValueOrDefault(provider) + 1;
                }
            }
            providerStatuses.AddRange(batch.Providers.Select(x => x with
            {
                NewCount = newByProvider.GetValueOrDefault(x.Provider)
            }));
            if (known is null) break;
            if (fresh.Count - freshBefore < items.Count) break;
            var cutoff = requestedAt - TimeSpan.FromHours(24);
            if (!items.Any(x => x.CreatedAt == DateTimeOffset.MinValue || x.CreatedAt >= cutoff)) break;
            page++;
        }

        var now = clock.GetUtcNow();
        var inboxCutoff = now - TimeSpan.FromHours(Math.Max(24, options.InboxRetentionHours));
        inbox = inbox.Where(x => !x.Processed || x.CollectedAt >= inboxCutoff)
            .TakeLast(Math.Max(1, options.InboxCapacity)).ToList();
        if (maxId > 0 || maxCreatedAt is not null || inbox.Count > 0 || fetched)
            await SaveStateAsync(new NewsFeedState(maxId, maxCreatedAt, _dailyFeedRequests.ToArray(), maxKey,
                seenIds.TakeLast(Math.Max(options.InboxCapacity, 1)).ToArray(), inbox, feed.Name,
                fetched ? now : known?.LastFeedRequestAt), ct);
        if (known is null)
            return new CollectionResult(budget, fetched && status is "ok" or "empty" ? "baseline" : status,
                fetched, 0, latestPublishedAt, providerStatuses);
        if (fresh.Count == 0)
            return new CollectionResult(budget, status, fetched, 0, latestPublishedAt, providerStatuses);

        state.SeenArticles(fresh.Count);
        QueueInboxArticles(fresh, watchlist);
        state.QueueDepth(QueueDepth);
        var providerSummary = providerStatuses.GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NewsProviderFetchStatus(group.Key, group.Last().Status,
                group.Sum(x => x.Count), group.Sum(x => x.NewCount))).ToArray();
        return new CollectionResult(budget, status == "empty" ? "ok" : status, fetched, fresh.Count,
            latestPublishedAt, providerSummary);
    }

    void QueuePendingInbox(IReadOnlyList<NewsInboxEntry> entries, IReadOnlyList<NewsWatchSymbol> watchlist)
        => QueueInboxArticles(entries.Where(x => !x.Processed).ToArray(), watchlist);

    void QueueInboxArticles(IReadOnlyList<NewsInboxEntry> entries, IReadOnlyList<NewsWatchSymbol> watchlist)
    {
        var queued = entries.OrderBy(x => x.Item.CreatedAt).ThenBy(x => x.Item.Id, StringComparer.Ordinal)
            .Select(x => (Entry: x, Article: new NewsArticle(x.Item.Id, x.Item.Title, x.Item.Summary, x.Item.Source,
                x.Item.CreatedAt, x.Item.Tickers, x.Item.Headline, x.Item.HeadlineOnly, x.Item.GroupId,
                x.Item.Entities, x.Item.Content, Url: x.Item.Url))).ToArray();
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
            classifiedFrom, entry.Article.Entities, classification?.KoreanTitle,
            classification?.KoreanSource, classification?.ImpactScores,
            entry.Article.Summary, entry.Article.Content,
            entry.Article.Url,
            entry.Prior?.CollectedAt ?? entry.CollectedAt,
            classifiedFrom is not null ? "representative" : inputKind == NewsInputKinds.Headline ? "headline"
                : classificationSource.Contains("summary", StringComparison.Ordinal) || classificationSource.Contains("excerpt", StringComparison.Ordinal)
                    ? "excerpt" : "body",
            translationQueue is null ? entry.Prior?.TranslationStatus ?? "not_requested" : "pending",
            classificationText, classificationSource, classifiedFrom ?? entry.Article.Id,
            entry.Prior?.SummaryKo, entry.Prior?.ContentKo,
            entry.Article.CreatedAt == DateTimeOffset.MinValue ? "unknown" : entry.Article.CreatedAt > clock.GetUtcNow().AddMinutes(5) ? "future" : "known",
            translationQueue is null ? entry.Prior?.TitleTranslationStatus ?? "not_requested" : "pending",
            translationQueue is null ? entry.Prior?.SummaryTranslationStatus ?? "not_requested" : "pending",
            translationQueue is null ? entry.Prior?.ContentTranslationStatus ?? "not_requested" : "pending",
            translationQueue is null ? entry.Prior?.ClassificationTranslationStatus ?? "not_requested" : "pending",
            entry.Prior?.ClassificationTextKo,
            entry.Prior?.TranslationContentHash);
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
        translationQueue?.Enqueue(record);
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
