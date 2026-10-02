using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application.Chat;

namespace Astra.Server.Api;

/// <summary>Ollama 채팅 프록시 API(#338). 브라우저는 이 서버만 호출하고 11434는 외부에 노출하지 않는다.</summary>
public static class ChatEndpoints
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase) { "user", "assistant" };

    public sealed record CheckRequest(string? Model);
    public sealed record ChatRequest(string? Model, IReadOnlyList<ChatMessage>? Messages);

    public static IEndpointRouteBuilder MapChatApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/chat/models", async (ChatService chat, CancellationToken ct) => Results.Ok(await chat.ModelsAsync(ct)));
        app.MapPost("/api/chat/check", async (CheckRequest? body, ChatService chat, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Model)) return Results.BadRequest(new { error = "model이 필요합니다." });
            return Results.Ok(await chat.CheckAsync(body.Model.Trim(), ct));
        });
        app.MapPost("/api/chat", ChatStreamAsync);
        return app;
    }

    static async Task ChatStreamAsync(HttpContext context, ChatService chat, ChatOptions options)
    {
        ChatRequest? body;
        try { body = await context.Request.ReadFromJsonAsync<ChatRequest>(Json, context.RequestAborted); }
        catch (JsonException) { body = null; }
        var error = Validate(body, options);
        if (error is not null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
            return;
        }
        if (!options.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "채팅 기능이 꺼져 있습니다(Chat:Enabled=false)." }, context.RequestAborted);
            return;
        }
        if (chat.IsBusy)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { error = ChatMessages.LocalBusy }, context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.Body.FlushAsync(context.RequestAborted);

        var ct = context.RequestAborted;
        try
        {
            await foreach (var evt in chat.StreamAsync(body!.Model!.Trim(), body.Messages!, ct))
            {
                await context.Response.WriteAsync("data: ", ct);
                await context.Response.WriteAsync(JsonSerializer.Serialize(evt, Json), ct);
                await context.Response.WriteAsync("\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    static string? Validate(ChatRequest? body, ChatOptions options)
    {
        if (body is null) return "요청 본문이 올바르지 않습니다.";
        if (string.IsNullOrWhiteSpace(body.Model)) return "model이 필요합니다.";
        if (body.Messages is null || body.Messages.Count == 0) return "messages가 비어 있습니다.";
        if (body.Messages.Any(m => m is null || !Roles.Contains(m.Role ?? ""))) return "messages의 role은 user 또는 assistant여야 합니다.";
        if (!string.Equals(body.Messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase)) return "마지막 메시지는 user여야 합니다.";
        if (string.IsNullOrWhiteSpace(body.Messages[^1].Content)) return "마지막 메시지 내용이 비어 있습니다.";
        if (body.Messages.Count > Math.Max(1, options.MaxTurns) * 4) return "대화 이력이 너무 깁니다. 최근 대화만 보내세요.";
        return null;
    }
}
