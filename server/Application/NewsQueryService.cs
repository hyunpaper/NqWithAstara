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
        var rows = state.Recent().AsEnumerable();
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
        feed = string.IsNullOrWhiteSpace(options.MarketauxApiKey) ? "saveticker" : "marketaux",
        lastPollAt = state.LastPollAt,
        lastAttemptAt = state.LastAttemptAt,
        lastSuccessAt = state.LastSuccessAt,
        lastError = state.LastError,
        queue = state.Queue,
        dropped = state.Dropped,
        seen = state.Seen,
        classified = state.Classified,
        storageLimited = state.StorageLimited,
        ollama = state.OllamaOk ? "ok" : "down",
        promptVersion = NewsPromptVersions.V2c,
    };

    static object Project(NewsRecord record) => new
    {
        id = record.Id,
        title = record.Title,
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
