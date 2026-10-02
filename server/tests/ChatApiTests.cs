using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Application.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ChatApiTests
{
    [Fact]
    public async Task 모델_목록은_뉴스_모델_표시와_자원_안내를_함께_돌려준다()
    {
        using var host = new Host(new FakeGateway(), newsEnabled: true);
        var newsState = host.Factory.Services.GetRequiredService<NewsRuntimeState>();
        newsState.QueueDepth(4);
        using var client = host.Factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/chat/models"));

        var root = doc.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        var models = root.GetProperty("models").EnumerateArray().ToArray();
        Assert.Equal(["deepseek-r1:14b", "qwen2.5:7b-instruct"], models.Select(m => m.GetProperty("name").GetString()));
        Assert.True(models[1].GetProperty("isNewsModel").GetBoolean());
        Assert.False(models[0].GetProperty("isNewsModel").GetBoolean());
        Assert.Equal("qwen2.5:7b-instruct", root.GetProperty("defaultModel").GetString());
        var resources = root.GetProperty("resources");
        Assert.True(resources.GetProperty("newsEnabled").GetBoolean());
        Assert.Equal("qwen2.5:7b-instruct", resources.GetProperty("newsModel").GetString());
        Assert.Equal(4, resources.GetProperty("newsQueue").GetInt32());
        Assert.False(resources.GetProperty("chatBusy").GetBoolean());
    }

    [Fact]
    public async Task Ollama에_닿지_않으면_모델_목록은_error_상태와_한국어_메시지를_돌려준다()
    {
        using var host = new Host(new FakeGateway { ListFailure = new ChatGatewayException(ChatErrorKinds.Unreachable, "refused") });
        using var client = host.Factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/chat/models"));

        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("Ollama에 연결할 수 없습니다", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task 연결_확인은_지연과_뉴스_모델_불일치_경고를_돌려준다()
    {
        using var host = new Host(new FakeGateway { CheckLatencyMs = 321 }, newsEnabled: true);
        using var client = host.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/chat/check", new { model = "deepseek-r1:14b" });
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(321, doc.RootElement.GetProperty("latencyMs").GetInt64());
        Assert.Contains("뉴스 분류 모델(qwen2.5:7b-instruct)과 다릅니다", doc.RootElement.GetProperty("warning").GetString());
        Assert.False(doc.RootElement.GetProperty("resources").GetProperty("chatBusy").GetBoolean());
    }

    [Fact]
    public async Task 연결_확인은_뉴스_모델과_같거나_뉴스가_꺼져_있으면_경고가_없다()
    {
        using var sameHost = new Host(new FakeGateway(), newsEnabled: true);
        using var offHost = new Host(new FakeGateway(), newsEnabled: false);

        using var same = await sameHost.Factory.CreateClient().PostAsJsonAsync("/api/chat/check", new { model = "qwen2.5:7b-instruct" });
        using var off = await offHost.Factory.CreateClient().PostAsJsonAsync("/api/chat/check", new { model = "deepseek-r1:14b" });

        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await same.Content.ReadAsStringAsync()).RootElement.GetProperty("warning").ValueKind);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await off.Content.ReadAsStringAsync()).RootElement.GetProperty("warning").ValueKind);
    }

    [Fact]
    public async Task 연결_확인_실패는_ok_false와_오류_종류를_돌려준다()
    {
        using var host = new Host(new FakeGateway { CheckFailure = new ChatGatewayException(ChatErrorKinds.Busy, "503") });
        using var client = host.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/chat/check", new { model = "qwen2.5:7b-instruct" });
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("busy", doc.RootElement.GetProperty("errorKind").GetString());
        Assert.Contains("503", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task 연결_확인에_model이_없으면_400이다()
    {
        using var host = new Host(new FakeGateway());
        using var client = host.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/chat/check", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 채팅_스트림은_SSE로_delta와_done을_중계하고_think_블록을_thinking으로_분리한다()
    {
        var gateway = new FakeGateway
        {
            Chunks = ["<think>추론", " 중</think>안녕", "하세요"],
        };
        using var host = new Host(gateway);
        using var client = host.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/chat", new { model = "qwen3:14b", messages = new[] { new { role = "user", content = "hi" } } });
        var text = await response.Content.ReadAsStringAsync();
        var events = ParseSse(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(["thinking", "thinking", "delta", "delta", "done"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("추론 중", string.Concat(events.Where(e => e.GetProperty("type").GetString() == "thinking").Select(e => e.GetProperty("thinking").GetString())));
        Assert.Equal("안녕하세요", string.Concat(events.Where(e => e.GetProperty("type").GetString() == "delta").Select(e => e.GetProperty("content").GetString())));
        Assert.Equal(42, events[^1].GetProperty("totalMs").GetInt64());
        Assert.Equal("system", gateway.LastRequest!.Messages[0].Role);
        Assert.Contains("한국어", gateway.LastRequest.Messages[0].Content);
        Assert.Equal("qwen3:14b", gateway.LastRequest.Model);
    }

    [Fact]
    public async Task 게이트웨이_오류는_SSE_error_이벤트로_전달된다()
    {
        using var host = new Host(new FakeGateway { StreamFailure = new ChatGatewayException(ChatErrorKinds.Busy, "queue full") });
        using var client = host.Factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/chat", new { model = "m", messages = new[] { new { role = "user", content = "hi" } } });
        var events = ParseSse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var error = Assert.Single(events);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("busy", error.GetProperty("errorKind").GetString());
        Assert.Contains("503", error.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("""{"model":"m","messages":[]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}]}""")]
    [InlineData("""{"model":"m","messages":[{"role":"system","content":"x"},{"role":"user","content":"hi"}]}""")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"yo"}]}""")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":"  "}]}""")]
    [InlineData("not json")]
    public async Task 잘못된_채팅_요청은_400이다(string body)
    {
        using var host = new Host(new FakeGateway());
        using var client = host.Factory.CreateClient();

        using var response = await client.PostAsync("/api/chat", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 대화가_진행_중이면_두_번째_채팅은_429다()
    {
        var gateway = new FakeGateway { HoldStream = new TaskCompletionSource() };
        using var host = new Host(gateway);
        using var client = host.Factory.CreateClient();

        var first = client.PostAsJsonAsync("/api/chat", new { model = "m", messages = new[] { new { role = "user", content = "hi" } } });
        await gateway.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var second = await client.PostAsJsonAsync("/api/chat", new { model = "m", messages = new[] { new { role = "user", content = "hi" } } });
        gateway.HoldStream.SetResult();
        using var firstResponse = await first;

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("다른 대화가 진행 중", await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
    }

    [Fact]
    public async Task Chat_Enabled가_false면_모델_목록은_disabled이고_채팅은_404다()
    {
        using var host = new Host(new FakeGateway(), chatEnabled: false);
        using var client = host.Factory.CreateClient();

        using var models = JsonDocument.Parse(await client.GetStringAsync("/api/chat/models"));
        using var chat = await client.PostAsJsonAsync("/api/chat", new { model = "m", messages = new[] { new { role = "user", content = "hi" } } });

        Assert.False(models.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", models.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);
    }

    static List<JsonElement> ParseSse(string text)
        => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Trim())
            .Where(block => block.StartsWith("data: ", StringComparison.Ordinal))
            .Select(block => JsonDocument.Parse(block["data: ".Length..]).RootElement.Clone())
            .ToList();

    sealed class FakeGateway : IChatModelGateway
    {
        public ChatGatewayException? ListFailure { get; init; }
        public ChatGatewayException? CheckFailure { get; init; }
        public ChatGatewayException? StreamFailure { get; init; }
        public long CheckLatencyMs { get; init; } = 100;
        public string[] Chunks { get; init; } = ["안녕"];
        public TaskCompletionSource? HoldStream { get; init; }
        public TaskCompletionSource StreamStarted { get; } = new();
        public ChatCompletionRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<ChatModelInfo>> ListModelsAsync(CancellationToken ct)
        {
            if (ListFailure is not null) throw ListFailure;
            return Task.FromResult<IReadOnlyList<ChatModelInfo>>(
            [
                new("qwen2.5:7b-instruct", 4_600_000_000, "7.6B", "qwen2", null),
                new("deepseek-r1:14b", 9_000_000_000, "14.8B", "qwen2", null),
            ]);
        }

        public Task<ChatCheckResult> CheckAsync(string model, CancellationToken ct)
        {
            if (CheckFailure is not null) throw CheckFailure;
            return Task.FromResult(new ChatCheckResult(true, CheckLatencyMs, 0, null, null));
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            LastRequest = request;
            StreamStarted.TrySetResult();
            if (HoldStream is not null) await HoldStream.Task.WaitAsync(ct);
            if (StreamFailure is not null) throw StreamFailure;
            foreach (var chunk in Chunks) yield return ChatStreamEvent.Delta(chunk);
            yield return ChatStreamEvent.Done(42, 7);
        }
    }

    sealed class Host : IDisposable
    {
        readonly string _root = Directory.CreateTempSubdirectory("astra-chat-api-").FullName;
        public WebApplicationFactory<Program> Factory { get; }

        public Host(IChatModelGateway gateway, bool newsEnabled = false, bool chatEnabled = true)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(_root);
                builder.UseSetting("Chat:Enabled", chatEnabled ? "true" : "false");
                builder.UseSetting("Chat:BusyWaitSeconds", "0");
                builder.UseSetting("News:Enabled", newsEnabled ? "true" : "false");
                builder.UseSetting("News:Model", "qwen2.5:7b-instruct");
                builder.UseSetting("News:OllamaUrl", "http://127.0.0.1:9");
                builder.UseSetting("News:FeedUrl", "http://127.0.0.1:9");
                builder.ConfigureServices(services =>
                {
                    services.Remove(services.Single(x => x.ServiceType == typeof(IChatModelGateway)));
                    services.AddSingleton(gateway);
                });
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
