using System.Net;
using System.Text;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Application.Chat;
using Astra.Server.Infrastructure.Chat;
using Xunit;

namespace Astra.Server.Tests;

public sealed class OllamaChatGatewayTests
{
    sealed record Captured(Uri Url, string Body);

    static OllamaChatGateway Gateway(Func<HttpRequestMessage, HttpResponseMessage> respond, out List<Captured> requests, string? chatUrl = null)
    {
        var captured = new List<Captured>();
        requests = captured;
        var handler = new StubHandler(req =>
        {
            captured.Add(new Captured(req.RequestUri!, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult()));
            return respond(req);
        });
        return new OllamaChatGateway(new ChatOptions { OllamaUrl = chatUrl }, new NewsOptions { OllamaUrl = "http://news-host:11434" }, new HttpClient(handler));
    }

    static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 모델_목록은_tags를_읽고_이름순_정보를_돌려준다()
    {
        var gateway = Gateway(_ => JsonResponse(HttpStatusCode.OK,
            """{"models":[{"name":"qwen2.5:7b-instruct","size":4683087332,"modified_at":"2026-09-01T00:00:00Z","details":{"family":"qwen2","parameter_size":"7.6B"}},{"name":"qwen3:14b","size":9000,"details":{"family":"qwen3","parameter_size":"14.8B"}}]}"""),
            out var requests);

        var models = await gateway.ListModelsAsync(CancellationToken.None);

        Assert.Equal("http://news-host:11434/api/tags", requests.Single().Url.ToString());
        Assert.Equal(2, models.Count);
        Assert.Equal("qwen2.5:7b-instruct", models[0].Name);
        Assert.Equal("7.6B", models[0].ParameterSize);
        Assert.Equal("qwen2", models[0].Family);
        Assert.Equal(4683087332, models[0].SizeBytes);
    }

    [Fact]
    public async Task Chat_OllamaUrl이_있으면_News_URL보다_우선한다()
    {
        var gateway = Gateway(_ => JsonResponse(HttpStatusCode.OK, """{"models":[]}"""), out var requests, chatUrl: "http://ai-host:11434/");

        await gateway.ListModelsAsync(CancellationToken.None);

        Assert.Equal("http://ai-host:11434/api/tags", requests.Single().Url.ToString());
    }

    [Fact]
    public async Task 연결_실패는_unreachable_예외로_바꾼다()
    {
        var gateway = Gateway(_ => throw new HttpRequestException("connection refused"), out _);

        var ex = await Assert.ThrowsAsync<ChatGatewayException>(() => gateway.ListModelsAsync(CancellationToken.None));

        Assert.Equal(ChatErrorKinds.Unreachable, ex.Kind);
    }

    [Fact]
    public async Task 연결_확인은_stream_false로_짧은_생성을_요청하고_keep_alive와_num_ctx를_보내지_않는다()
    {
        var gateway = Gateway(_ => JsonResponse(HttpStatusCode.OK,
            """{"model":"qwen2.5:7b-instruct","message":{"role":"assistant","content":"네"},"done":true,"load_duration":2500000000,"total_duration":3000000000}"""), out var requests);

        var result = await gateway.CheckAsync("qwen2.5:7b-instruct", CancellationToken.None);

        var body = requests.Single().Body;
        using var doc = JsonDocument.Parse(body);
        Assert.True(result.Ok);
        Assert.Equal(2500, result.LoadMs);
        Assert.Equal("http://news-host:11434/api/chat", requests.Single().Url.ToString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("qwen2.5:7b-instruct", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(8, doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("keep_alive", out _));
        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("num_ctx", out _));
        Assert.Equal("system", doc.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task 연결_확인에서_503은_busy_404는_model_not_found다()
    {
        var busy = Gateway(_ => JsonResponse(HttpStatusCode.ServiceUnavailable, """{"error":"server busy"}"""), out _);
        var missing = Gateway(_ => JsonResponse(HttpStatusCode.NotFound, """{"error":"model 'x' not found"}"""), out _);

        var busyEx = await Assert.ThrowsAsync<ChatGatewayException>(() => busy.CheckAsync("m", CancellationToken.None));
        var missingEx = await Assert.ThrowsAsync<ChatGatewayException>(() => missing.CheckAsync("x", CancellationToken.None));

        Assert.Equal(ChatErrorKinds.Busy, busyEx.Kind);
        Assert.Equal(ChatErrorKinds.ModelNotFound, missingEx.Kind);
        Assert.Equal("model 'x' not found", missingEx.Detail);
    }

    [Fact]
    public async Task 스트림은_NDJSON_조각을_delta_thinking_done_이벤트로_중계한다()
    {
        var ndjson = string.Join("\n",
            """{"message":{"role":"assistant","content":"안녕"},"done":false}""",
            """{"message":{"role":"assistant","content":"","thinking":"생각"},"done":false}""",
            """{"message":{"role":"assistant","content":"하세요"},"done":false}""",
            """{"message":{"role":"assistant","content":""},"done":true,"total_duration":1500000000,"eval_count":12}""") + "\n";
        var gateway = Gateway(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson") }, out var requests);

        var events = new List<ChatStreamEvent>();
        await foreach (var evt in gateway.StreamAsync(new ChatCompletionRequest("qwen3:14b", [new("system", "s"), new("user", "u")], 256), CancellationToken.None))
            events.Add(evt);

        var body = requests.Single().Body;
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(256, doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("keep_alive", out _));
        Assert.Equal(["delta", "thinking", "delta", "done"], events.Select(e => e.Type));
        Assert.Equal("안녕", events[0].Content);
        Assert.Equal("생각", events[1].Thinking);
        Assert.Equal(1500, events[3].TotalMs);
        Assert.Equal(12, events[3].OutputTokens);
    }

    [Fact]
    public async Task 스트림_도중_error_조각은_예외로_바꾼다()
    {
        var ndjson = """{"message":{"role":"assistant","content":"a"},"done":false}""" + "\n" + """{"error":"model 'gone' not found"}""" + "\n";
        var gateway = Gateway(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson") }, out _);

        var events = new List<ChatStreamEvent>();
        var ex = await Assert.ThrowsAsync<ChatGatewayException>(async () =>
        {
            await foreach (var evt in gateway.StreamAsync(new ChatCompletionRequest("gone", [new("user", "u")], 64), CancellationToken.None))
                events.Add(evt);
        });

        Assert.Single(events);
        Assert.Equal(ChatErrorKinds.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task 스트림_요청의_503은_busy_예외다()
    {
        var gateway = Gateway(_ => JsonResponse(HttpStatusCode.ServiceUnavailable, "queue full"), out _);

        var ex = await Assert.ThrowsAsync<ChatGatewayException>(async () =>
        {
            await foreach (var _ in gateway.StreamAsync(new ChatCompletionRequest("m", [new("user", "u")], 64), CancellationToken.None)) { }
        });

        Assert.Equal(ChatErrorKinds.Busy, ex.Kind);
        Assert.Equal("queue full", ex.Detail);
    }

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
