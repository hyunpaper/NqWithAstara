using System.Runtime.CompilerServices;

namespace Astra.Server.Application.Chat;

public sealed record ChatModelsResponse(bool Enabled, string Status, string? Error, IReadOnlyList<ChatModelItem> Models,
    string? DefaultModel, ChatResourceNotice Resources);

public sealed record ChatModelItem(string Name, long SizeBytes, string? ParameterSize, string? Family, bool IsNewsModel);

/// <summary>뉴스 분류(Ollama)와의 자원 경쟁 안내. 매매 판정에는 영향이 없다.</summary>
public sealed record ChatResourceNotice(bool NewsEnabled, string NewsModel, int NewsQueue, bool ChatBusy);

public sealed record ChatCheckResponse(bool Ok, string Model, long LatencyMs, long LoadMs, string? Error, string? ErrorKind,
    string? Warning, ChatResourceNotice Resources);

public sealed class ChatService(ChatOptions options, NewsOptions news, NewsRuntimeState newsState, IChatModelGateway gateway, TimeProvider clock)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public string OllamaUrl => options.ResolveOllamaUrl(news);

    public bool IsBusy => _gate.CurrentCount == 0;

    public ChatResourceNotice Resources() => new(news.Enabled, news.Model, newsState.Queue, IsBusy);

    public string? ModelWarning(string model)
    {
        if (!news.Enabled || string.Equals(model, news.Model, StringComparison.Ordinal)) return null;
        return $"뉴스 분류 모델({news.Model})과 다릅니다. 모델 교체 시 재로딩으로 뉴스 분류와 채팅 응답이 함께 느려질 수 있습니다(매매 판정 영향 없음).";
    }

    public async Task<ChatModelsResponse> ModelsAsync(CancellationToken ct)
    {
        if (!options.Enabled) return new(false, "disabled", null, [], null, Resources());
        try
        {
            var models = await gateway.ListModelsAsync(ct);
            var items = models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new ChatModelItem(m.Name, m.SizeBytes, m.ParameterSize, m.Family, string.Equals(m.Name, news.Model, StringComparison.Ordinal)))
                .ToArray();
            var fallback = items.FirstOrDefault(i => i.IsNewsModel)?.Name ?? items.FirstOrDefault()?.Name;
            var preferred = !string.IsNullOrWhiteSpace(options.DefaultModel) && items.Any(i => i.Name == options.DefaultModel) ? options.DefaultModel : fallback;
            return new(true, "ok", null, items, preferred, Resources());
        }
        catch (ChatGatewayException ex)
        {
            return new(true, "error", ChatMessages.ForKind(ex.Kind, OllamaUrl, ex.Detail), [], null, Resources());
        }
    }

    public async Task<ChatCheckResponse> CheckAsync(string model, CancellationToken ct)
    {
        var resources = Resources();
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(Math.Max(0, options.BusyWaitSeconds)), ct))
            return new(false, model, 0, 0, ChatMessages.LocalBusy, ChatErrorKinds.Busy, ModelWarning(model), resources);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.CheckTimeoutSeconds)));
            var started = clock.GetTimestamp();
            try
            {
                var result = await gateway.CheckAsync(model, timeout.Token);
                var latency = result.Ok && result.LatencyMs > 0 ? result.LatencyMs : (long)clock.GetElapsedTime(started).TotalMilliseconds;
                var error = result.Error is null ? null : ChatMessages.ForKind(result.ErrorKind ?? ChatErrorKinds.Upstream, OllamaUrl, result.Error);
                return new(result.Ok, model, latency, result.LoadMs, error, result.ErrorKind, ModelWarning(model), resources);
            }
            catch (ChatGatewayException ex)
            {
                return new(false, model, (long)clock.GetElapsedTime(started).TotalMilliseconds, 0, ChatMessages.ForKind(ex.Kind, OllamaUrl, ex.Detail), ex.Kind, ModelWarning(model), resources);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(false, model, (long)clock.GetElapsedTime(started).TotalMilliseconds, 0, ChatMessages.ForKind(ChatErrorKinds.Timeout, OllamaUrl), ChatErrorKinds.Timeout, ModelWarning(model), resources);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>동시 1건만 허용한다. 점유 중이면 첫 이벤트로 busy 오류를 내고 끝낸다.</summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(string model, IReadOnlyList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            yield return ChatStreamEvent.Fail(ChatErrorKinds.Busy, ChatMessages.LocalBusy);
            yield break;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)));
            var request = new ChatCompletionRequest(model, ChatPrompt.Build(history, options), Math.Clamp(options.MaxPredictTokens, 16, 8192));
            var splitter = new ThinkBlockSplitter();
            await using var source = Relay(request, timeout.Token).GetAsyncEnumerator(CancellationToken.None);
            while (true)
            {
                ChatStreamEvent? next = null;
                ChatStreamEvent? failure = null;
                try
                {
                    if (await source.MoveNextAsync()) next = source.Current;
                }
                catch (ChatGatewayException ex)
                {
                    failure = ChatStreamEvent.Fail(ex.Kind, ChatMessages.ForKind(ex.Kind, OllamaUrl, ex.Detail));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }
                catch (OperationCanceledException)
                {
                    failure = ChatStreamEvent.Fail(ChatErrorKinds.Timeout, ChatMessages.ForKind(ChatErrorKinds.Timeout, OllamaUrl));
                }
                if (failure is not null)
                {
                    yield return failure;
                    yield break;
                }
                if (next is null) break;
                if (next.Type == "delta" && next.Content is { Length: > 0 })
                {
                    var (content, thinking) = splitter.Push(next.Content);
                    if (thinking.Length > 0) yield return ChatStreamEvent.Think(thinking);
                    if (content.Length > 0) yield return ChatStreamEvent.Delta(content);
                    continue;
                }
                if (next.Type == "done")
                {
                    var (content, thinking) = splitter.Flush();
                    if (thinking.Length > 0) yield return ChatStreamEvent.Think(thinking);
                    if (content.Length > 0) yield return ChatStreamEvent.Delta(content);
                }
                yield return next;
            }
        }
        finally { _gate.Release(); }
    }

    IAsyncEnumerable<ChatStreamEvent> Relay(ChatCompletionRequest request, CancellationToken ct) => gateway.StreamAsync(request, ct);
}
