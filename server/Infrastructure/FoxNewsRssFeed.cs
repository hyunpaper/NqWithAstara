using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>Fox News 공식 공개 RSS의 제목·요약·원문 링크를 수집한다.</summary>
public sealed class FoxNewsRssFeed : INewsFeed
{
    public const int SummaryLimit = 1500;
    static readonly Regex HtmlTags = new("<[^>]*>", RegexOptions.Compiled);
    readonly NewsOptions _options;
    readonly HttpClient _http;

    public FoxNewsRssFeed(NewsOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Name => NewsFeedProviders.FoxNewsRss;
    public int DailyRequestLimit => Math.Max(1, _options.RssDailyRequestLimit);

    public async Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
        => (await FetchAsync(page, ct)).Items;

    public async Task<NewsFeedBatch> FetchAsync(int page, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(_options.FoxNewsRssUrl, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failed("quota_wait");
            if (response.StatusCode == HttpStatusCode.NotModified) return Failed("empty");
            if (!response.IsSuccessStatusCode) return Failed("failed");

            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var rows = document.Descendants("item")
                .Select(Map)
                .Where(x => x is not null)
                .Select(x => x!)
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => x.First())
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id, StringComparer.Ordinal)
                .Take(100)
                .ToArray();
            var status = rows.Length == 0 ? "empty" : "ok";
            return new NewsFeedBatch(rows, status,
                [new NewsProviderFetchStatus(NewsFeedProviders.FoxNewsRss, status, rows.Length)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return Failed("failed"); }
        catch (TaskCanceledException) { return Failed("failed"); }
        catch (XmlException) { return Failed("invalid_response"); }
    }

    public Task<NewsDetail?> DetailAsync(string id, CancellationToken ct)
        => Task.FromResult<NewsDetail?>(null);

    static NewsFeedItem? Map(XElement item)
    {
        var title = item.Element("title")?.Value?.Trim() ?? "";
        var link = ValidUrl(item.Element("link")?.Value);
        var guid = item.Element("guid")?.Value?.Trim() ?? "";
        var id = guid.Length > 0 ? guid : link ?? title;
        if (id.Length == 0) return null;
        var summary = Normalize(item.Element("description")?.Value);
        var published = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var at)
            ? at : DateTimeOffset.MinValue;
        return new NewsFeedItem(id, title, summary, "Fox News", published, [],
            Url: link, Provider: NewsFeedProviders.FoxNewsRss);
    }

    static NewsFeedBatch Failed(string status)
        => new([], status, [new NewsProviderFetchStatus(NewsFeedProviders.FoxNewsRss, status, 0)]);

    static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decoded = WebUtility.HtmlDecode(value);
        var plain = HtmlTags.Replace(decoded, " ");
        plain = Regex.Replace(plain, "\\s+", " ").Trim();
        return plain.Length > SummaryLimit ? plain[..SummaryLimit] : plain;
    }

    static string? ValidUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.ToString() : null;
}
