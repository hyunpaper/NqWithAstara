namespace Astra.Server.Application;

/// <summary>백그라운드 루프 1회 반복을 격리한다. 취소만 전파하고 나머지 예외는 로그 후 삼켜 호스트를 살려 둔다 (#385).</summary>
public static class BackgroundIteration
{
    public static async Task GuardAsync(Func<Task> body, IMonitorDiagnostics diagnostics, string scope,
        CancellationToken ct)
    {
        try { await body(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { diagnostics.PollFailed(scope, exception); }
    }
}
