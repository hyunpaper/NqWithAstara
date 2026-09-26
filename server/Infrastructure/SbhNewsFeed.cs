using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

/// <summary>SBHNews 공개 RSS와 공개 기사 본문을 수집한다(#304).</summary>
public sealed class SbhNewsFeed : INewsFeed
{
    public const int DetailBodyLimit = 4000;
    public const string SourceName = NewsFeedProviders.SbhNewsSource;
    static readonly Regex HtmlTags = new("<[^>]*>", RegexOptions.Compiled);
    static readonly Regex Description = new("<meta\\s+name=[\"']description[\"']\\s+content=[\"'](?<value>.*?)[\"']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    const string ContentMarker = "__html\\\":\\\"";
    const string ContentEndMarker = "\\\"}";
    readonly NewsOptions _options;
    readonly HttpClient _http;
    readonly INewsRelevancePolicy _relevance;
    readonly ConcurrentDictionary<string, string> _articleUrls = new(StringComparer.Ordinal);
    string? _etag;
    DateTimeOffset? _lastModified;

    public SbhNewsFeed(NewsOptions options, HttpClient? http = null, INewsRelevancePolicy? relevance = null)
    {
        _options = options;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _relevance = relevance ?? new NewsRelevancePolicy();
    }

    public string Name => NewsFeedProviders.SbhNews;
    public TimeSpan MinimumInterval => TimeSpan.FromMinutes(15);
    public int DailyRequestLimit => Math.Max(1, _options.RssDailyRequestLimit);

    public async Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
        => (await FetchAsync(page, ct)).Items;

    public async Task<NewsFeedBatch> FetchAsync(int page, CancellationToken ct)
    {
        if (page > 1) return Failed("empty");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.SbhNewsRssUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "AstraNews/1.0 (local personal project)");
            if (!string.IsNullOrWhiteSpace(_etag)) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            if (_lastModified is not null) request.Headers.IfModifiedSince = _lastModified;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failed("quota_wait", RetryAfter(response));
            if (response.StatusCode == HttpStatusCode.NotModified) return Failed("empty");
            if (!response.IsSuccessStatusCode) return Failed("failed");
            _etag = response.Headers.ETag?.Tag;
            _lastModified = response.Content.Headers.LastModified ?? response.Headers.Date;
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var candidates = document.Descendants("item").Select(Map).Where(x => x is not null).Select(x => x!)
                .GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First())
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id, StringComparer.Ordinal).Take(100).ToArray();
            var rows = candidates.Select(x => x with { Relevance = _relevance.Evaluate(x) }).ToArray();
            foreach (var row in rows.Where(x => x.Relevance!.Included && !string.IsNullOrWhiteSpace(x.Url)))
                _articleUrls[row.Id] = row.Url!;
            var included = rows.Count(x => x.Relevance!.Included);
            var status = rows.Length == 0 ? "empty" : "ok";
            return new NewsFeedBatch(rows, status, [new NewsProviderFetchStatus(Name, status, rows.Length,
                IncludedCount: included,
                ExcludedCount: rows.Count(x => x.Relevance!.Decision == NewsRelevanceDecisions.Exclude),
                ReviewCount: rows.Count(x => x.Relevance!.Decision == NewsRelevanceDecisions.Review),
                FilterPolicyVersion: _relevance.Version)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return Failed("failed"); }
        catch (TaskCanceledException) { return Failed("failed"); }
        catch (XmlException) { return Failed("invalid_response"); }
    }

    public async Task<NewsDetail?> DetailAsync(string id, CancellationToken ct)
    {
        if (!_articleUrls.TryGetValue(id, out var url) || !IsSbhArticleUrl(url)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "AstraNews/1.0 (local personal project)");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var html = await response.Content.ReadAsStringAsync(ct);
            var summary = Normalize(Description.Match(html).Groups["value"].Value);
            var body = ExtractBody(html);
            return summary.Length == 0 && body.Length == 0 ? null : new NewsDetail(summary, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (JsonException) { return null; }
    }

    static NewsFeedItem? Map(XElement item)
    {
        var title = item.Element("title")?.Value?.Trim() ?? "";
        var link = ValidUrl(item.Element("link")?.Value);
        var id = item.Element("guid")?.Value?.Trim();
        id = string.IsNullOrWhiteSpace(id) ? link ?? title : id;
        if (string.IsNullOrWhiteSpace(id)) return null;
        var summary = Normalize(item.Element("description")?.Value);
        var publishedText = item.Element("pubDate")?.Value
            ?? item.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("updated", StringComparison.OrdinalIgnoreCase))?.Value;
        var published = DateTimeOffset.TryParse(publishedText, out var at) ? at : DateTimeOffset.MinValue;
        return new NewsFeedItem(id, title, summary, SourceName, published, [], Url: link, Provider: NewsFeedProviders.SbhNews);
    }

    static string ExtractBody(string html)
    {
        var start = html.IndexOf(ContentMarker, StringComparison.Ordinal);
        if (start < 0) return "";
        start += ContentMarker.Length;
        var end = html.IndexOf(ContentEndMarker, start, StringComparison.Ordinal);
        if (end < 0) return "";
        var escaped = html[start..end];
        var body = JsonSerializer.Deserialize<string>("\"" + escaped + "\"") ?? "";
        return Normalize(body.Length > DetailBodyLimit ? body[..DetailBodyLimit] : body);
    }

    static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decoded = WebUtility.HtmlDecode(value);
        return Regex.Replace(HtmlTags.Replace(decoded, " "), "\\s+", " ").Trim();
    }

    static bool IsSbhArticleUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "www.sbhnews.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/news/", StringComparison.OrdinalIgnoreCase);

    static string? ValidUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && IsSbhArticleUrl(uri.ToString()) ? uri.ToString() : null;

    static TimeSpan? RetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at
            ? at - DateTimeOffset.UtcNow : null);

    NewsFeedBatch Failed(string status, TimeSpan? retryAfter = null)
        => new([], status, [new NewsProviderFetchStatus(Name, status, 0, RetryAfter: retryAfter)]);
}
