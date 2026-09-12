using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>뉴스 전용 파일 어댑터(#151 §5). `App_Data/news` 아래에만 쓴다.</summary>
public sealed class NewsStore(IWebHostEnvironment env) : INewsStore
{
    readonly string _root = Path.Combine(env.ContentRootPath, "App_Data", "news");
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<long> SizeAsync(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { var info = new FileInfo(Path.Combine(_root, file)); return info.Exists ? info.Length : 0; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_root, file);
            return File.Exists(path) ? await File.ReadAllLinesAsync(path, ct) : [];
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(string file, string line, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            await File.AppendAllTextAsync(Path.Combine(_root, file), line + "\n", ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ReadTextAsync(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_root, file);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteTextAsync(string file, string content, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, file);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, content, ct);
            File.Move(temporary, path, true);
        }
        finally { _gate.Release(); }
    }
}
