using System.Collections.Immutable;
using System.Text.Json;

namespace Astra.Server.Application;

// 이슈 #26 — v5 알림 이벤트 계약(승인 설계안 §4).
// 알림 판정의 단일 권위는 StructureAnalysisService gate 안 commit 지점이고, 이 클래스는 그 지점에서 파생된
// 이벤트의 dedup·영속·공개만 소유한다. FE는 /api/state의 additive `structureEvents`를 소비만 한다.
// off/shadow에서는 호출되지 않으며(§16B "shadow는 진입 알림을 쓰지 않는다") 점수·확률·승률을 만들지 않는다.

/// <summary>
/// 발행된 v5 알림 이벤트. seq는 프로세스 재시작을 넘어 단조 증가한다(복원된 최대값에서 이어짐).
/// EntryQuality/NetR은 후보 계획의 값 그대로이며 승률·확률이 아니다(§29 오인 방지).
/// </summary>
public sealed record StructureAlertEvent(long Seq, string Type, string Symbol, string EventId, string Kind,
    double? EntryQuality, decimal? NetR, decimal? QuotePrice, DateTimeOffset At, DateTimeOffset SessionStart,
    string PolicyHash, string? PlanId, decimal? Stop, decimal? Target, string? Reason);

/// <summary>commit 지점이 넘겨주는 이벤트 초안. seq·시각·세션 정보는 발행 시점에 publisher가 채운다.</summary>
public sealed record StructureAlertDraft(string Type, string EventId, string Kind, double? EntryQuality,
    decimal? NetR, string? PlanId = null, decimal? Stop = null, decimal? Target = null, string? Reason = null);

/// <summary>
/// v5 알림 이벤트 목록의 소유자(승인 설계안 §4). 중복 방지 3중 중 서버 측 1중을 담당한다:
/// (sessionStart, policyHash, eventId, type[, reason]) 키를 `structure-alerts.json`에 영속하고
/// 이미 발행한 키는 재발행하지 않는다. 저장 실패 시 이벤트도 발행하지 않아(예외 전파) 래치 commit이
/// 막히고 다음 poll이 멱등하게 재시도한다(관측 append와 동일한 원자성, §12.6).
/// 재시작 복원 이벤트는 신규가 아니다 — seq가 복원되므로 FE seed가 그대로 유효하다(§16B watermark 준수).
/// </summary>
public sealed class StructureAlertPublisher(IStructureObservationStore store)
{
    public const string AlertsFile = "structure-alerts.json";
    public const string TypeReady = "V5_READY";
    public const string TypeEntered = "V5_ENTERED";
    public const string TypeBlocked = "V5_BLOCKED";
    /// <summary>/api/state에 노출하는 최근 이벤트 수 상한.</summary>
    public const int RecentLimit = 50;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    readonly SemaphoreSlim _gate = new(1, 1);
    ImmutableArray<StructureAlertEvent> _events = ImmutableArray<StructureAlertEvent>.Empty;
    long _seq;
    bool _restored;

    /// <summary>
    /// 초안들을 dedup 후 발행한다. 새 이벤트가 하나라도 있으면 먼저 영속하고, 영속이 성공한 뒤에만
    /// 메모리 목록을 갱신한다. 세션/정책이 바뀌면 지난 키는 보관하지 않는다(초기화) — seq는 리셋하지 않는다.
    /// </summary>
    public async Task PublishAsync(string symbol, DateTimeOffset sessionStart, string policyHash,
        IReadOnlyList<StructureAlertDraft> drafts, decimal? quotePrice, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await RestoreAsync(ct);
            // 세션/정책 변경 시 초기화: 다른 (session, policyHash)의 키·이벤트는 새 세션과 충돌하지 않으므로 버린다.
            var current = _events.Where(x => x.SessionStart == sessionStart &&
                string.Equals(x.PolicyHash, policyHash, StringComparison.Ordinal)).ToList();
            var keys = new HashSet<string>(current.Select(Key), StringComparer.Ordinal);
            var added = false;
            foreach (var draft in drafts)
            {
                var evt = new StructureAlertEvent(_seq + 1, draft.Type, symbol, draft.EventId, draft.Kind,
                    draft.EntryQuality, draft.NetR, quotePrice, at, sessionStart, policyHash,
                    draft.PlanId, draft.Stop, draft.Target, draft.Reason);
                if (!keys.Add(Key(evt))) continue;
                current.Add(evt);
                _seq++;
                added = true;
            }
            if (!added && current.Count == _events.Length) return;

            // 영속 먼저(§12.6 원자성): 실패하면 메모리도 갱신하지 않고 예외를 전파해 래치 commit을 막는다.
            var snapshot = current.OrderBy(x => x.Seq).ToImmutableArray();
            await store.WriteTextAsync(AlertsFile, JsonSerializer.Serialize(snapshot, Json), ct);
            _events = snapshot;
        }
        finally { _gate.Release(); }
    }

    /// <summary>/api/state용 최근 이벤트(seq 오름차순, 최대 <see cref="RecentLimit"/>건). 조회는 계산을 유발하지 않는다.</summary>
    public async Task<ImmutableArray<StructureAlertEvent>> GetRecentAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RestoreAsync(ct);
            var events = _events;
            return events.Length <= RecentLimit ? events : [.. events.Skip(events.Length - RecentLimit)];
        }
        finally { _gate.Release(); }
    }

    static string Key(StructureAlertEvent evt) =>
        $"{evt.SessionStart.UtcTicks}|{evt.PolicyHash}|{evt.EventId}|{evt.Type}|{evt.Reason}";

    async Task RestoreAsync(CancellationToken ct)
    {
        if (_restored) return;
        try
        {
            var text = await store.ReadTextAsync(AlertsFile, ct);
            if (!string.IsNullOrWhiteSpace(text) &&
                JsonSerializer.Deserialize<StructureAlertEvent[]>(text, Json) is { } restored)
            {
                _events = restored.Where(x => x is { EventId.Length: > 0, Type.Length: > 0 })
                    .OrderBy(x => x.Seq).ToImmutableArray();
                _seq = _events.Length == 0 ? 0 : _events[^1].Seq;
            }
        }
        catch (JsonException)
        {
            // 손상된 파일은 빈 상태에서 다시 시작한다. 삭제하지 않고 다음 발행이 덮어쓴다(§16 보수적 복원).
            _events = ImmutableArray<StructureAlertEvent>.Empty;
            _seq = 0;
        }
        _restored = true;
    }
}
