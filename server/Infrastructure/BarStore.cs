using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>봉 저장 파일 어댑터(#165 C5). `App_Data/bars` 아래에만 쓴다.</summary>
public sealed class BarStore : IBarStore
{
    readonly string _root;
    readonly SemaphoreSlim _gate = new(1, 1);

    public BarStore(IWebHostEnvironment env) : this(Path.Combine(env.ContentRootPath, "App_Data", "bars"))
    {
    }

    /// <summary>호스트 없이 도는 측정 진입점(#169 C5)이 쓰는 생성자.</summary>
    public BarStore(string root) => _root = root ?? throw new ArgumentNullException(nameof(root));

    public async Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = FilePath(day, symbol);
            if (!File.Exists(path)) return null;
            var lines = await File.ReadAllLinesAsync(path, ct);
            return lines.Length == 0 ? null : lines[^1];
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(string day, string symbol, string line, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, day));
            await File.AppendAllTextAsync(FilePath(day, symbol), line + "\n", ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_root)) return [];
            return Directory.GetDirectories(_root).Select(Path.GetFileName).Where(x => x is not null)
                .Select(x => x!).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var dir = Path.Combine(_root, day);
            if (!Directory.Exists(dir)) return [];
            return Directory.GetFiles(dir, "*.jsonl").Select(Path.GetFileNameWithoutExtension)
                .Where(x => x is not null).Select(x => x!).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = FilePath(day, symbol);
            if (!File.Exists(path)) return 0;
            var lines = await File.ReadAllLinesAsync(path, ct);
            return lines.Count(x => x.Length > 0);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = FilePath(day, symbol);
            if (!File.Exists(path)) return [];
            var lines = await File.ReadAllLinesAsync(path, ct);
            return lines.Where(x => x.Length > 0).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteDayAsync(string day, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var dir = Path.Combine(_root, day);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        finally { _gate.Release(); }
    }

    string FilePath(string day, string symbol) => Path.Combine(_root, day, symbol.ToUpperInvariant() + ".jsonl");
}

public sealed class ReplayBarStoreFactory : IReplayBarStoreFactory
{
    public IBarStore Create(string root) => new BarStore(root);
}

public sealed class ReplayWorkspace(IWebHostEnvironment environment) : IReplayWorkspace
{
    public string Root => Path.Combine(environment.ContentRootPath, "App_Data");
}
