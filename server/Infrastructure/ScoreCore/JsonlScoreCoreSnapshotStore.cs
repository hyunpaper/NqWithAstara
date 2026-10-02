using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Server.Application;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Infrastructure.ScoreCore;

/// <summary>Score Core shadow snapshot의 날짜별 append-only jsonl 저장소(§6, #309).</summary>
public sealed class JsonlScoreCoreSnapshotStore(string root, IMonitorDiagnostics? diagnostics = null)
    : IScoreCoreSnapshotStore
{
    const string DateFormat = "yyyy-MM-dd";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    readonly string _root = Path.GetFullPath(root);
    readonly Dictionary<string, DayIndex> _days = new(StringComparer.Ordinal);
    SemaphoreSlim Gate => Gates.GetOrAdd(_root, _ => new SemaphoreSlim(1, 1));

    public long RecoveredTailBytes { get; private set; }

    public async Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            await using var processLock = await OpenExclusiveAsync(Path.Combine(_root, ".append.lock"), ct);
            var path = PathFor(snapshot.Score.AsOf);
            var index = await RefreshAsync(path, ct);
            if (index.Ids.Contains(snapshot.CaptureId))
            {
                var existing = await ReadSnapshotAsync(path, snapshot.CaptureId, ct);
                if (existing is not null && !string.Equals(JsonSerializer.Serialize(existing, Json),
                        JsonSerializer.Serialize(snapshot, Json), StringComparison.Ordinal))
                    throw new InvalidOperationException("동일한 captureId에 다른 snapshot을 추가할 수 없습니다.");
                return ScoreSnapshotAppendResult.AlreadyExists;
            }
            if (snapshot.ContentKey is { Length: > 0 } contentKey &&
                index.Latest.TryGetValue(TargetKey(snapshot.Score.TargetKind, snapshot.Score.TargetId), out var latest) &&
                string.Equals(latest.ContentKey, contentKey, StringComparison.Ordinal) &&
                snapshot.Score.AsOf >= latest.AsOf)
                return ScoreSnapshotAppendResult.Unchanged;
            await using var stream = await OpenWriterAsync(path, ct);
            var removed = RecoverTruncatedTail(stream);
            if (removed > 0)
            {
                RecoveredTailBytes += removed;
                diagnostics?.PollFailed("score-core-store", new InvalidDataException(
                    $"{Path.GetFileName(path)} 잘린 꼬리 {removed}바이트를 제거했습니다."));
            }
            stream.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, Json) + "\n");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            stream.Flush(true);
            index.Add(snapshot.CaptureId, snapshot.Score.TargetKind, snapshot.Score.TargetId, snapshot.Score.AsOf,
                snapshot.ContentKey);
            index.KnownLength = stream.Length;
            return ScoreSnapshotAppendResult.Appended;
        }
        finally { Gate.Release(); }
    }

    public async Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        await Gate.WaitAsync(ct);
        try
        {
            foreach (var path in DatedFiles().Reverse())
            {
                if (!(await RefreshAsync(path, ct)).Ids.Contains(captureId)) continue;
                if (await ReadSnapshotAsync(path, captureId, ct) is { } found) return found;
            }
            return null;
        }
        finally { Gate.Release(); }
    }

    public async Task<ScoreCoreShadowSnapshot?> FindLatestAsync(ImpactTargetKind kind, string targetId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        await Gate.WaitAsync(ct);
        try
        {
            var key = TargetKey(kind, targetId);
            foreach (var path in DatedFiles().Reverse())
            {
                var index = await RefreshAsync(path, ct);
                if (index.Latest.TryGetValue(key, out var latest))
                    return await ReadSnapshotAsync(path, latest.CaptureId, ct);
            }
            return null;
        }
        finally { Gate.Release(); }
    }

    public async Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-Math.Max(1, retentionDays));
        await Gate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_root)) return 0;
            await using var processLock = await OpenExclusiveAsync(Path.Combine(_root, ".append.lock"), ct);
            var deleted = 0;
            foreach (var path in DatedFiles())
            {
                if (FileDate(path) is not { } date || date >= cutoff) continue;
                File.Delete(path);
                _days.Remove(path);
                deleted++;
            }
            return deleted;
        }
        finally { Gate.Release(); }
    }

    IEnumerable<string> DatedFiles() => !Directory.Exists(_root)
        ? []
        : Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(x => FileDate(x) is not null).Order(StringComparer.Ordinal).ToArray();

    static DateOnly? FileDate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length != DateFormat.Length + 6 || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return null;
        return DateOnly.TryParseExact(name[..DateFormat.Length], DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
    }

    async Task<DayIndex> RefreshAsync(string path, CancellationToken ct)
    {
        if (!_days.TryGetValue(path, out var index)) _days[path] = index = new DayIndex();
        if (!File.Exists(path)) { index.Reset(); return index; }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < index.KnownLength) index.Reset();
        if (stream.Length == index.KnownLength) return index;
        stream.Seek(index.KnownLength, SeekOrigin.Begin);
        var position = index.KnownLength;
        var buffer = new byte[64 * 1024];
        using var pending = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pending.Write(buffer, start, i - start);
                IndexLine(index, Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length));
                position += pending.Length + 1;
                pending.SetLength(0);
                start = i + 1;
            }
            pending.Write(buffer, start, read - start);
        }
        index.KnownLength = position;
        return index;
    }

    static void IndexLine(DayIndex index, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        IndexRow? row;
        try { row = JsonSerializer.Deserialize<IndexRow>(line, Json); }
        catch (JsonException) { return; }
        if (row?.CaptureId is not { Length: > 0 } id || row.Score?.TargetId is not { Length: > 0 } target) return;
        index.Add(id, row.Score.TargetKind, target, row.Score.AsOf, row.ContentKey);
    }

    static async Task<ScoreCoreShadowSnapshot?> ReadSnapshotAsync(string path, string captureId, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        var marker = $"\"captureId\":{JsonSerializer.Serialize(captureId)}";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.Contains(marker, StringComparison.Ordinal)) continue;
            ScoreCoreShadowSnapshot? value;
            try { value = JsonSerializer.Deserialize<ScoreCoreShadowSnapshot>(line, Json); }
            catch (JsonException) { continue; }
            if (value is not null && string.Equals(value.CaptureId, captureId, StringComparison.Ordinal)) return value;
        }
        return null;
    }

    string PathFor(DateTimeOffset asOf)
        => Path.Combine(_root, asOf.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture) + ".jsonl");

    static string TargetKey(ImpactTargetKind kind, string targetId) => kind + "|" + targetId.ToUpperInvariant();

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

    static long RecoverTruncatedTail(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0) return 0;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n') return 0;
        for (var position = length - 1; position >= 0; position--)
        {
            stream.Seek(position, SeekOrigin.Begin);
            if (stream.ReadByte() != '\n') continue;
            stream.SetLength(position + 1);
            return length - position - 1;
        }
        stream.SetLength(0);
        return length;
    }

    sealed class DayIndex
    {
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (DateTimeOffset AsOf, string CaptureId, string? ContentKey)> Latest { get; } = new(StringComparer.Ordinal);
        public long KnownLength { get; set; }

        public void Add(string captureId, ImpactTargetKind kind, string targetId, DateTimeOffset asOf,
            string? contentKey)
        {
            Ids.Add(captureId);
            var key = TargetKey(kind, targetId);
            if (!Latest.TryGetValue(key, out var current) || asOf >= current.AsOf) Latest[key] = (asOf, captureId, contentKey);
        }

        public void Reset()
        {
            Ids.Clear();
            Latest.Clear();
            KnownLength = 0;
        }
    }

    sealed record IndexRow(string? CaptureId, IndexScore? Score, string? ContentKey);
    sealed record IndexScore(string? TargetId, ImpactTargetKind TargetKind, DateTimeOffset AsOf);
}
