using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>
/// saveticker 뉴스 피드 어댑터(#151 §1). 비브라우저 UA는 403이라 브라우저형 UA와 Referer를 붙인다.
/// 429·5xx는 예외로 올려 호출자가 다음 주기에 재시도하게 한다.
/// </summary>
public sealed class SaveTickerNewsFeed : INewsFeed
{
    public const int DetailBodyLimit = 1500;

    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;
    readonly Uri _root;

    public SaveTickerNewsFeed(NewsOptions options, HttpClient? http = null)
    {
        _root = new Uri(options.FeedUrl.TrimEnd('/') + "/");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
    {
        using var response = await SendAsync($"api/news/list?page={page.ToString(CultureInfo.InvariantCulture)}", ct);
        var payload = await response.Content.ReadFromJsonAsync<ListPayload>(Json, ct);
        if (payload?.NewsList is null) return [];
        return payload.NewsList
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => new NewsFeedItem(
                x.Id!,
                x.Title ?? "",
                x.Content ?? "",
                x.Source ?? "",
                x.CreatedAt ?? DateTimeOffset.MinValue,
                x.Tickers?.Where(t => !string.IsNullOrWhiteSpace(t.Symbol)).Select(t => t.Symbol!.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray() ?? []))
            .ToArray();
    }

    public async Task<string?> DetailAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync($"api/news/detail?id={Uri.EscapeDataString(id)}", ct);
        var payload = await response.Content.ReadFromJsonAsync<DetailPayload>(Json, ct);
        if (payload?.Content is null) return null;
        var body = string.Join("\n", payload.Content
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (body.Length == 0) return null;
        return body.Length > DetailBodyLimit ? body[..DetailBodyLimit] : body;
    }

    async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_root, path));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri(_root, "news");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    sealed record ListPayload([property: JsonPropertyName("news_list")] List<ListItem>? NewsList);

    sealed record ListItem(
        string? Id,
        string? Title,
        string? Content,
        string? Source,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        List<TickerTag>? Tickers);

    sealed record TickerTag(string? Symbol, string? Name);

    sealed record DetailPayload(string? Id, string? Title, List<ContentBlock>? Content);

    sealed record ContentBlock(string? Type, string? Content);
}
