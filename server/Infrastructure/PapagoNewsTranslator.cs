using System.Text.Json;
using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>Papago NMT 번역 어댑터. 키는 설정에서만 읽고 응답·로그에 포함하지 않는다.</summary>
public sealed class PapagoNewsTranslator : INewsTranslator
{
    const string Endpoint = "https://papago.apigw.ntruss.com/nmt/v1/translation";
    readonly NewsOptions _options;
    readonly HttpClient _http;

    public PapagoNewsTranslator(NewsOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<(string Title, string Source)?> TranslateAsync(string title, string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.PapagoClientId) || string.IsNullOrWhiteSpace(_options.PapagoClientSecret)) return null;
        try
        {
            var titleKo = await TranslateOneAsync(title, ct);
            var sourceKo = await TranslateOneAsync(source, ct);
            return string.IsNullOrWhiteSpace(titleKo) || string.IsNullOrWhiteSpace(sourceKo) ? null : (titleKo!, sourceKo!);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<string?> TranslateTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_options.PapagoClientId) || string.IsNullOrWhiteSpace(_options.PapagoClientSecret)) return null;
        try { return await TranslateOneAsync(text.Length > 5000 ? text[..5000] : text, ct); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    async Task<string?> TranslateOneAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-NCP-APIGW-API-KEY-ID", _options.PapagoClientId);
        request.Headers.Add("X-NCP-APIGW-API-KEY", _options.PapagoClientSecret);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["source"] = "auto", ["target"] = "ko", ["text"] = text
        });
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.GetProperty("message").GetProperty("result").GetProperty("translatedText").GetString();
    }
}
