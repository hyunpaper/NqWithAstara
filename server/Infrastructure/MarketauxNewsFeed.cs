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

    public string Name => string.IsNullOrWhiteSpace(_options.MarketauxApiKey) ? "rss" : "marketaux";
    public TimeSpan MinimumInterval => string.IsNullOrWhiteSpace(_options.MarketauxApiKey)
        ? TimeSpan.Zero
        : TimeSpan.FromMinutes(Math.Max(1, _options.MarketauxPollMinutes));
    public int DailyRequestLimit => string.IsNullOrWhiteSpace(_options.MarketauxApiKey)
        ? Math.Max(1, _options.RssDailyRequestLimit / RssSources().Length)
        : Math.Min(100, Math.Max(1, _options.MarketauxDailyRequestLimit));

    public async Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
        => (await FetchAsync(page, ct)).Items;

    public async Task<NewsFeedBatch> FetchAsync(int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.MarketauxApiKey)) return await RssAsync(ct, []);
        // 엔터티 없는 거시·시장 기사도 MARKET으로 분류해야 하므로 전체 피드를 유지한다.
        var uri = $"{_options.MarketauxUrl}?api_token={Uri.EscapeDataString(_options.MarketauxApiKey)}&language=en&limit=3&page={page.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            using var response = await _http.GetAsync(uri, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return FailedBatch("quota_wait");
            if (!response.IsSuccessStatusCode)
                return FailedBatch("failed");
            var payload = await response.Content.ReadFromJsonAsync<Payload>(Json, ct);
            var rows = payload?.Data?.Where(x => !string.IsNullOrWhiteSpace(x.Uuid)).Select(Map).ToArray() ?? [];
            var status = rows.Length == 0 ? "empty" : "ok";
            return new NewsFeedBatch(rows, status, [new NewsProviderFetchStatus("marketaux", status, rows.Length)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return FailedBatch("failed"); }
        catch (TaskCanceledException) { return FailedBatch("failed"); }
        catch (JsonException) { return FailedBatch("invalid_response"); }
    }

    public Task<NewsDetail?> DetailAsync(string id, CancellationToken ct) => Task.FromResult<NewsDetail?>(null);

    async Task<NewsFeedBatch> RssAsync(CancellationToken ct, IReadOnlyList<NewsProviderFetchStatus> initial)
    {
        var sources = RssSources();
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                using var response = await _http.GetAsync(source.Url, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return (Document: (XDocument?)null, Status: new NewsProviderFetchStatus(source.Provider, "quota_wait", 0));
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return (Document: new XDocument(new XElement("rss")), Status: new NewsProviderFetchStatus(source.Provider, "empty", 0));
                if (!response.IsSuccessStatusCode)
                    return (Document: (XDocument?)null, Status: new NewsProviderFetchStatus(source.Provider, "failed", 0));
                var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var count = document.Descendants("item").Count();
                return (Document: document, Status: new NewsProviderFetchStatus(source.Provider, count == 0 ? "empty" : "ok", count));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return (null, new NewsProviderFetchStatus(source.Provider, "failed", 0)); }
            catch (TaskCanceledException) { return (null, new NewsProviderFetchStatus(source.Provider, "failed", 0)); }
            catch (System.Xml.XmlException) { return (null, new NewsProviderFetchStatus(source.Provider, "invalid_response", 0)); }
        }));
        var providers = initial.Concat(results.Select(x => x.Status)).ToArray();
        var rows = results.Where(x => x.Document is not null).SelectMany(x => x.Document!.Descendants("item")).Select(item =>
        {
            var link = item.Element("link")?.Value?.Trim() ?? "";
            var title = item.Element("title")?.Value?.Trim() ?? "";
            var description = NormalizeRssText(item.Element("description")?.Value);
            var summary = NormalizeRssText(item.Element("summary")?.Value);
            var listedEvidence = string.Join("\n", new[] { description, summary }.Where(x => x.Length > 0));
            var content = NormalizeRssText(item.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("encoded", StringComparison.OrdinalIgnoreCase))?.Value);
            var source = item.Element("source")?.Value?.Trim() ?? "Google News";
            var published = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var at)
                ? at : DateTimeOffset.MinValue;
            var provider = item.Document?.Root?.Descendants("item").Any() == true
                ? results.FirstOrDefault(x => ReferenceEquals(x.Document, item.Document)).Status.Provider
                : "rss";
            return new NewsFeedItem(link.Length == 0 ? title : link, title, listedEvidence, source, published, [],
                Content: content, Url: ValidUrl(link), Provider: provider);
        }).Where(x => x.Id.Length > 0)
          .GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First())
          .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id, StringComparer.Ordinal).Take(100).ToArray();
        var successful = providers.Count(x => x.Status is "ok" or "empty");
        var failed = providers.Length - successful;
        var status = successful == 0
            ? (providers.All(x => x.Status == "quota_wait") ? "quota_wait" : "failed")
            : failed > 0 ? "partial" : rows.Length == 0 ? "empty" : "ok";
        return new NewsFeedBatch(rows, status, providers);
    }

    (string Provider, string Url)[] RssSources()
        => new[] { (Provider: "google-rss", Url: _options.GoogleNewsUrl), (Provider: "yahoo-rss", Url: _options.YahooNewsUrl) }
            .DistinctBy(x => x.Url, StringComparer.OrdinalIgnoreCase).ToArray();

    static NewsFeedBatch FailedBatch(string status)
        => new([], status, [new NewsProviderFetchStatus("marketaux", status, 0)]);

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
        return new NewsFeedItem(x.Uuid!, x.Title ?? "", x.Description ?? "", x.Source ?? "", x.PublishedAt ?? DateTimeOffset.MinValue, tickers, Entities: entities, Url: ValidUrl(x.Url), Provider: "marketaux");
    }

    static string? ValidUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.ToString() : null;

    sealed record Payload(List<Item>? Data);
    sealed record Item(
        string? Uuid, string? Title, string? Description, string? Source,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        List<Entity>? Entities,
        string? Url);
    sealed record Entity(string? Symbol, string? Name, string? Industry,
        [property: JsonPropertyName("sentiment_score")] double? SentimentScore,
        [property: JsonPropertyName("match_score")] double? MatchScore);
}
