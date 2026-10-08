using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>
/// 실체결 파일 어댑터(#131). `App_Data/real-fills` 아래에만 쓰고 기존 운영 파일을 건드리지 않는다.
/// </summary>
public sealed class RealFillStore(IWebHostEnvironment env) : IRealFillStore
{
    readonly string _root = Path.Combine(env.ContentRootPath, "App_Data", "real-fills");
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> ReadAsync(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_root, file);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string file, string content, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, file);
            await AtomicFile.WriteReplaceAsync(path, content, ct);
        }
        finally { _gate.Release(); }
    }
}
