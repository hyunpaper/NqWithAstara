using System.Collections.Concurrent;
using System.Text;
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
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    readonly string _root = Path.GetFullPath(root);
    SemaphoreSlim Gate => Gates.GetOrAdd(_root, _ => new SemaphoreSlim(1, 1));

    public async Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            await using var processLock = await OpenExclusiveAsync(Path.Combine(_root, ".append.lock"), ct);
            var existing = await FindUnsafeAsync(snapshot.CaptureId, ct);
            if (existing is not null)
            {
                if (!string.Equals(JsonSerializer.Serialize(existing, Json), JsonSerializer.Serialize(snapshot, Json),
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("동일한 captureId에 다른 snapshot을 추가할 수 없습니다.");
                return ScoreSnapshotAppendResult.AlreadyExists;
            }
            var path = PathFor(snapshot.Score.AsOf);
            await using var stream = await OpenWriterAsync(path, ct);
            RecoverTruncatedTail(stream);
            stream.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, Json) + "\n");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
            return ScoreSnapshotAppendResult.Appended;
        }
        finally { Gate.Release(); }
    }

    public async Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        await Gate.WaitAsync(ct);
        try { return await FindUnsafeAsync(captureId, ct); }
        finally { Gate.Release(); }
    }

    async Task<ScoreCoreShadowSnapshot?> FindUnsafeAsync(string captureId, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return null;
        foreach (var path in Directory.EnumerateFiles(_root, "*.jsonl").Order(StringComparer.Ordinal))
        {
            foreach (var line in await File.ReadAllLinesAsync(path, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ScoreCoreShadowSnapshot? value;
                try { value = JsonSerializer.Deserialize<ScoreCoreShadowSnapshot>(line, Json); }
                catch (JsonException) { continue; }
                if (value is null) continue;
                if (string.Equals(value.CaptureId, captureId, StringComparison.Ordinal)) return value;
            }
        }
        return null;
    }

    string PathFor(DateTimeOffset asOf) => Path.Combine(_root, $"{asOf.UtcDateTime:yyyy-MM-dd}.jsonl");

    static async Task<FileStream> OpenWriterAsync(string path, CancellationToken ct)
        => await OpenAsync(path, FileShare.Read, ct);

    static async Task<FileStream> OpenExclusiveAsync(string path, CancellationToken ct)
        => await OpenAsync(path, FileShare.None, ct);

    static async Task<FileStream> OpenAsync(string path, FileShare share, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, share,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }
    }

    static void RecoverTruncatedTail(FileStream stream)
    {
        if (stream.Length == 0) return;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n') return;
        for (var position = stream.Length - 1; position >= 0; position--)
        {
            stream.Seek(position, SeekOrigin.Begin);
            if (stream.ReadByte() != '\n') continue;
            stream.SetLength(position + 1);
            return;
        }
        stream.SetLength(0);
    }
}
