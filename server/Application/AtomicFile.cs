namespace Astra.Server.Application;

/// <summary>App_Data 원자적 교체의 일시적 접근 거부·공유 위반을 짧은 지수 백오프로 재시도한다 (#385).</summary>
public static class AtomicFile
{
    public const int DefaultMaxAttempts = 5;

    public static async Task WriteReplaceAsync(string destination, string content, CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts, Func<int, CancellationToken, Task>? delay = null)
    {
        var temporary = destination + ".tmp";
        try
        {
            await RetryAsync(async () =>
            {
                await File.WriteAllTextAsync(temporary, content, ct);
                File.Move(temporary, destination, true);
            }, ct, maxAttempts, delay);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public static void Move(string source, string destination, bool overwrite,
        int maxAttempts = DefaultMaxAttempts, Action<int>? delay = null)
    {
        delay ??= DefaultSyncDelay;
        for (var attempt = 1; ; attempt++)
        {
            try { File.Move(source, destination, overwrite); return; }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex)) { delay(attempt); }
        }
    }

    public static async Task RetryAsync(Func<Task> operation, CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts, Func<int, CancellationToken, Task>? delay = null)
    {
        delay ??= DefaultAsyncDelay;
        for (var attempt = 1; ; attempt++)
        {
            try { await operation(); return; }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex)) { await delay(attempt, ct); }
        }
    }

    public static bool IsTransient(Exception exception)
        => exception is UnauthorizedAccessException
            || (exception is IOException io && IsSharingViolation(io));

    static bool IsSharingViolation(IOException io)
    {
        var code = io.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(400, 25 * (1 << (attempt - 1))));

    static Task DefaultAsyncDelay(int attempt, CancellationToken ct) => Task.Delay(Backoff(attempt), ct);

    static void DefaultSyncDelay(int attempt) => Thread.Sleep(Backoff(attempt));

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
