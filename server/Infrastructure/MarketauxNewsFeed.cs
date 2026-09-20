using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

/// <summary>Marketaux 전체 시장 피드 어댑터. symbols 없이 전체 기사를 수집한다.</summary>
public sealed class MarketauxNewsFeed : INewsFeed
{
    readonly NewsOptions _options;
    readonly HttpClient _http;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public MarketauxNewsFeed(NewsOptions options, HttpClient? http = null)
    { _options = options; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }; }

    public async Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.MarketauxApiKey)) return [];
        // 엔터티 없는 거시·시장 기사도 MARKET으로 분류해야 하므로 전체 피드를 유지한다.
        var uri = $"{_options.MarketauxUrl}?api_token={Uri.EscapeDataString(_options.MarketauxApiKey)}&language=en&limit=100&page={page.ToString(CultureInfo.InvariantCulture)}";
        var payload = await _http.GetFromJsonAsync<Payload>(uri, Json, ct);
        return payload?.Data?.Where(x => !string.IsNullOrWhiteSpace(x.Uuid)).Select(Map).ToArray() ?? [];
    }

    public Task<NewsDetail?> DetailAsync(string id, CancellationToken ct) => Task.FromResult<NewsDetail?>(null);

    static NewsFeedItem Map(Item x)
    {
        var entities = x.Entities?.Where(e => !string.IsNullOrWhiteSpace(e.Symbol) || !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => new NewsEntity((e.Symbol ?? "").ToUpperInvariant(), e.Name ?? "", e.Industry ?? "", e.SentimentScore, e.MatchScore)).ToArray() ?? [];
        var tickers = entities.Where(e => e.Symbol.Length > 0).Select(e => e.Symbol).Distinct(StringComparer.Ordinal).ToArray();
        return new NewsFeedItem(x.Uuid!, x.Title ?? "", x.Description ?? "", x.Source ?? "", x.PublishedAt ?? DateTimeOffset.UtcNow, tickers, Entities: entities);
    }

    sealed record Payload(List<Item>? Data);
    sealed record Item(
        string? Uuid, string? Title, string? Description, string? Source,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        List<Entity>? Entities);
    sealed record Entity(string? Symbol, string? Name, string? Industry,
        [property: JsonPropertyName("sentiment_score")] double? SentimentScore,
        [property: JsonPropertyName("match_score")] double? MatchScore);
}
