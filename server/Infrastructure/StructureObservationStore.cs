using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>
/// v5 관측·래치 파일 어댑터(설계 §16). `App_Data/structure` 아래에만 쓰며 기존 운영 파일을 건드리지 않는다.
/// 사용자가 파일 단위로 확인·삭제할 수 있게 일자별 jsonl을 그대로 남기고 조용히 지우지 않는다.
/// </summary>
public sealed class StructureObservationStore(IWebHostEnvironment env) : IStructureObservationStore
{
    readonly string _root = Path.Combine(env.ContentRootPath, "App_Data", "structure");
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
            // jsonl은 플랫폼과 무관하게 LF 한 줄이다. 바이트 상한 계산과 줄 수가 어긋나지 않게 고정한다.
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
