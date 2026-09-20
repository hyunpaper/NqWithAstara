using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

/// <summary>
/// 로컬 Ollama 감성 분류기(#151 §3, #171 프롬프트 v2c). 타임아웃·파싱 실패는 unclassified로 남기고,
/// 연결 자체가 안 되면 Available=false로 알려 호출자가 기사를 소비하지 않게 한다.
/// </summary>
public sealed class OllamaNewsClassifier : INewsClassifier
{
    public const int BodyLimit = 600;

    /// <summary>현재 채택된 분류 프롬프트 버전(#171). <see cref="NewsPromptVersions.V2c"/>의 별칭이다.</summary>
    public const string PromptVersion = NewsPromptVersions.V2c;

    const string PromptResourceName = "news-classify-v2c.txt";

    // 번역 제목·언론사·종목별 영향도까지 JSON으로 생성하는 데 7B 모델이 20초를 넘길 수 있다.
    // 짧은 timeout은 정상 기사를 unclassified로 영구 저장하는 결과를 만들었다.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static readonly Lazy<string> PromptTemplate = new(LoadPromptTemplate);

    static readonly Regex Placeholder = new("\\{tickers\\}|\\{title\\}|\\{body\\}", RegexOptions.Compiled);

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
            new GenerateOptions(0, 1024, 192));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsJsonAsync(url, payload, Json, ct);
            if (!response.IsSuccessStatusCode) return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, false, PromptVersion);
            var body = await response.Content.ReadFromJsonAsync<GenerateResponse>(Json, ct);
            stopwatch.Stop();
            return new NewsClassificationResult(
                NewsClassificationParser.TryParse(body?.Response), _options.Model, stopwatch.ElapsedMilliseconds, true, PromptVersion);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // 타임아웃은 모델이 살아 있다는 뜻이므로 기사를 unclassified로 확정한다.
            return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, true, PromptVersion);
        }
        catch (HttpRequestException)
        {
            return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, false, PromptVersion);
        }
        catch (JsonException)
        {
            return new NewsClassificationResult(null, _options.Model, stopwatch.ElapsedMilliseconds, true, PromptVersion);
        }
    }

    /// <summary>v2c 리소스 템플릿에 기사 원문을 채운다(#171). 종목을 못 고르면 MARKET으로 답하게 한다.</summary>
    public static string BuildPrompt(NewsClassificationRequest request)
    {
        var body = request.Body.Length > BodyLimit ? request.Body[..BodyLimit] : request.Body;
        var tickers = request.Tickers.Count > 0 ? string.Join(", ", request.Tickers) : "(none)";
        return Placeholder.Replace(PromptTemplate.Value, match => match.Value switch
        {
            "{tickers}" => tickers,
            "{title}" => request.Title,
            "{body}" => body,
            _ => match.Value,
        });
    }

    static string LoadPromptTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(PromptResourceName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
