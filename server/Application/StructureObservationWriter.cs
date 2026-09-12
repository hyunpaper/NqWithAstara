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
    string[] Warnings, string[] Notes, ConfluenceDto? Confluence = null);

public sealed record ObservationWriteResult(bool Written, bool Duplicate, bool Limited, long FileBytes,
    bool Core = false, bool ZonesOmitted = false, bool CoreLimited = false,
    long RoutineDropCount = 0, long CoreDropCount = 0);

/// <summary>
/// 한도 도달로 버려진 관측의 장부(설계 §16 "조용히 삭제하지 않는다", 이슈 #44).
/// 관측 본문을 남길 수 없을 때에도 "몇 건이, 언제부터 언제까지, 왜" 사라졌는지는 반드시 남는다.
/// 관측 jsonl과 별도 파일이라 기존 소비자(#27 코호트·#28 검증)의 파싱은 영향을 받지 않는다.
/// 크기는 파일당 고정(단일 JSON 객체)이므로 일일 디스크 상한을 늘리지 않는다.
/// </summary>
public sealed record ObservationDropLedger(string File, long LimitBytes, long CoreReserveBytes,
    long RoutineDropCount, long CoreDropCount, DateTimeOffset? FirstDroppedAt, DateTimeOffset? LastDroppedAt,
    string? FirstDroppedObservationId, string? LastDroppedObservationId, string[] Reasons);

/// <summary>
/// 설계 §16. `structure-observations-YYYY-MM-DD.jsonl`에 append하며 동일 observation ID는 한 번만 기록한다.
/// stable observation ID로 재시작·재실행 후의 append 중복도 막는다.
///
/// 이슈 #44 보존 우선순위: 일자 상한은 그대로 두되(디스크 상한 불변) 상한의 마지막
/// <see cref="StructurePolicy.ObservationCoreReserveRatio"/> 구간은 핵심 관측 전용 예비 예산이다.
/// 주기 WAIT 요약이 먼저 희생되고, 예비 구간에서는 핵심 관측을 zones 배열 없이(§16 "매 15초 전체 Zone 배열을
/// 덤프하지 않는다") 축약 저장해 진입·READY·거절 근거를 끝까지 남긴다. 버려진 건수는 drop 장부에 남는다.
/// </summary>
public sealed class StructureObservationWriter(IStructureObservationStore store, StructurePolicy? policy = null)
{
    public const string LimitWarning = "ObservationStorageLimited";

    /// <summary>핵심 예비 예산까지 소진되어 상태 전이 관측 자체가 누락된 상태. 전체 검증 불가를 뜻한다.</summary>
    public const string CoreLimitWarning = "ObservationCoreStorageLimited";

    public const string RoutineDropReason = "RoutineBudgetExhausted";
    public const string CoreDropReason = "CoreBudgetExhausted";

    /// <summary>§16 요약 관측의 후보 요약 값. 이 조합만 "주기 소음"으로 보고 먼저 버린다.</summary>
    const string SummaryDetail = "summary";
    const string WaitSummary = "WAIT";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly StructurePolicy _policy = policy ?? StructurePolicy.Default;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly HashSet<string> _known = new(StringComparer.Ordinal);
    string? _loadedFile;
    long _bytes;
    bool _limited;
    bool _coreLimited;
    ObservationDropLedger? _drops;

    /// <summary>래치 지속 저장도 같은 v5 전용 저장소를 쓴다. 실제 거래 저장소를 건드리지 않는다.</summary>
    public IStructureObservationStore Store => store;

    public bool Limited => _limited;

    /// <summary>핵심 관측까지 버려졌는가. true면 그 일자 분석은 전체 검증이 불가능하다.</summary>
    public bool CoreLimited => _coreLimited;

    /// <summary>현재 일자의 누락 장부(없으면 null). 없는 이벤트를 0건으로 보고하지 않기 위한 근거다.</summary>
    public ObservationDropLedger? Drops => _drops;

    public static string FileName(DateOnly tradingDate) =>
        $"structure-observations-{tradingDate:yyyy-MM-dd}.jsonl";

    /// <summary>관측 jsonl과 분리된 누락 장부 파일. glob `structure-observations-*.jsonl`에 걸리지 않는다.</summary>
    public static string DropLedgerFileName(DateOnly tradingDate) =>
        $"structure-observation-drops-{tradingDate:yyyy-MM-dd}.json";

    /// <summary>
    /// 주기 소음의 정의: 이벤트 서명이 그대로여서 요약으로 내려간 WAIT 관측. 그 외(=full 스냅샷,
    /// READY/ENTERED/REJECTED/INVALIDATED/EXPIRED 요약)는 전부 후보 결정·상태 전이이므로 핵심으로 본다.
    /// </summary>
    public static bool IsRoutine(StructureObservationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Equals(record.Detail, SummaryDetail, StringComparison.Ordinal) &&
               string.Equals(record.CandidateSummary, WaitSummary, StringComparison.Ordinal);
    }

    public static bool IsCore(StructureObservationRecord record) => !IsRoutine(record);

    /// <summary>한도 상태를 API 경고 코드로 옮긴다(§16 `ObservationStorageLimited` 노출).</summary>
    public static ImmutableArray<string> StorageWarnings(ObservationWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Limited && !result.CoreLimited) return ImmutableArray<string>.Empty;
        var builder = ImmutableArray.CreateBuilder<string>(2);
        if (result.Limited) builder.Add(LimitWarning);
        if (result.CoreLimited) builder.Add(CoreLimitWarning);
        return builder.ToImmutable();
    }

    /// <summary>핵심 예비 예산 크기. 일자 상한 안의 구간이며 상한을 넘겨 늘리지 않는다.</summary>
    public long CoreReserveBytes => Reserve(_policy);

    static long Reserve(StructurePolicy policy)
    {
        var limit = policy.ObservationDailyByteLimit;
        if (limit <= 0) return 0;
        var ratio = double.IsFinite(policy.ObservationCoreReserveRatio)
            ? Math.Clamp(policy.ObservationCoreReserveRatio, 0, 1)
            : 0;
        return (long)(limit * ratio);
    }

    public async Task<ObservationWriteResult> AppendAsync(StructureObservationRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        // 일자 기준은 New York 거래일이다(§16). UTC/KST 날짜로 나누지 않는다.
        var tradingDate = MarketRules.TradingDate(record.SessionStart);
        var file = FileName(tradingDate);
        var line = JsonSerializer.Serialize(record, Json);
        var size = Encoding.UTF8.GetByteCount(line) + 1;

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.Equals(_loadedFile, file, StringComparison.Ordinal))
            {
                _known.Clear();
                _limited = false;
                _coreLimited = false;
                _bytes = await store.SizeAsync(file, ct);
                foreach (var id in await ExistingIdsAsync(file, ct)) _known.Add(id);
                // 재시작해도 이미 발생한 누락을 0건으로 되돌리지 않는다(§16 "조용히 삭제하지 않는다").
                _drops = await ReadLedgerAsync(DropLedgerFileName(tradingDate), ct);
                _limited = _drops is { RoutineDropCount: > 0 } or { CoreDropCount: > 0 };
                _coreLimited = _drops is { CoreDropCount: > 0 };
                _loadedFile = file;
            }

            if (_known.Contains(record.ObservationId))
                return Result(false, true);

            var limit = _policy.ObservationDailyByteLimit;
            var routineCap = Math.Max(0, limit - Reserve(_policy));

            // 1) 일반 예산 안이면 생성된 그대로 기록한다. 평시 동작과 저장 내용은 완전히 동일하다.
            if (_bytes + size <= routineCap)
            {
                await store.AppendAsync(file, line, ct);
                _bytes += size;
                _known.Add(record.ObservationId);
                return Result(true, false, IsCore(record));
            }

            // 2) 일반 예산 소진. 주기 WAIT 요약은 여기서 버려지고, 그 사실이 경고로 드러난다.
            _limited = true;
            if (IsRoutine(record))
            {
                await RecordDropAsync(tradingDate, record, RoutineDropReason, ct);
                return Result(false, false);
            }

            // 3) 핵심 관측은 예비 예산으로 간다. 반복되는 전체 Zone 배열만 덜어내고(§16) 결정 근거는 그대로 둔다.
            var essential = Essential(record);
            var omitted = !ReferenceEquals(essential, record);
            var essentialLine = omitted ? JsonSerializer.Serialize(essential, Json) : line;
            var essentialSize = omitted ? Encoding.UTF8.GetByteCount(essentialLine) + 1 : size;

            if (_bytes + essentialSize > limit)
            {
                // 4) 예비 예산까지 소진. 본문을 남길 수 없으므로 장부에만 남기고 전체 검증 불가를 노출한다.
                _coreLimited = true;
                await RecordDropAsync(tradingDate, record, CoreDropReason, ct);
                return Result(false, false);
            }

            await store.AppendAsync(file, essentialLine, ct);
            _bytes += essentialSize;
            _known.Add(record.ObservationId);
            return Result(true, false, true, omitted);
        }
        finally { _gate.Release(); }
    }

    ObservationWriteResult Result(bool written, bool duplicate, bool core = false, bool omitted = false) =>
        new(written, duplicate, _limited, _bytes, core, omitted, _coreLimited,
            _drops?.RoutineDropCount ?? 0, _drops?.CoreDropCount ?? 0);

    /// <summary>
    /// 축약 형태: 같은 레코드에서 전체 Zone 배열만 비운다. `Zones`는 원래부터 nullable이고 모든 요약 관측이
    /// 이미 null이므로 계약은 바뀌지 않는다. 선택된 구간의 ID·경계는 후보 계획(invalidation/target zone
    /// snapshot)에 그대로 남아 진입 근거 재구성에는 손실이 없다(§11).
    /// 축약 사실은 기존 경고 코드 목록에 `ObservationStorageLimited`로 표시해 사후에 구분 가능하게 한다.
    /// </summary>
    static StructureObservationRecord Essential(StructureObservationRecord record)
    {
        if (record.Zones is not { Length: > 0 }) return record;
        var warnings = record.Warnings.Contains(LimitWarning, StringComparer.Ordinal)
            ? record.Warnings
            : [.. record.Warnings, LimitWarning];
        return record with { Zones = null, Warnings = warnings };
    }

    async Task RecordDropAsync(DateOnly tradingDate, StructureObservationRecord record, string reason,
        CancellationToken ct)
    {
        var current = _drops;
        var reasons = current?.Reasons ?? [];
        if (!reasons.Contains(reason, StringComparer.Ordinal))
            reasons = [.. reasons, reason];
        var next = new ObservationDropLedger(FileName(tradingDate), _policy.ObservationDailyByteLimit,
            Reserve(_policy),
            (current?.RoutineDropCount ?? 0) + (reason == RoutineDropReason ? 1 : 0),
            (current?.CoreDropCount ?? 0) + (reason == CoreDropReason ? 1 : 0),
            current?.FirstDroppedAt ?? record.ObservedAt, record.ObservedAt,
            current?.FirstDroppedObservationId ?? record.ObservationId, record.ObservationId,
            [.. reasons.Order(StringComparer.Ordinal)]);
        // 원자적 교체(.tmp + File.Move)는 저장소 어댑터가 보장한다. 한 줄 JSON이라 개행 규칙도 바뀌지 않는다.
        // 저장이 성공한 뒤에만 메모리 장부를 올려 디스크와 어긋나지 않게 한다(§12.6 저장 성공 후 상태 갱신).
        await store.WriteTextAsync(DropLedgerFileName(tradingDate), JsonSerializer.Serialize(next, Json), ct);
        _drops = next;
    }

    async Task<ObservationDropLedger?> ReadLedgerAsync(string file, CancellationToken ct)
    {
        var text = await store.ReadTextAsync(file, ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        // 손상된 장부는 삭제하지 않고 무시한다. 없는 누락을 새로 만들어내지도 않는다.
        try { return JsonSerializer.Deserialize<ObservationDropLedger>(text, Json); }
        catch (JsonException) { return null; }
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
