using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application;
using Astra.Server.Application.Chat;

namespace Astra.Server.Infrastructure.Chat;

/// <summary>Ollama `/api/tags`·`/api/chat` 프록시(#338). keep_alive·num_ctx를 보내지 않아 서버 운영값을 덮어쓰지 않는다.</summary>
public sealed class OllamaChatGateway(ChatOptions options, NewsOptions news, HttpClient? http = null) : IChatModelGateway
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    readonly HttpClient _http = http ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    Uri Endpoint(string path) => new(new Uri(options.ResolveOllamaUrl(news).TrimEnd('/') + "/"), path);

    public async Task<IReadOnlyList<ChatModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(Endpoint("api/tags"), HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccess(response, ct);
            var body = await response.Content.ReadFromJsonAsync<TagsResponse>(Json, ct);
            return (body?.Models ?? []).Select(m => new ChatModelInfo(m.Name ?? m.Model ?? "", m.Size, m.Details?.ParameterSize, m.Details?.Family, m.ModifiedAt))
                .Where(m => m.Name.Length > 0).ToArray();
        }
        catch (HttpRequestException ex) { throw new ChatGatewayException(ChatErrorKinds.Unreachable, ex.Message); }
        catch (JsonException ex) { throw new ChatGatewayException(ChatErrorKinds.Upstream, "응답 형식 오류: " + ex.Message); }
    }

    public async Task<ChatCheckResult> CheckAsync(string model, CancellationToken ct)
    {
        var payload = new ChatRequest(model, [new("system", ChatPrompt.SystemKorean), new("user", ChatPrompt.CheckUser)], false, new ChatRequestOptions(8, 0));
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsJsonAsync(Endpoint("api/chat"), payload, Json, ct);
            await EnsureSuccess(response, ct);
            var body = await response.Content.ReadFromJsonAsync<ChatChunk>(Json, ct);
            sw.Stop();
            return new ChatCheckResult(true, sw.ElapsedMilliseconds, (body?.LoadDuration ?? 0) / 1_000_000, null, null);
        }
        catch (HttpRequestException ex) { throw new ChatGatewayException(ChatErrorKinds.Unreachable, ex.Message); }
        catch (JsonException ex) { throw new ChatGatewayException(ChatErrorKinds.Upstream, "응답 형식 오류: " + ex.Message); }
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new ChatRequest(request.Model, request.Messages.Select(m => new ChatRequestMessage(m.Role, m.Content)).ToArray(), true,
            new ChatRequestOptions(request.MaxPredictTokens, null));
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/chat")) { Content = JsonContent.Create(payload, options: Json) };
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) { throw new ChatGatewayException(ChatErrorKinds.Unreachable, ex.Message); }
        using (response)
        {
            await EnsureSuccess(response, ct);
            var sw = Stopwatch.StartNew();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var outputTokens = 0;
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                ChatChunk? chunk;
                try { chunk = JsonSerializer.Deserialize<ChatChunk>(line, Json); }
                catch (JsonException ex) { throw new ChatGatewayException(ChatErrorKinds.Upstream, "스트림 형식 오류: " + ex.Message); }
                if (chunk is null) continue;
                if (!string.IsNullOrEmpty(chunk.Error)) throw new ChatGatewayException(ClassifyError(chunk.Error), chunk.Error);
                if (chunk.Message?.Thinking is { Length: > 0 } thinking) yield return ChatStreamEvent.Think(thinking);
                if (chunk.Message?.Content is { Length: > 0 } content) yield return ChatStreamEvent.Delta(content);
                if (chunk.Done)
                {
                    outputTokens = chunk.EvalCount ?? 0;
                    yield return ChatStreamEvent.Done(chunk.TotalDuration is > 0 ? chunk.TotalDuration.Value / 1_000_000 : sw.ElapsedMilliseconds, outputTokens);
                    yield break;
                }
            }
            yield return ChatStreamEvent.Done(sw.ElapsedMilliseconds, outputTokens);
        }
    }

    static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        var detail = ExtractError(text) ?? $"HTTP {(int)response.StatusCode}";
        var kind = response.StatusCode switch
        {
            HttpStatusCode.ServiceUnavailable => ChatErrorKinds.Busy,
            HttpStatusCode.NotFound => ChatErrorKinds.ModelNotFound,
            _ => ClassifyError(detail),
        };
        throw new ChatGatewayException(kind, detail);
    }

    static string ClassifyError(string detail)
        => detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ? ChatErrorKinds.ModelNotFound : ChatErrorKinds.Upstream;

    static string? ExtractError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : text.Trim();
        }
        catch (JsonException) { return text.Trim(); }
    }

    sealed record TagsResponse(IReadOnlyList<TagModel>? Models);
    sealed record TagModel(string? Name, string? Model, long Size, [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt, TagDetails? Details);
    sealed record TagDetails(string? Family, [property: JsonPropertyName("parameter_size")] string? ParameterSize);

    sealed record ChatRequest(string Model, IReadOnlyList<ChatRequestMessage> Messages, bool Stream, ChatRequestOptions Options);
    sealed record ChatRequestMessage(string Role, string Content);
    sealed record ChatRequestOptions([property: JsonPropertyName("num_predict")] int NumPredict, double? Temperature);

    sealed record ChatChunk(ChatChunkMessage? Message, bool Done, string? Error,
        [property: JsonPropertyName("total_duration")] long? TotalDuration,
        [property: JsonPropertyName("load_duration")] long? LoadDuration,
        [property: JsonPropertyName("eval_count")] int? EvalCount);
    sealed record ChatChunkMessage(string? Role, string? Content, string? Thinking);
}
