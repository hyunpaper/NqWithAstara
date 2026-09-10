using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

// v5 구조 엔진 D3 — 설계 §16 저장량·성능 및 관측.
// 관측은 실제 거래와 별도 파일이며 매 15초 전체 Zone 배열을 덤프하지 않는다.
// 일자 기준은 New York 거래일이고 일자당 상한을 넘으면 더 저장하지 않되 기존 기록을 삭제하지 않는다.

/// <summary>
/// 관측 레코드(§16 관측 필드). Detail="full"은 이벤트/정책 변경 시, 그 외는 요약이다.
/// 미지 값은 null이며 NaN/Infinity는 어떤 필드에도 들어가지 않는다(§11).
/// </summary>
public sealed record StructureObservationRecord(string ObservationId, string RecordVersion, string Symbol,
    DateTimeOffset ObservedAt, DateTimeOffset SessionStart, DateTimeOffset AnalysisAsOf, DateTimeOffset? QuoteAt,
    DateTimeOffset LastCompletedBarStart, string PolicyHash, string EngineVersion, string Mode, string EntryOwner,
    string Detail, string Status, string CandidateSummary, string? PreferredCandidateId, StructureTrendDto? Trend,
    StructureQualityDto? Quality, StructureZoneDto[]? Zones, StructureCandidateDto[] Candidates,
    string[] Warnings, string[] Notes);

public sealed record ObservationWriteResult(bool Written, bool Duplicate, bool Limited, long FileBytes);

/// <summary>
/// 설계 §16. `structure-observations-YYYY-MM-DD.jsonl`에 append하며 동일 observation ID는 한 번만 기록한다.
/// stable observation ID로 재시작·재실행 후의 append 중복도 막는다.
/// </summary>
public sealed class StructureObservationWriter(IStructureObservationStore store, StructurePolicy? policy = null)
{
    public const string LimitWarning = "ObservationStorageLimited";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly StructurePolicy _policy = policy ?? StructurePolicy.Default;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly HashSet<string> _known = new(StringComparer.Ordinal);
    string? _loadedFile;
    long _bytes;
    bool _limited;

    /// <summary>래치 지속 저장도 같은 v5 전용 저장소를 쓴다. 실제 거래 저장소를 건드리지 않는다.</summary>
    public IStructureObservationStore Store => store;

    public bool Limited => _limited;

    public static string FileName(DateOnly tradingDate) =>
        $"structure-observations-{tradingDate:yyyy-MM-dd}.jsonl";

    public async Task<ObservationWriteResult> AppendAsync(StructureObservationRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        // 일자 기준은 New York 거래일이다(§16). UTC/KST 날짜로 나누지 않는다.
        var file = FileName(MarketRules.TradingDate(record.SessionStart));
        var line = JsonSerializer.Serialize(record, Json);
        var size = Encoding.UTF8.GetByteCount(line) + 1;

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.Equals(_loadedFile, file, StringComparison.Ordinal))
            {
                _known.Clear();
                _limited = false;
                _bytes = await store.SizeAsync(file, ct);
                foreach (var id in await ExistingIdsAsync(file, ct)) _known.Add(id);
                _loadedFile = file;
            }

            if (_known.Contains(record.ObservationId))
                return new ObservationWriteResult(false, true, _limited, _bytes);

            if (_bytes + size > _policy.ObservationDailyByteLimit)
            {
                _limited = true;
                return new ObservationWriteResult(false, false, true, _bytes);
            }

            await store.AppendAsync(file, line, ct);
            _bytes += size;
            _known.Add(record.ObservationId);
            return new ObservationWriteResult(true, false, _limited, _bytes);
        }
        finally { _gate.Release(); }
    }

    /// <summary>재시작 후에도 같은 observation ID를 두 번 append하지 않도록 기존 파일의 ID를 한 번 읽는다.</summary>
    async Task<IReadOnlyList<string>> ExistingIdsAsync(string file, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var line in await store.ReadLinesAsync(file, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("observationId", out var value) &&
                    value.GetString() is { Length: > 0 } id) ids.Add(id);
            }
            catch (JsonException) { /* 손상된 줄은 삭제하지 않고 무시한다(§16: 조용히 삭제하지 않는다). */ }
        }
        return ids;
    }
}

/// <summary>
/// 지속 래치의 저장 형식(§16B "watermark와 이벤트 tombstone은 재시작 복원이 가능해야 한다").
/// Domain의 불변 타입을 직렬화 가능한 평범한 형태로 옮기고 되살리기 금지 상태를 그대로 보존한다.
/// </summary>
public sealed record StructureLatchRecord(string Symbol, DateTimeOffset SessionStart, string PolicyHash,
    DateTimeOffset? WatermarkBarStart, bool Seeded, Dictionary<string, string> Tombstones, string[] ConsumedGuardKeys,
    string[] RetiredZoneIds, string? LastEventSignature, string? LastObservationId);

public static class StructureLatchStorage
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize(IEnumerable<StructuralLatch> latches)
    {
        ArgumentNullException.ThrowIfNull(latches);
        var records = latches
            .OrderBy(x => x.Symbol, StringComparer.Ordinal)
            .Select(x => new StructureLatchRecord(x.Symbol, x.SessionStart, x.PolicyHash, x.WatermarkBarStart,
                x.Seeded,
                x.Tombstones.OrderBy(t => t.Key, StringComparer.Ordinal)
                    .ToDictionary(t => t.Key, t => t.Value.ToString(), StringComparer.Ordinal),
                x.ConsumedGuardKeys.Order(StringComparer.Ordinal).ToArray(),
                x.RetiredZoneIds.Order(StringComparer.Ordinal).ToArray(),
                x.LastEventSignature, x.LastObservationId))
            .ToArray();
        return JsonSerializer.Serialize(records, Json);
    }

    public static ImmutableArray<StructuralLatch> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ImmutableArray<StructuralLatch>.Empty;
        var records = JsonSerializer.Deserialize<StructureLatchRecord[]>(text, Json);
        if (records is null) return ImmutableArray<StructuralLatch>.Empty;
        var result = ImmutableArray.CreateBuilder<StructuralLatch>(records.Length);
        foreach (var record in records)
        {
            if (record is null || string.IsNullOrEmpty(record.Symbol)) continue;
            var tombstones = ImmutableDictionary<string, CandidateDisposition>.Empty;
            foreach (var (key, value) in record.Tombstones ?? [])
                if (Enum.TryParse<CandidateDisposition>(value, true, out var disposition))
                    tombstones = tombstones.SetItem(key, disposition);
            result.Add(new StructuralLatch(record.Symbol, record.SessionStart, record.PolicyHash ?? string.Empty,
                record.WatermarkBarStart, record.Seeded, tombstones,
                (record.ConsumedGuardKeys ?? []).ToImmutableHashSet(StringComparer.Ordinal),
                (record.RetiredZoneIds ?? []).ToImmutableHashSet(StringComparer.Ordinal),
                record.LastEventSignature, record.LastObservationId));
        }
        return result.ToImmutable();
    }
}
