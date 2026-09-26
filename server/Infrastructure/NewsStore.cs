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

    public async Task<int> FilterLinesAsync(string file, Func<string, bool> keep, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_root, file);
            if (!File.Exists(path)) return 0;
            var lines = await File.ReadAllLinesAsync(path, ct);
            var retained = lines.Where(keep).ToArray();
            var removed = lines.Length - retained.Length;
            if (removed == 0) return 0;
            Directory.CreateDirectory(_root);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary,
                string.Join("\n", retained) + (retained.Length > 0 ? "\n" : ""), ct);
            File.Move(temporary, path, true);
            return removed;
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

    public async Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return Directory.Exists(_root) ? Directory.GetFiles(_root).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToArray() : []; }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_root, file);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }
}
