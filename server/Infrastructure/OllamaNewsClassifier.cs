using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

/// <summary>
/// 로컬 Ollama 감성 분류기(#151 §3). 타임아웃·파싱 실패는 unclassified로 남기고,
/// 연결 자체가 안 되면 Available=false로 알려 호출자가 기사를 소비하지 않게 한다.
/// </summary>
public sealed class OllamaNewsClassifier : INewsClassifier
{
    public const int BodyLimit = 1500;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly NewsOptions _options;
    readonly HttpClient _http;

    public OllamaNewsClassifier(NewsOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient { Timeout = Timeout };
    }

    public async Task<NewsClassificationResult> ClassifyAsync(NewsClassificationRequest request, CancellationToken ct)
    {
        var url = new Uri(new Uri(_options.OllamaUrl.TrimEnd('/') + "/"), "api/generate");
        var payload = new GenerateRequest(
            _options.Model, BuildPrompt(request), false, "json", _options.KeepAlive,
            new GenerateOptions(0, 2048, 160));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsJsonAsync(url, payload, Json, ct);
            if (!response.IsSuccessStatusCode) return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, false);
            var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(Json, ct);
            stopwatch.Stop();
            return new NewsClassificationResult(
                NewsClassificationParser.TryParse(body?.Response), _options.Model, stopwatch.ElapsedMilliseconds, true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 타임아웃은 모델이 살아 있다는 뜻이므로 기사를 unclassified로 확정한다.
            return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, true);
        }
        catch (HttpRequestException)
        {
            return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, false);
        }
        catch (JsonException)
        {
            return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, true);
        }
    }

    /// <summary>영문 지시 + 한국어 기사 원문. 종목을 못 고르면 MARKET으로 답하게 한다.</summary>
    public static string BuildPrompt(NewsClassificationRequest request)
    {
        var body = request.Body.Length > BodyLimit ? request.Body[..BodyLimit] : request.Body;
        var tickers = request.Tickers.Count > 0 ? string.Join(", ", request.Tickers) : "(none)";
        return $$"""
            You classify financial news for a stock dashboard. The article is written in Korean.
            Reply with ONE JSON object and nothing else:
            {"symbols":["TICKER"],"sentiment":"positive|negative|neutral","strength":1,"reason":"<=20 words"}
            Rules:
            - symbols: uppercase US-listed tickers the article is about. Prefer the feed tickers when given.
            - If the article is macro or market-wide (war, central banks, oil, indices, economic data) and names no listed company, answer exactly ["MARKET"].
            - sentiment: the likely effect on those symbols. strength: 1 (minor) to 5 (major).
            - reason: at most 20 words, written in Korean.
            Feed tickers: {{tickers}}
            Title: {{request.Title}}
            Body: {{body}}
            """;
    }

    sealed record GenerateRequest(
        string Model, string Prompt, bool Stream, string Format,
        [property: JsonPropertyName("keep_alive")] string KeepAlive, GenerateOptions Options);

    sealed record GenerateOptions(
        double Temperature,
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    sealed record GenerateResponse(string? Response);
}
