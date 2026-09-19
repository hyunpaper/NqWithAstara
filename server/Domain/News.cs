using System.Globalization;
using System.Text.Json;

namespace Astra.Server.Domain.News;

public sealed record NewsEntity(string Symbol, string Name, string Industry, double? SentimentScore, double? MatchScore);

/// <summary>감성 분류 라벨(#151). 판정 실패는 <see cref="Unclassified"/>로 남긴다.</summary>
public static class NewsSentiments
{
    public const string Positive = "positive";
    public const string Negative = "negative";
    public const string Neutral = "neutral";
    public const string Unclassified = "unclassified";

    public static bool IsKnown(string? value)
        => value is Positive or Negative or Neutral;

    public static int Sign(string? sentiment) => sentiment switch
    {
        Positive => 1,
        Negative => -1,
        _ => 0,
    };
}

/// <summary>종목 태그가 없는 거시·시장 뉴스를 모으는 의사 심볼(#151).</summary>
public static class NewsSymbols
{
    public const string Market = "MARKET";
}

/// <summary>분류 프롬프트 버전(#171). 저장 레코드·health에 그대로 노출한다.</summary>
public static class NewsPromptVersions
{
    public const string V2b = "v2b";
}

/// <summary>
/// 매칭·분류 입력으로 쓰는 기사 최소 형태(#151). Headline은 피드가 붙인 AI 헤드라인이다.
/// <see cref="GroupId"/>가 같은 신규 기사는 사건 그룹으로 묶여 대표 1건만 분류된다(#171).
/// </summary>
public sealed record NewsArticle(
    string Id,
    string Title,
    string Summary,
    string Source,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tickers,
    string Headline = "",
    bool HeadlineOnly = false,
    string? GroupId = null,
    IReadOnlyList<NewsEntity>? Entities = null);

/// <summary>분류 입력으로 무엇을 썼는지(#151). 판정 근거를 사후에 되짚기 위해 레코드에 남긴다.</summary>
public static class NewsInputKinds
{
    /// <summary>상세의 AI 요약(`translations.translated.ko_KR.summary`).</summary>
    public const string Summary = "summary";

    /// <summary>상세 본문 블록 또는 목록 요약.</summary>
    public const string Body = "body";

    /// <summary>제목(과 AI 헤드라인)만 있는 속보.</summary>
    public const string Headline = "headline";
}

/// <summary>관심종목 매칭 결과(#151 §2). 매칭 여부는 저장이 아니라 큐 우선순위를 정한다.</summary>
public sealed record NewsMatch(IReadOnlyList<string> Symbols)
{
    public bool Matched => Symbols.Count > 0;
}

/// <summary>
/// 관심종목 매칭(#151 §2). 피드 `tickers` 태그를 우선하고, 없으면 제목·요약에서
/// `$TICKER`·영문명·한글 별칭을 찾는다. 외부 상태를 읽지 않는 순수 함수다.
/// </summary>
public static class NewsMatcher
{
    const int MinimumNameLength = 3;
    const int MinimumAliasLength = 2;

    public static NewsMatch Match(NewsArticle article, IReadOnlyList<NewsWatchSymbol> watchlist)
    {
        if (watchlist.Count == 0) return new NewsMatch([]);

        var tagged = new List<string>();
        foreach (var watch in watchlist)
        {
            if (article.Tickers.Any(t => Same(t, watch.Symbol))) tagged.Add(watch.Symbol);
        }
        if (tagged.Count > 0) return new NewsMatch(Distinct(tagged));

        var text = article.Title + "\n" + article.Headline + "\n" + article.Summary;
        if (string.IsNullOrWhiteSpace(text)) return new NewsMatch([]);

        var found = new List<string>();
        foreach (var watch in watchlist)
        {
            if (MatchesText(text, watch)) found.Add(watch.Symbol);
        }
        return new NewsMatch(Distinct(found));
    }

    static bool MatchesText(string text, NewsWatchSymbol watch)
    {
        if (ContainsCashTag(text, watch.Symbol)) return true;
        if (watch.Name.Length >= MinimumNameLength && text.Contains(watch.Name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var alias in Astra.Server.SymbolAliases.AliasesFor(watch.Symbol))
        {
            if (alias.Length >= MinimumAliasLength && text.Contains(alias, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>`$INTC` 형태만 티커로 본다. 일반 영문 단어가 티커로 오인되지 않게 한다.</summary>
    static bool ContainsCashTag(string text, string symbol)
    {
        var tag = "$" + symbol;
        var index = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var after = index + tag.Length;
            if (after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '.')) return true;
            index = text.IndexOf(tag, after, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static IReadOnlyList<string> Distinct(List<string> symbols)
        => symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>매칭 대상 관심종목(심볼·영문명). Application이 watchlist에서 만들어 넘긴다(#151).</summary>
public sealed record NewsWatchSymbol(string Symbol, string Name);

/// <summary>감쇠 점수 입력 한 건(#151 §4).</summary>
public sealed record NewsSentimentInput(string Symbol, string Sentiment, int Strength, DateTimeOffset At);

/// <summary>심볼별 감성 점수(#151 §4, #157). Weight는 최근성 가중치 합(Σw)이며 표시용이다.</summary>
public sealed record NewsSentimentScore(string Symbol, double Score, int Count, DateTimeOffset LatestAt, double Weight);

/// <summary>
/// 반감기 가중 평균 감성 점수(#157): Σ(sign×strength×w)/Σw, w = 0.5^(age/HalfLife).
/// 유입 속도가 빨라도 항상 -5..+5 범위 안에 자연히 들어오므로 클램프가 필요 없다.
/// </summary>
public static class NewsSentimentDecay
{
    public const double DefaultHalfLifeMinutes = 30;

    /// <summary>기사 하나의 최근성 가중치(부호·강도와 무관). 미래 타임스탬프는 age 0으로 본다.</summary>
    public static double DecayWeight(TimeSpan age, double halfLifeMinutes)
    {
        var half = halfLifeMinutes > 0 ? halfLifeMinutes : DefaultHalfLifeMinutes;
        var minutes = Math.Max(0, age.TotalMinutes);
        return Math.Pow(0.5, minutes / half);
    }

    sealed record Accumulator(double Numerator, double WeightSum, int Count, DateTimeOffset Latest);

    public static IReadOnlyList<NewsSentimentScore> Score(
        IEnumerable<NewsSentimentInput> inputs, DateTimeOffset now, double halfLifeMinutes)
    {
        var totals = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Symbol)) continue;
            var weight = DecayWeight(now - input.At, halfLifeMinutes);
            var sign = NewsSentiments.Sign(input.Sentiment);
            var current = totals.TryGetValue(input.Symbol, out var existing) ? existing : new Accumulator(0, 0, 0, DateTimeOffset.MinValue);
            totals[input.Symbol] = current with
            {
                Numerator = current.Numerator + sign * input.Strength * weight,
                WeightSum = current.WeightSum + weight,
                Count = current.Count + 1,
                Latest = input.At > current.Latest ? input.At : current.Latest,
            };
        }
        return totals
            .Select(x => new NewsSentimentScore(
                x.Key,
                Round(x.Value.WeightSum > 0 ? x.Value.Numerator / x.Value.WeightSum : 0),
                x.Value.Count,
                x.Value.Latest,
                Math.Round(x.Value.WeightSum, 2, MidpointRounding.AwayFromZero)))
            .OrderByDescending(x => Math.Abs(x.Score)).ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

/// <summary>로컬 LLM 분류 결과(#151 §3). 심볼은 관심종목에 한정하지 않는다.</summary>
public sealed record NewsClassification(IReadOnlyList<string> Symbols, string Sentiment, int Strength, string Reason);

/// <summary>
/// 분류 JSON 파서(#151 §3). 코드펜스·앞뒤 잡문을 제거하고 첫 JSON 객체만 읽는다.
/// 형식이 어긋나면 null을 돌려 호출자가 unclassified로 남기게 한다.
/// </summary>
public static class NewsClassificationParser
{
    public const int MaxReasonLength = 200;

    public static NewsClassification? TryParse(string? raw)
    {
        var json = ExtractObject(raw);
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var sentiment = Text(root, "sentiment")?.Trim().ToLowerInvariant();
            if (!NewsSentiments.IsKnown(sentiment)) return null;

            var symbols = Symbols(root);
            if (symbols.Count == 0) symbols = [NewsSymbols.Market];

            return new NewsClassification(symbols, sentiment!, Strength(root), Reason(root));
        }
        catch (JsonException) { return null; }
    }

    static IReadOnlyList<string> Symbols(JsonElement root)
    {
        if (!root.TryGetProperty("symbols", out var element)) return [];
        var values = new List<string>();
        if (element.ValueKind == JsonValueKind.String) values.Add(element.GetString() ?? "");
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) values.Add(item.GetString() ?? "");
            }
        }
        return values
            .Select(Normalize)
            .Where(x => x.Length is > 0 and <= 12)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    static string Normalize(string symbol)
        => symbol.Trim().TrimStart('$').Trim().ToUpperInvariant();

    static int Strength(JsonElement root)
    {
        if (!root.TryGetProperty("strength", out var element)) return 1;
        var value = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out var number) ? number : 1,
            JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1,
            _ => 1,
        };
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1, 5);
    }

    static string Reason(JsonElement root)
    {
        var reason = Text(root, "reason")?.Trim() ?? "";
        return reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
    }

    static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>코드펜스를 걷어내고 균형 잡힌 첫 JSON 객체를 잘라낸다.</summary>
    static string? ExtractObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "");
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
}
