using System.Globalization;
using System.Text.Json;
using Astra.Server.Domain;
using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>증분 상태(#151 §1). 첫 기동은 기준점만 저장하고 과거 기사를 분류하지 않는다.</summary>
public sealed record NewsFeedState(long LastId, DateTimeOffset? LastCreatedAt);

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
    TimeProvider clock)
{
    public const string StateFile = "state.json";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly LinkedList<QueuedArticle> _matched = new();
    readonly LinkedList<QueuedArticle> _other = new();
    readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    readonly Queue<DateTimeOffset> _classifications = new();

    /// <summary>대표 기사 id -> 같은 사건 그룹의 나머지 기사(#171). 대표가 분류되면 함께 저장한다.</summary>
    readonly Dictionary<string, List<QueuedArticle>> _pendingFollowers = new(StringComparer.Ordinal);

    NewsFeedState? _state;
    bool _stateLoaded;
    bool _restored;
    string? _dayFile;
    long _dayBytes;

    sealed record QueuedArticle(NewsArticle Article, IReadOnlyList<string> MatchedSymbols);

    public int QueueDepth { get { lock (_queued) return _matched.Count + _other.Count; } }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        state.PollStarted(clock.GetUtcNow());
        await _gate.WaitAsync(ct);
        try
        {
            var budget = Math.Max(1, options.MaxFeedRequestsPerMinute);
            await RestoreAsync(ct);
            budget = await CollectAsync(budget, ct);
            await DrainAsync(budget, ct);
            state.QueueDepth(QueueDepth);
            state.PollCompleted(clock.GetUtcNow());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { diagnostics.PollFailed("news", exception); state.PollFailed(clock.GetUtcNow(), exception.Message); }
        finally { _gate.Release(); }
    }

    /// <summary>재기동 후에도 당일 판정을 조회에 보이게 today jsonl을 한 번만 되읽는다.</summary>
    async Task RestoreAsync(CancellationToken ct)
    {
        if (_restored) return;
        _restored = true;
        try
        {
            var file = DayFile(clock.GetUtcNow());
            var lines = await store.ReadLinesAsync(file, ct);
            foreach (var line in lines.TakeLast(options.RecentCapacity))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var record = JsonSerializer.Deserialize<NewsRecord>(line, Json);
                if (record is not null) state.Add(record, options.RecentCapacity);
            }
        }
        catch (Exception exception) { diagnostics.PollFailed("news-restore", exception); }
    }

    async Task<int> CollectAsync(int budget, CancellationToken ct)
    {
        if (budget <= 0) return budget;
        var watchlist = await WatchlistAsync(ct);
        var known = await LoadStateAsync(ct);

        var fresh = new List<NewsFeedItem>();
        var page = 1;
        var maxId = known?.LastId ?? 0;
        DateTimeOffset? maxCreatedAt = known?.LastCreatedAt;

        while (page <= Math.Max(1, options.MaxPages) && budget > 0)
        {
            IReadOnlyList<NewsFeedItem> items;
            try { items = await feed.ListAsync(page, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (Exception exception) { diagnostics.PollFailed("news-feed", exception); break; }
            finally { budget--; }

            if (items.Count == 0) break;
            foreach (var item in items)
            {
                var id = ParseId(item.Id);
                if (id > maxId) maxId = id;
                if (maxCreatedAt is null || item.CreatedAt > maxCreatedAt) maxCreatedAt = item.CreatedAt;
            }

            if (known is null) break;
            var newer = items.Where(x => ParseId(x.Id) > known.LastId).ToArray();
            fresh.AddRange(newer);
            // 신규가 한 페이지를 가득 채웠을 때만 더 과거 페이지를 본다.
            if (newer.Length < items.Count) break;
            page++;
        }

        if (maxId > 0) await SaveStateAsync(new NewsFeedState(maxId, maxCreatedAt), ct);
        if (known is null || fresh.Count == 0) return budget;

        state.SeenArticles(fresh.Count);
        var articles = fresh.OrderBy(x => ParseId(x.Id))
            .Select(item => new NewsArticle(item.Id, item.Title, item.Summary, item.Source, item.CreatedAt,
                item.Tickers, item.Headline, item.HeadlineOnly, item.GroupId))
            .ToArray();

        var followerIds = GroupFollowerArticles(articles, watchlist);
        foreach (var article in articles)
        {
            if (followerIds.Contains(article.Id)) continue;
            Enqueue(article, NewsMatcher.Match(article, watchlist).Symbols);
        }
        state.QueueDepth(QueueDepth);
        return budget;
    }

    /// <summary>
    /// 사건 그룹 단위 1회 분류(#171 §3). <see cref="NewsArticle.GroupId"/>가 같은 신규 기사 중
    /// 그룹 내 최신 <see cref="NewsArticle.CreatedAt"/> 1건만 대표로 큐에 넣고, 나머지는
    /// <see cref="_pendingFollowers"/>에 쌓아 두었다가 대표가 분류되면 결과를 복사해 저장한다.
    /// </summary>
    HashSet<string> GroupFollowerArticles(IReadOnlyList<NewsArticle> articles, IReadOnlyList<NewsWatchSymbol> watchlist)
    {
        var followerIds = new HashSet<string>(StringComparer.Ordinal);
        var groups = articles
            .Where(a => !string.IsNullOrWhiteSpace(a.GroupId))
            .GroupBy(a => a.GroupId!, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var members = group.ToArray();
            if (members.Length < 2) continue;

            var representative = members
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => ParseId(a.Id))
                .First();
            var followers = members.Where(a => a.Id != representative.Id).ToArray();
            foreach (var follower in followers) followerIds.Add(follower.Id);

            _pendingFollowers[representative.Id] = followers
                .Select(a => new QueuedArticle(a, NewsMatcher.Match(a, watchlist).Symbols))
                .ToList();
        }
        return followerIds;
    }

    /// <summary>큐 상한 초과 시 비매칭 기사부터 버린다(사용자 요구: 매칭 우선).</summary>
    void Enqueue(NewsArticle article, IReadOnlyList<string> matchedSymbols)
    {
        lock (_queued)
        {
            if (!_queued.Add(article.Id)) return;
            var entry = new QueuedArticle(article, matchedSymbols);
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

            var body = entry.Article.Summary;
            var inputKind = NewsInputKinds.Body;
            // 상세는 관심종목 매칭 기사에만, 남은 피드 요청 예산 안에서 받는다(#151 §1).
            if (entry.MatchedSymbols.Count > 0 && feedBudget > 0)
            {
                feedBudget--;
                try
                {
                    var detail = await feed.DetailAsync(entry.Article.Id, ct);
                    // 사용자 요구: AI 요약이 있으면 본문 대신 요약만 쓴다.
                    if (!string.IsNullOrWhiteSpace(detail?.Summary)) { body = detail!.Summary; inputKind = NewsInputKinds.Summary; }
                    else if (!string.IsNullOrWhiteSpace(detail?.Body)) body = detail!.Body;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception exception) { diagnostics.PollFailed("news-detail", exception); }
            }
            if (inputKind == NewsInputKinds.Body && (entry.Article.HeadlineOnly || string.IsNullOrWhiteSpace(body)))
            {
                inputKind = NewsInputKinds.Headline;
                body = "";
            }

            var title = string.IsNullOrWhiteSpace(entry.Article.Headline)
                ? entry.Article.Title
                : entry.Article.Title + "\n" + entry.Article.Headline;

            NewsClassificationResult result;
            try
            {
                result = await classifier.ClassifyAsync(
                    new NewsClassificationRequest(title, body, entry.Article.Tickers), ct);
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
            await SaveAsync(Compose(entry, result, inputKind), ct);
            await SaveGroupFollowersAsync(entry.Article.Id, result, ct);
        }
        state.QueueDepth(QueueDepth);
    }

    /// <summary>대표가 분류되면 같은 그룹의 나머지 기사에 결과를 복사해 저장한다(#171 §3).</summary>
    async Task SaveGroupFollowersAsync(string representativeId, NewsClassificationResult result, CancellationToken ct)
    {
        if (!_pendingFollowers.Remove(representativeId, out var followers)) return;
        var copied = result with { LatencyMs = 0 };
        foreach (var follower in followers)
        {
            var inputKind = follower.Article.HeadlineOnly || string.IsNullOrWhiteSpace(follower.Article.Summary)
                ? NewsInputKinds.Headline
                : NewsInputKinds.Body;
            await SaveAsync(Compose(follower, copied, inputKind, representativeId), ct);
        }
    }

    NewsRecord Compose(QueuedArticle entry, NewsClassificationResult result, string inputKind, string? classifiedFrom = null)
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
            classifiedFrom);
    }

    async Task SaveAsync(NewsRecord record, CancellationToken ct)
    {
        state.Add(record, options.RecentCapacity);
        var file = DayFile(clock.GetUtcNow());
        if (_dayFile != file)
        {
            _dayFile = file;
            _dayBytes = await store.SizeAsync(file, ct);
        }
        var line = JsonSerializer.Serialize(record, Json);
        var size = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
        if (_dayBytes + size > options.MaxDailyBytes) { state.Limited(true); return; }
        await store.AppendAsync(file, line, ct);
        _dayBytes += size;
        state.Limited(false);
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
            if (!string.IsNullOrWhiteSpace(text)) _state = JsonSerializer.Deserialize<NewsFeedState>(text, Json);
        }
        catch (Exception exception) { diagnostics.PollFailed("news-state", exception); }
        return _state;
    }

    async Task SaveStateAsync(NewsFeedState next, CancellationToken ct)
    {
        if (_state is not null && next.LastId <= _state.LastId) return;
        _state = next;
        await store.WriteTextAsync(StateFile, JsonSerializer.Serialize(next, Json), ct);
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
}
