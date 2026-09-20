using System.Globalization;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

/// <summary>Marketaux 전체 시장 피드 어댑터. symbols 없이 전체 기사를 수집한다.</summary>
public sealed class MarketauxNewsFeed : INewsFeed
{
    public const int RssTextLimit = 1500;
    static readonly Regex HtmlTags = new("<[^>]*>", RegexOptions.Compiled);
    readonly NewsOptions _options;
    readonly HttpClient _http;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public MarketauxNewsFeed(NewsOptions options, HttpClient? http = null)
    { _options = options; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }; }

    public async Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.MarketauxApiKey)) return await GoogleRssAsync(ct);
        // 엔터티 없는 거시·시장 기사도 MARKET으로 분류해야 하므로 전체 피드를 유지한다.
        var uri = $"{_options.MarketauxUrl}?api_token={Uri.EscapeDataString(_options.MarketauxApiKey)}&language=en&limit=100&page={page.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            var payload = await _http.GetFromJsonAsync<Payload>(uri, Json, ct);
            return payload?.Data?.Where(x => !string.IsNullOrWhiteSpace(x.Uuid)).Select(Map).ToArray() ?? [];
        }
        catch (HttpRequestException) { return await GoogleRssAsync(ct); }
    }

    public Task<NewsDetail?> DetailAsync(string id, CancellationToken ct) => Task.FromResult<NewsDetail?>(null);

    async Task<IReadOnlyList<NewsFeedItem>> GoogleRssAsync(CancellationToken ct)
    {
        var urls = new[] { _options.GoogleNewsUrl, _options.YahooNewsUrl }.Distinct(StringComparer.OrdinalIgnoreCase);
        var documents = await Task.WhenAll(urls.Select(async url =>
        {
            try
            {
                using var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                return XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException) { return null; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (System.Xml.XmlException) { return null; }
        }));
        return documents.Where(x => x is not null).SelectMany(x => x!.Descendants("item")).Select(item =>
        {
            var link = item.Element("link")?.Value?.Trim() ?? "";
            var title = item.Element("title")?.Value?.Trim() ?? "";
            var description = NormalizeRssText(item.Element("description")?.Value);
            var summary = NormalizeRssText(item.Element("summary")?.Value);
            var listedEvidence = string.Join("\n", new[] { description, summary }.Where(x => x.Length > 0));
            var content = NormalizeRssText(item.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("encoded", StringComparison.OrdinalIgnoreCase))?.Value);
            var source = item.Element("source")?.Value?.Trim() ?? "Google News";
            var published = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var at)
                ? at : DateTimeOffset.UtcNow;
            return new NewsFeedItem(link.Length == 0 ? title : link, title, listedEvidence, source, published, [], Content: content);
        }).Where(x => x.Id.Length > 0).GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).Take(100).ToArray();
    }

    static string NormalizeRssText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decoded = WebUtility.HtmlDecode(value);
        var plain = HtmlTags.Replace(decoded, " ");
        plain = Regex.Replace(plain, "\\s+", " ").Trim();
        return plain.Length > RssTextLimit ? plain[..RssTextLimit] : plain;
    }

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
