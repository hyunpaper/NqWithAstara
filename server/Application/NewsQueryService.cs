using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>
/// 뉴스 조회(#151 §6). 읽기 전용이며 조회가 피드·LLM을 호출하지 않는다.
/// 심볼은 관심종목에 한정하지 않는다 — 필터는 클라이언트가 한다.
/// </summary>
public sealed class NewsQueryService(NewsOptions options, NewsRuntimeState state, TimeProvider clock)
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
        return new
        {
            enabled = options.Enabled,
            asOf = now,
            halfLifeMinutes = options.HalfLifeMinutes,
            market = market is null ? null : Project(market),
            symbols = scores.Where(x => x != market).Select(Project).ToArray(),
            sectors = recent.SelectMany(x => x.Entities ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Industry))
                .GroupBy(x => x.Industry, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { sector = g.Key, count = g.Count(), symbols = g.Select(x => x.Symbol).Where(s => s.Length > 0).Distinct().ToArray() })
                .OrderByDescending(x => x.count).ToArray(),
        };
    }

    public object Health() => new
    {
        enabled = options.Enabled,
        feed = options.UseSaveTicker ? "saveticker" : (string.IsNullOrWhiteSpace(options.MarketauxApiKey) ? "rss" : "marketaux"),
        lastPollAt = state.LastPollAt,
        lastAttemptAt = state.LastAttemptAt,
        lastSuccessAt = state.LastSuccessAt,
        lastError = state.LastError,
        feedStatus = state.FeedStatus,
        queue = state.Queue,
        dropped = state.Dropped,
        seen = state.Seen,
        classified = state.Classified,
        storageLimited = state.StorageLimited,
        ollama = state.OllamaOk ? "ok" : "down",
        promptVersion = NewsPromptVersions.V2c,
    };

    public object? Detail(string id)
    {
        var record = state.Find(id);
        return record is null ? null : Project(record);
    }

    public object? DetailByQuery(string? id) => string.IsNullOrWhiteSpace(id) ? null : Detail(id);

    public object? Evidence(string id)
    {
        var record = state.Find(id);
        if (record is null) return null;
        var now = clock.GetUtcNow();
        var evidence = state.Recent()
            .Where(x => x.Symbols.Any(s => record.Symbols.Contains(s, StringComparer.OrdinalIgnoreCase)) && NewsSentiments.IsKnown(x.Sentiment))
            .Select(x => new {
                id = x.Id, title = x.Title, titleKo = x.TitleKo, source = x.Source, sourceKo = x.SourceKo,
                sentiment = x.Sentiment, strength = x.Strength,
                weight = NewsSentimentDecay.DecayWeight(now - x.CreatedAt, options.HalfLifeMinutes),
                contribution = NewsSentiments.Sign(x.Sentiment) * x.Strength * NewsSentimentDecay.DecayWeight(now - x.CreatedAt, options.HalfLifeMinutes),
                createdAt = x.CreatedAt
            }).OrderByDescending(x => Math.Abs(x.contribution)).ToArray();
        return new
        {
            id = record.Id, title = record.Title, titleKo = record.TitleKo,
            source = record.Source, sourceKo = record.SourceKo, url = record.Url,
            createdAt = record.CreatedAt, collectedAt = record.CollectedAt,
            inputKind = record.InputKind, evidenceSource = record.EvidenceSource,
            translationStatus = record.TranslationStatus, summary = record.Summary,
            content = record.Content, sentiment = record.Sentiment,
            strength = record.Strength, reason = record.Reason,
            impactScores = record.ImpactScores, model = record.Model,
            promptVersion = record.PromptVersion, classifiedAt = record.ClassifiedAt,
            classificationText = record.ClassificationText,
            classificationSource = record.ClassificationSource,
            evidenceArticleId = record.EvidenceArticleId,
            summaryKo = record.SummaryKo, contentKo = record.ContentKo,
            publishedAtStatus = record.PublishedAtStatus
            , evidence = evidence.Take(5), remainingEvidenceCount = Math.Max(0, evidence.Length - 5),
            remainingContribution = evidence.Skip(5).Sum(x => x.contribution)
        };
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
    };

    static object Project(NewsSentimentScore score) => new
    {
        symbol = score.Symbol,
        score = score.Score,
        count = score.Count,
        latestAt = score.LatestAt,
        weight = score.Weight,
    };
}
