using System.Collections.Immutable;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D3 — 설계 §10 상태 전이 + §16B "시각과 재시작"의 순수 로직.
// Application이 시각·저장·gate를 담당하고 여기서는 계산만 한다(§4: Domain은 현재 시각을 직접 읽지 않는다).
// 되살리기 금지·watermark·tombstone은 재시작 후에도 같은 결론이 나와야 하므로 전부 입력에서만 유도한다.

/// <summary>
/// 종목별 지속 래치(§16B). 세션 또는 PolicyHash가 바뀌면 새로 시작한다.
/// Tombstones는 종결된 EventId의 상태이며 어떤 이유로도 되살리지 않는다.
/// </summary>
public sealed record StructuralLatch(string Symbol, DateTimeOffset SessionStart, string PolicyHash,
    DateTimeOffset? WatermarkBarStart, bool Seeded,
    ImmutableDictionary<string, CandidateDisposition> Tombstones,
    ImmutableHashSet<string> ConsumedGuardKeys, ImmutableHashSet<string> RetiredZoneIds,
    string? LastEventSignature, string? LastObservationId)
{
    public static StructuralLatch Empty(string symbol, DateTimeOffset sessionStart, string policyHash) =>
        new(symbol, sessionStart, policyHash, null, false,
            ImmutableDictionary<string, CandidateDisposition>.Empty,
            ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty, null, null);

    /// <summary>복원된 래치가 현재 세션·정책에 해당하는지. 다르면 승계하지 않는다.</summary>
    public bool Matches(DateTimeOffset sessionStart, string policyHash) =>
        SessionStart == sessionStart && string.Equals(PolicyHash, policyHash, StringComparison.Ordinal);
}

/// <summary>
/// 최신 완료 봉을 신규 트리거로 평가할 수 있는지에 대한 판정(§16B).
/// 놓친 중간 봉은 구조 복원·기존 OPEN 청산에만 쓰이고 신규 진입·알림을 만들지 않는다.
/// </summary>
public sealed record TriggerGate(bool AllowNewTrigger, int MissedBars, bool Stale, bool AlreadyEvaluated,
    ImmutableArray<string> Notes, ImmutableArray<string> Blockers);

public static class StructuralLifecycle
{
    public const string NoteWatermarkSeeded = "WATERMARK_SEEDED_NO_NEW_TRIGGER";
    public const string NoteMissedBars = "MISSED_BARS_REPLAY_ONLY";
    public const string NoteBarAlreadyEvaluated = "BAR_ALREADY_EVALUATED";
    public const string NoteTombstoned = "TOMBSTONED_EVENT_NOT_REVIVED";
    public const string NoteLiveInvalidated = "LIVE_PRICE_BROKE_INVALIDATION";
    public const string NoteTtlExpired = "CANDIDATE_TTL_EXPIRED";
    public const string CodeDuplicateGuard = "DUPLICATE_TRIGGER_GUARD";
    public const string CodeNewTriggerSuppressed = "NEW_TRIGGER_SUPPRESSED";

    /// <summary>
    /// §16B: 첫 시작/재시작/새 관심종목 등록 시 최신 완료 봉까지 watermark를 설정하고 그 봉으로 신규 트리거를 만들지 않는다.
    /// 이후 polling에서 watermark보다 새로운 최신 완료 봉 하나만 신규 트리거로 평가한다.
    /// 최신 완료 봉의 종료가 현재보다 <see cref="StructurePolicy.LatestBarMaxAgeSeconds"/>를 넘게 오래됐으면 신규 후보 금지다.
    /// </summary>
    public static TriggerGate Gate(StructuralLatch latch, DateTimeOffset? latestBarStart, DateTimeOffset? latestBarEnd,
        TimeSpan barDuration, DateTimeOffset now, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(latch);
        ArgumentNullException.ThrowIfNull(policy);
        if (barDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(barDuration));

        var notes = new SortedSet<string>(StringComparer.Ordinal);
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        if (latestBarStart is null || latestBarEnd is null)
            return new TriggerGate(false, 0, false, false, ImmutableArray<string>.Empty,
                [SetupDetector.WarningNoTriggerBar]);

        var stale = (now - latestBarEnd.Value).TotalSeconds > policy.LatestBarMaxAgeSeconds;
        if (stale) { blockers.Add(SetupDetector.BlockerStaleLatestBar); notes.Add(SetupDetector.BlockerStaleLatestBar); }

        var missed = 0;
        var alreadyEvaluated = false;
        var allow = true;

        if (!latch.Seeded || latch.WatermarkBarStart is null)
        {
            allow = false;
            notes.Add(NoteWatermarkSeeded);
        }
        else if (latestBarStart.Value <= latch.WatermarkBarStart.Value)
        {
            allow = false;
            alreadyEvaluated = true;
            notes.Add(NoteBarAlreadyEvaluated);
        }
        else
        {
            var steps = (latestBarStart.Value - latch.WatermarkBarStart.Value).Ticks / barDuration.Ticks;
            missed = (int)Math.Max(steps - 1, 0);
            if (missed > 0) notes.Add(NoteMissedBars);
        }

        if (stale) allow = false;
        return new TriggerGate(allow, missed, stale, alreadyEvaluated, notes.ToImmutableArray(), blockers.ToImmutableArray());
    }

    /// <summary>
    /// 지속 상태를 계산 결과에 적용한다. 종결된 EventId는 tombstone 상태를 유지하고 계획을 되살리지 않으며,
    /// 이미 소비된 중복 방지 키의 신규 READY는 거절한다(§8, §16B).
    /// </summary>
    public static ImmutableArray<EntryCandidate> ApplyLatch(StructuralLatch latch,
        ImmutableArray<EntryCandidate> candidates, bool allowNewTrigger)
    {
        ArgumentNullException.ThrowIfNull(latch);
        var result = ImmutableArray.CreateBuilder<EntryCandidate>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (latch.Tombstones.TryGetValue(candidate.EventId, out var prior))
            {
                result.Add(candidate with
                {
                    Disposition = CandidateSelection.Reconcile(prior, candidate.Disposition),
                    Plan = null,
                    Notes = Add(candidate.Notes, NoteTombstoned)
                });
                continue;
            }
            if (!allowNewTrigger)
            {
                result.Add(candidate with
                {
                    Disposition = CandidateDisposition.Wait,
                    Plan = null,
                    Notes = Add(candidate.Notes, CodeNewTriggerSuppressed)
                });
                continue;
            }
            if (candidate.Disposition == CandidateDisposition.Ready &&
                latch.ConsumedGuardKeys.Contains(candidate.DuplicateGuardKey))
            {
                result.Add(candidate with
                {
                    Disposition = CandidateDisposition.Rejected,
                    Plan = null,
                    RejectionCodes = Add(candidate.RejectionCodes, CodeDuplicateGuard)
                });
                continue;
            }
            result.Add(candidate);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// 새 완료 봉이 없는 poll에서도 매번 확인하는 실시간 유지 조건·만료(§12.4, §10).
    /// 상태는 종결 방향으로만 움직이며 재상승·호가 개선으로 되살리지 않는다.
    /// </summary>
    public static ImmutableArray<EntryCandidate> ApplyLive(ImmutableArray<EntryCandidate> candidates,
        decimal? livePrice, DateTimeOffset now)
    {
        var result = ImmutableArray.CreateBuilder<EntryCandidate>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (CandidateSelection.IsTerminal(candidate.Disposition)) { result.Add(candidate); continue; }
            if (livePrice is { } live && Breaks(candidate, live))
            {
                result.Add(candidate with
                {
                    Disposition = CandidateDisposition.Invalidated,
                    Plan = null,
                    Notes = Add(candidate.Notes, NoteLiveInvalidated)
                });
                continue;
            }
            if (now >= candidate.ExpiresAt)
            {
                result.Add(candidate with
                {
                    Disposition = CandidateDisposition.Expired,
                    Plan = null,
                    Notes = Add(candidate.Notes, NoteTtlExpired)
                });
                continue;
            }
            result.Add(candidate);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// 실시간 가격이 무효화 근거를 깼는지. 계획이 있으면 구조 손절과(돌파는) 돌파 수준을 함께 본다.
    /// 계획이 없는 후보는 anchor 자체를 기준으로 하되 돌파는 anchor(=구간 Lower)로 넓히지 않는다(§8).
    /// </summary>
    static bool Breaks(EntryCandidate candidate, decimal live)
    {
        if (candidate.Plan is { } plan)
        {
            if (live <= plan.Stop) return true;
            if (candidate.Kind == SetupKind.Breakout && live <= plan.InvalidationZoneSnapshot.Upper) return true;
            return false;
        }
        return candidate.Kind != SetupKind.Breakout && candidate.InvalidationAnchor is { } anchor && live < anchor;
    }

    /// <summary>
    /// 저장이 성공한 뒤에만 호출한다(§12.6). 실패했는데 이벤트를 소비한 것으로 남기지 않는다.
    /// </summary>
    public static StructuralLatch Commit(StructuralLatch latch, DateTimeOffset? evaluatedBarStart,
        ImmutableArray<EntryCandidate> candidates, IEnumerable<string> retiredZoneIds,
        string? eventSignature, string? observationId)
    {
        ArgumentNullException.ThrowIfNull(latch);
        ArgumentNullException.ThrowIfNull(retiredZoneIds);
        var watermark = latch.WatermarkBarStart;
        if (evaluatedBarStart is { } start && (watermark is null || start > watermark.Value)) watermark = start;

        var tombstones = latch.Tombstones;
        var guards = latch.ConsumedGuardKeys;
        foreach (var candidate in candidates)
        {
            if (CandidateSelection.IsTerminal(candidate.Disposition))
                tombstones = tombstones.SetItem(candidate.EventId, candidate.Disposition);
            if (candidate.Disposition is CandidateDisposition.Ready or CandidateDisposition.Entered)
                guards = guards.Add(candidate.DuplicateGuardKey);
        }

        return latch with
        {
            WatermarkBarStart = watermark,
            Seeded = true,
            Tombstones = tombstones,
            ConsumedGuardKeys = guards,
            RetiredZoneIds = latch.RetiredZoneIds.Union(retiredZoneIds),
            LastEventSignature = eventSignature ?? latch.LastEventSignature,
            LastObservationId = observationId ?? latch.LastObservationId
        };
    }

    /// <summary>이벤트 상태 변화 감지용 canonical 서명. 변하면 full snapshot을 기록한다(§16).</summary>
    public static string EventSignature(ImmutableArray<EntryCandidate> candidates, string? preferredCandidateId) =>
        string.Join(';', candidates
            .Select(x => $"{x.EventId}={x.Disposition}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .Append($"preferred={preferredCandidateId ?? "null"}"));

    /// <summary>
    /// §16: 동일 symbol/lastCompletedBar/policyHash 관측은 한 번만 기록한다.
    /// 재시작·재실행 후에도 같은 값이 나오는 stable ID여야 append 중복을 막을 수 있다.
    /// </summary>
    public static string ObservationId(string symbol, DateTimeOffset lastCompletedBarStart, string policyHash) =>
        StructureMath.SourceId("observation", symbol, StructureMath.Iso(lastCompletedBarStart), policyHash);

    static ImmutableArray<string> Add(ImmutableArray<string> values, string value) =>
        values.Contains(value, StringComparer.Ordinal)
            ? values
            : values.Append(value).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
}
