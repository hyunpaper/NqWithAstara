using System.Text.Json;

namespace Astra.Server.Application.Opening;

public sealed record OpeningScanRecordCounts(int Snapshots, int Followup30, int Close, int DuplicatesSuppressed);

/// <summary>개장 스캔 관측을 `opening-scan-YYYY-MM-DD.jsonl`에 append한다(#371 §4.4). observationId로 재기동 후 중복을 막는다.</summary>
public sealed class OpeningScanObservationWriter(IStructureObservationStore store)
{
    public const string KindSnapshot = "snapshot";
    public const string KindFollowup30 = "followup30";
    public const string KindClose = "close";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly HashSet<string> _known = new(StringComparer.Ordinal);
    string? _loadedFile;
    int _snapshots;
    int _followup30;
    int _close;
    int _duplicates;

    public static string FileName(DateOnly sessionDate) => $"opening-scan-{sessionDate:yyyy-MM-dd}.jsonl";

    public OpeningScanRecordCounts Counts => new(_snapshots, _followup30, _close, _duplicates);

    public async Task<IReadOnlyList<string>> ReadAsync(DateOnly sessionDate, CancellationToken ct) =>
        await store.ReadLinesAsync(FileName(sessionDate), ct);

    public async Task<bool> AppendAsync(DateOnly sessionDate, string observationId, string kind, object record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var file = FileName(sessionDate);
        var line = JsonSerializer.Serialize(record, Json);

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.Equals(_loadedFile, file, StringComparison.Ordinal))
            {
                _known.Clear();
                _snapshots = _followup30 = _close = _duplicates = 0;
                foreach (var id in await ExistingIdsAsync(file, ct)) _known.Add(id);
                _loadedFile = file;
            }

            if (!_known.Add(observationId)) { _duplicates++; return false; }
            await store.AppendAsync(file, line, ct);
            switch (kind)
            {
                case KindSnapshot: _snapshots++; break;
                case KindFollowup30: _followup30++; break;
                case KindClose: _close++; break;
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    async Task<IReadOnlyList<string>> ExistingIdsAsync(string file, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var line in await store.ReadLinesAsync(file, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("observationId", out var value) && value.GetString() is { Length: > 0 } id)
                    ids.Add(id);
            }
            catch (JsonException) { }
        }
        return ids;
    }
}
