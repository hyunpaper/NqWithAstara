using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application.ScoreCore;

namespace Astra.Server.Infrastructure.ScoreCore;

public sealed class JsonlScoreCoreSnapshotStore(string root) : IScoreCoreSnapshotStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    readonly string _root = Path.GetFullPath(root);
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            var existing = await FindUnsafeAsync(snapshot.CaptureId, ct);
            if (existing is not null)
            {
                if (!string.Equals(JsonSerializer.Serialize(existing, Json), JsonSerializer.Serialize(snapshot, Json),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("동일한 captureId에 다른 snapshot을 추가할 수 없습니다.");
                return ScoreSnapshotAppendResult.AlreadyExists;
            }
            await File.AppendAllTextAsync(PathFor(snapshot.Score.AsOf),
                JsonSerializer.Serialize(snapshot, Json) + Environment.NewLine, ct);
            return ScoreSnapshotAppendResult.Appended;
        }
        finally { _gate.Release(); }
    }

    public async Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        await _gate.WaitAsync(ct);
        try { return await FindUnsafeAsync(captureId, ct); }
        finally { _gate.Release(); }
    }

    async Task<ScoreCoreShadowSnapshot?> FindUnsafeAsync(string captureId, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return null;
        foreach (var path in Directory.EnumerateFiles(_root, "*.jsonl").Order(StringComparer.Ordinal))
        {
            foreach (var line in await File.ReadAllLinesAsync(path, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var value = JsonSerializer.Deserialize<ScoreCoreShadowSnapshot>(line, Json)
                    ?? throw new InvalidDataException($"Score Core snapshot을 읽을 수 없습니다: {Path.GetFileName(path)}");
                if (string.Equals(value.CaptureId, captureId, StringComparison.Ordinal)) return value;
            }
        }
        return null;
    }

    string PathFor(DateTimeOffset asOf) => Path.Combine(_root, $"{asOf.UtcDateTime:yyyy-MM-dd}.jsonl");
}
