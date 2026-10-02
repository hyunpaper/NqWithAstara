namespace Astra.Server.Application.Chat;

public sealed record ChatMessage(string Role, string Content);

public sealed record ChatModelInfo(string Name, long SizeBytes, string? ParameterSize, string? Family, DateTimeOffset? ModifiedAt);

public static class ChatErrorKinds
{
    public const string Unreachable = "unreachable";
    public const string Busy = "busy";
    public const string Timeout = "timeout";
    public const string ModelNotFound = "model_not_found";
    public const string Upstream = "upstream";
    public const string Cancelled = "cancelled";
}

/// <summary>게이트웨이 실패를 종류별로 전달한다. 사용자 문구는 <see cref="ChatMessages"/>가 만든다.</summary>
public sealed class ChatGatewayException(string kind, string detail) : Exception(detail)
{
    public string Kind { get; } = kind;
    public string Detail { get; } = detail;
}

public sealed record ChatCheckResult(bool Ok, long LatencyMs, long LoadMs, string? Error, string? ErrorKind);

public sealed record ChatStreamEvent(string Type, string? Content = null, string? Thinking = null,
    long? TotalMs = null, int? OutputTokens = null, string? Error = null, string? ErrorKind = null)
{
    public static ChatStreamEvent Delta(string content) => new("delta", Content: content);
    public static ChatStreamEvent Think(string thinking) => new("thinking", Thinking: thinking);
    public static ChatStreamEvent Done(long totalMs, int outputTokens) => new("done", TotalMs: totalMs, OutputTokens: outputTokens);
    public static ChatStreamEvent Fail(string kind, string message) => new("error", Error: message, ErrorKind: kind);
}

public sealed record ChatCompletionRequest(string Model, IReadOnlyList<ChatMessage> Messages, int MaxPredictTokens);

/// <summary>Ollama 호출 경계(#338). keep_alive·num_ctx는 보내지 않아 서버 기본값을 존중한다.</summary>
public interface IChatModelGateway
{
    Task<IReadOnlyList<ChatModelInfo>> ListModelsAsync(CancellationToken ct);
    Task<ChatCheckResult> CheckAsync(string model, CancellationToken ct);
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatCompletionRequest request, CancellationToken ct);
}

public static class ChatMessages
{
    public static string ForKind(string kind, string ollamaUrl, string? detail = null) => kind switch
    {
        ChatErrorKinds.Unreachable => $"Ollama에 연결할 수 없습니다({ollamaUrl}). 서버가 켜져 있는지 확인하세요.",
        ChatErrorKinds.Busy => "Ollama 대기열이 가득 찼습니다(503). 잠시 후 다시 시도하세요.",
        ChatErrorKinds.Timeout => "응답 시간이 초과되었습니다. 모델 로딩 중이거나 서버가 바쁠 수 있습니다.",
        ChatErrorKinds.ModelNotFound => $"모델을 찾을 수 없습니다{(string.IsNullOrEmpty(detail) ? "" : $"({detail})")}. 모델 목록을 새로고침하세요.",
        ChatErrorKinds.Cancelled => "요청이 취소되었습니다.",
        _ => $"Ollama 오류{(string.IsNullOrEmpty(detail) ? "" : $": {detail}")}",
    };

    public const string LocalBusy = "다른 대화가 진행 중입니다. 잠시 후 다시 시도하세요.";
}
