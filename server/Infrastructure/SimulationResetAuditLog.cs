using System.Text.Json;
using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>리셋 감사 장부: App_Data/sim-reset-audit.jsonl에 append하고 경고 로그를 남긴다.</summary>
public sealed class SimulationResetAuditLog(IWebHostEnvironment env, ILogger<SimulationResetAuditLog> logger) : ISimulationResetAuditLog
{
    public const string FileName = "sim-reset-audit.jsonl";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    readonly string _root = Path.Combine(env.ContentRootPath, "App_Data");
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(SimulationResetAuditEntry entry)
    {
        logger.LogWarning("시뮬 이력 초기화: 삭제 {Removed}건 · 유지 {Kept}건 · 백업 {Backup} · 요청 {Remote} · UA {UserAgent}",
            entry.Removed, entry.Kept, entry.Backup ?? "없음", entry.RemoteAddress ?? "?", entry.UserAgent ?? "?");
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_root);
            await File.AppendAllTextAsync(Path.Combine(_root, FileName), JsonSerializer.Serialize(entry, Json) + "\n");
        }
        finally { _gate.Release(); }
    }
}
