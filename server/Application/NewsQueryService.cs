using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>
/// 뉴스 조회(#151 §6). 읽기 전용이며 조회가 피드·LLM을 호출하지 않는다.
/// 심볼은 관심종목에 한정하지 않는다 — 필터는 클라이언트가 한다.
/// </summary>
public sealed class NewsQueryService(NewsOptions options, NewsRuntimeState state, TimeProvider clock,
    NewsTranslationQueue? translations = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public object Articles(string? symbol, int? limit)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = state.Recent().OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id, StringComparer.Ordinal).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var wanted = symbol.Trim().TrimStart('$');
            rows = rows.Where(x => x.Symbols.Concat(x.MatchedSymbols)
                .Any(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase)));
        }
        var articles = rows.Take(take).Select(Project).ToArray();
        return new { enabled = options.Enabled, count = articles.Length, articles };
    }

    public object Sentiment()
    {
        var now = clock.GetUtcNow();
        var recent = state.Recent();
        var inputs = recent
            .Where(x => NewsSentiments.IsKnown(x.Sentiment))
            .SelectMany(x => x.Symbols.Select(s => new NewsSentimentInput(s, x.Sentiment, x.Strength, x.CreatedAt)));
        var scores = NewsSentimentDecay.Score(inputs, now, options.HalfLifeMinutes);
        var market = scores.FirstOrDefault(x => string.Equals(x.Symbol, NewsSymbols.Market, StringComparison.OrdinalIgnoreCase));
        var snapshotId = SnapshotId(now);
        return new
        {
            enabled = options.Enabled,
            asOf = now,
            snapshotId,
            halfLifeMinutes = options.HalfLifeMinutes,
            market = market is null ? null : Project(market, recent, now, snapshotId),
            symbols = scores.Where(x => x != market).Select(x => Project(x, recent, now, snapshotId)).ToArray(),
            sectors = recent.SelectMany(x => x.Entities ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Industry))
                .GroupBy(x => x.Industry, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { sector = g.Key, count = g.Count(), symbols = g.Select(x => x.Symbol).Where(s => s.Length > 0).Distinct().ToArray() })
                .OrderByDescending(x => x.count).ToArray(),
        };
    }

    public object Health() => new
    {
        enabled = options.Enabled,
        feed = options.UseSbhNews ? NewsFeedProviders.SbhNews
            : options.UseFoxNewsRss ? NewsFeedProviders.FoxNewsRss
            : options.UseSaveTicker ? "saveticker"
            : string.IsNullOrWhiteSpace(options.MarketauxApiKey) ? "rss" : "marketaux",
        lastPollAt = state.LastPollAt,
        lastAttemptAt = state.LastAttemptAt,
        lastSuccessAt = state.LastSuccessAt,
        lastFetchAt = state.LastFetchAt,
        lastNewArticleAt = state.LastNewArticleAt,
        latestPublishedAt = state.LatestPublishedAt,
        lastError = state.LastError,
        feedStatus = state.FeedStatus,
        providers = state.Providers,
        queue = state.Queue,
        translationQueue = translations?.QueueDepth ?? 0,
        relevanceAdjudication = new { status = state.RelevanceAdjudicationStatus,
            reason = state.RelevanceAdjudicationReason, queue = state.RelevanceAdjudicationQueue },
        dropped = state.Dropped,
        seen = state.Seen,
        classified = state.Classified,
        storageLimited = state.StorageLimited,
        ollama = state.OllamaOk ? "ok" : "down",
        promptVersion = NewsPromptVersions.V2c,
    };

    public object? Detail(string id, string? symbol = null)
    {
        var record = state.Find(id);
        if (record is null) return null;
        var now = clock.GetUtcNow();
        var selectedSymbol = SelectEvidenceSymbol(record, symbol);
        var snapshot = selectedSymbol is null ? null : BuildEvidence(selectedSymbol, state.Recent(), now);
        var provenance = !string.IsNullOrWhiteSpace(record.ClassifiedFrom) ? state.Find(record.ClassifiedFrom) ?? record : record;
        return new
        {
            id = record.Id, title = record.Title, titleKo = record.TitleKo,
            source = record.Source, sourceKo = record.SourceKo, url = record.Url,
            summary = record.Summary, body = record.Content, content = record.Content,
            summaryKo = record.SummaryKo, contentKo = record.ContentKo,
            translationStatus = record.TranslationStatus,
            titleTranslationStatus = record.TitleTranslationStatus,
            summaryTranslationStatus = record.SummaryTranslationStatus,
            contentTranslationStatus = record.ContentTranslationStatus,
            classificationTranslationStatus = record.ClassificationTranslationStatus,
            createdAt = record.CreatedAt, collectedAt = record.CollectedAt,
            publishedAtStatus = record.PublishedAtStatus,
            inputKind = record.InputKind, evidenceSource = record.EvidenceSource,
            classificationText = string.IsNullOrWhiteSpace(record.ClassificationText) ? provenance.ClassificationText : record.ClassificationText,
            classificationTextKo = record.ClassificationTextKo ?? provenance.ClassificationTextKo,
            classificationSource = record.ClassificationSource,
            evidenceArticleId = record.EvidenceArticleId ?? provenance.Id,
            classifiedFrom = record.ClassifiedFrom,
            sentiment = record.Sentiment, strength = record.Strength, reason = record.Reason,
            impactScores = record.ImpactScores, model = record.Model,
            promptVersion = record.PromptVersion, classifiedAt = record.ClassifiedAt,
            relevance = record.Relevance,
            evidenceSymbol = selectedSymbol,
            snapshotId = snapshot is null ? SnapshotId(now) : SnapshotId(now),
            asOf = now,
            score = snapshot?.Score,
            totalWeight = snapshot?.TotalWeight ?? 0,
            evidence = snapshot?.Top ?? [],
            remainingEvidenceCount = snapshot?.RemainingCount ?? 0,
            remainingContribution = snapshot?.RemainingContribution ?? 0,
            remainingWeight = snapshot?.RemainingWeight ?? 0
        };
    }

    public object? DetailByQuery(string? id, string? symbol = null)
        => string.IsNullOrWhiteSpace(id) ? null : Detail(id, symbol);

    public bool RequestTranslation(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || translations is null || state.Find(id) is not { } record) return false;
        translations.Enqueue(record);
        return true;
    }

    public object? Evidence(string id)
    {
        return Detail(id);
    }

    static object Project(NewsRecord record) => new
    {
        id = record.Id,
        title = record.Title,
        summary = record.Summary,
        content = record.Content,
        titleKo = record.TitleKo,
        source = record.Source,
        sourceKo = record.SourceKo,
        createdAt = record.CreatedAt,
        tickers = record.Tickers,
        matchedSymbols = record.MatchedSymbols,
        symbols = record.Symbols,
        sentiment = record.Sentiment,
        strength = record.Strength,
        reason = record.Reason,
        impactScores = record.ImpactScores,
        model = record.Model,
        latencyMs = record.LatencyMs,
        classifiedAt = record.ClassifiedAt,
        entities = record.Entities,
        inputKind = record.InputKind,
        promptVersion = record.PromptVersion,
        relevance = record.Relevance,
        classifiedFrom = record.ClassifiedFrom,
        url = record.Url,
        collectedAt = record.CollectedAt,
        evidenceSource = record.EvidenceSource,
        translationStatus = record.TranslationStatus,
        classificationText = record.ClassificationText,
        classificationSource = record.ClassificationSource,
        evidenceArticleId = record.EvidenceArticleId,
        summaryKo = record.SummaryKo,
        contentKo = record.ContentKo,
        publishedAtStatus = record.PublishedAtStatus,
        titleTranslationStatus = record.TitleTranslationStatus,
        summaryTranslationStatus = record.SummaryTranslationStatus,
        contentTranslationStatus = record.ContentTranslationStatus,
        classificationTranslationStatus = record.ClassificationTranslationStatus,
        classificationTextKo = record.ClassificationTextKo,
    };

    object Project(NewsSentimentScore score, IReadOnlyList<NewsRecord> records, DateTimeOffset now, string snapshotId)
    {
        var evidence = BuildEvidence(score.Symbol, records, now);
        return new
        {
            symbol = score.Symbol,
            score = score.Score,
            count = score.Count,
            latestAt = score.LatestAt,
            weight = score.Weight,
            totalWeight = evidence.TotalWeight,
            snapshotId,
            asOf = now,
            evidence = evidence.Top,
            remainingEvidenceCount = evidence.RemainingCount,
            remainingContribution = evidence.RemainingContribution,
            remainingWeight = evidence.RemainingWeight
        };
    }

    sealed record EvidenceItem(string Id, string Title, string? TitleKo, string Source, string? SourceKo,
        string Sentiment, int Strength, double Weight, double Contribution, DateTimeOffset CreatedAt);
    sealed record EvidenceSnapshot(double Score, double TotalWeight, IReadOnlyList<EvidenceItem> Top,
        int RemainingCount, double RemainingContribution, double RemainingWeight);

    EvidenceSnapshot BuildEvidence(string symbol, IReadOnlyList<NewsRecord> records, DateTimeOffset now)
    {
        var candidates = records.Where(x => NewsSentiments.IsKnown(x.Sentiment)
                && x.Symbols.Any(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase)))
            .Select(x => new
            {
                Record = x,
                Weight = NewsSentimentDecay.DecayWeight(now - x.CreatedAt, options.HalfLifeMinutes)
            }).ToArray();
        var totalWeight = candidates.Sum(x => x.Weight);
        if (totalWeight <= 0) return new EvidenceSnapshot(0, 0, [], 0, 0, 0);
        var score = NewsSentimentDecay.Score(candidates.Select(x => new NewsSentimentInput(symbol,
            x.Record.Sentiment, x.Record.Strength, x.Record.CreatedAt)), now, options.HalfLifeMinutes)
            .FirstOrDefault()?.Score ?? 0;
        var all = candidates.Select(x => new EvidenceItem(x.Record.Id, x.Record.Title, x.Record.TitleKo,
                x.Record.Source, x.Record.SourceKo, x.Record.Sentiment, x.Record.Strength,
                Round6(x.Weight), Round6(NewsSentiments.Sign(x.Record.Sentiment) * x.Record.Strength * x.Weight / totalWeight),
                x.Record.CreatedAt))
            .OrderByDescending(x => Math.Abs(x.Contribution)).ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (all.Count > 0)
        {
            var weightDrift = Round6(Round6(totalWeight) - all.Sum(x => x.Weight));
            if (weightDrift != 0) all[^1] = all[^1] with { Weight = Round6(all[^1].Weight + weightDrift) };
            var drift = Round6(score - all.Sum(x => x.Contribution));
            if (drift != 0) all[^1] = all[^1] with { Contribution = Round6(all[^1].Contribution + drift) };
        }
        var top = all.Take(5).ToArray();
        var remaining = all.Skip(5).ToArray();
        return new EvidenceSnapshot(score, Round6(totalWeight), top, remaining.Length,
            Round6(remaining.Sum(x => x.Contribution)), Round6(remaining.Sum(x => x.Weight)));
    }

    static string? SelectEvidenceSymbol(NewsRecord record, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var normalized = requested.Trim().TrimStart('$');
            if (record.Symbols.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                return normalized.ToUpperInvariant();
        }
        return record.Symbols.FirstOrDefault() ?? record.MatchedSymbols.FirstOrDefault();
    }

    static string SnapshotId(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    static double Round6(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
