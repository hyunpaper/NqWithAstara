using System.Text.Json;
namespace Astra.Server;

public sealed class LocalStore(IWebHostEnvironment env) : Application.ILocalStore
{
    readonly string _root = Path.Combine(env.ContentRootPath, "App_Data");
    readonly SemaphoreSlim _gate = new(1, 1);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<T> Read<T>(string file, T fallback)
    {
        await _gate.WaitAsync(); try { var p = Path.Combine(_root, file); return File.Exists(p) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(p), Json) ?? fallback : fallback; } finally { _gate.Release(); }
    }
    public async Task Write<T>(string file, T data)
    {
        await _gate.WaitAsync(); try { Directory.CreateDirectory(_root); var p = Path.Combine(_root, file); var t = p + ".tmp"; await File.WriteAllTextAsync(t, JsonSerializer.Serialize(data, Json)); File.Move(t, p, true); } finally { _gate.Release(); }
    }
    public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root); var p = Path.Combine(_root, file);
            var value = File.Exists(p) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(p), Json) ?? fallback : fallback;
            var changed = change(value); var t = p + ".tmp";
            await File.WriteAllTextAsync(t, JsonSerializer.Serialize(changed.Data, Json)); File.Move(t, p, true); return changed.Result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>같은 잠금 안에서 백업 → 변경을 처리한다. 백업 쓰기가 실패하면 원본 파일을 건드리지 않는다 (#228).</summary>
    public async Task<TResult> UpdateWithBackup<T, TResult>(string file, string backupFile, T fallback, Func<T, (T Data, TResult Result)> change)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root); var p = Path.Combine(_root, file);
            var raw = File.Exists(p) ? await File.ReadAllTextAsync(p) : null;
            var value = raw is null ? fallback : JsonSerializer.Deserialize<T>(raw, Json) ?? fallback;
            var backupPath = Path.Combine(_root, backupFile); var backupTmp = backupPath + ".tmp";
            await File.WriteAllTextAsync(backupTmp, raw ?? JsonSerializer.Serialize(value, Json)); File.Move(backupTmp, backupPath, true);
            var changed = change(value); var t = p + ".tmp";
            await File.WriteAllTextAsync(t, JsonSerializer.Serialize(changed.Data, Json)); File.Move(t, p, true); return changed.Result;
        }
        finally { _gate.Release(); }
    }
}
