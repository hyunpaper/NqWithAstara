using System.Collections.Immutable;
using System.Globalization;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D3 — 설계 §10 상태 전이 + §16B "시각과 재시작"의 순수 로직.
// Application이 시각·저장·gate를 담당하고 여기서는 계산만 한다(§4: Domain은 현재 시각을 직접 읽지 않는다).
// 되살리기 금지·watermark·tombstone은 재시작 후에도 같은 결론이 나와야 하므로 전부 입력에서만 유도한다.

/// <summary>
/// 종목별 지속 래치(§16B). 세션 또는 PolicyHash가 바뀌면 새로 시작한다.
/// Tombstones는 종결된 EventId의 상태이며 어떤 이유로도 되살리지 않는다.
/// <para>
/// <c>ConsumedGuardKeys</c>는 "이번 세션에서 소비된 lifecycle 키"의 단일 집합이며 두 종류가 들어간다.
/// (1) §8 중복 방지 키 <see cref="EntryCandidate.DuplicateGuardKey"/>,
/// (2) §10 돌파 쿨다운 표식 <see cref="StructuralLifecycle.BreakoutCooldownKey"/>.
/// 두 형식은 접두사로 구분되며 서로 충돌하지 않는다. 쿨다운을 별도 필드로 두면 기존 래치 저장 레코드를
/// 바꿔야 하므로, 이미 재시작 복원되는 이 집합을 그대로 재사용해 새 저장 경로를 만들지 않는다(§16B).
/// </para>
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
    public const string CodeEpisodeConsumed = "TRIGGER_EPISODE_CONSUMED";
    public const string CodeNewTriggerSuppressed = "NEW_TRIGGER_SUPPRESSED";

    /// <summary>
    /// 이슈 #47(§10): "돌파 동일 ZoneId 재발동 쿨다운은 30분". 같은 zone lineage에서 직전 돌파 발동으로부터
    /// <see cref="StructurePolicy.BreakoutCooldownMinutes"/> 이내면 새 트리거라도 READY로 승격하지 않는다.
    /// PULLBACK/REBOUND에는 적용하지 않는다(§10이 돌파만 명시).
    /// </summary>
    public const string CodeBreakoutCooldown = "BREAKOUT_ZONE_COOLDOWN";

    /// <summary>돌파 쿨다운 표식의 접두사. §8 중복 방지 키와 같은 집합에 들어가므로 형식이 겹치면 안 된다.</summary>
    public const string BreakoutCooldownKeyPrefix = "BREAKOUT_COOLDOWN";

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
    /// 같은 zone lineage에서 돌파가 최근에 발동했다면 §10 쿨다운으로 새 READY도 거절한다(이슈 #47).
    /// </summary>
    /// <param name="zones">
    /// 후보 계층(structureCutoff 기준)의 zone 스냅샷. 병합으로 대표 ID가 바뀐 경우 <see cref="PriceZone.Aliases"/>로
    /// 이전 ID의 쿨다운을 이어 받는다(§16B lineage). 비어 있으면 ZoneId 자기 자신만 본다.
    /// </param>
    public static ImmutableArray<EntryCandidate> ApplyLatch(StructuralLatch latch,
        ImmutableArray<EntryCandidate> candidates, bool allowNewTrigger, StructurePolicy policy,
        ImmutableArray<PriceZone> zones = default)
    {
        ArgumentNullException.ThrowIfNull(latch);
        ArgumentNullException.ThrowIfNull(policy);
        var cooldown = policy.BreakoutCooldown();
        var activations = BreakoutActivations(latch);
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
                // §16B 예시 D: 신규 트리거 억제는 계산된 종결 상태를 WAIT로 되돌리지 않는다.
                if (CandidateSelection.IsTerminal(candidate.Disposition))
                {
                    result.Add(candidate with { Plan = null });
                    continue;
                }
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
            if (candidate.Disposition == CandidateDisposition.Ready && candidate.Kind is SetupKind.Pullback or SetupKind.Rebound &&
                EpisodeConsumptionKey(candidate) is { } episodeKey && latch.ConsumedGuardKeys.Contains(episodeKey))
            {
                result.Add(candidate with
                {
                    Disposition = CandidateDisposition.Rejected,
                    Plan = null,
                    RejectionCodes = Add(candidate.RejectionCodes, CodeEpisodeConsumed)
                });
                continue;
            }
            // §10 돌파 쿨다운: 비교 기준은 확정된 과거 시각인 트리거 봉 종료(TriggerConfirmedAt)이며 현재 시각이 아니다.
            // 경계는 "경과 < 쿨다운"만 차단한다 — 정확히 30분이면 새 발동을 허용한다.
            if (candidate.Disposition == CandidateDisposition.Ready && candidate.Kind == SetupKind.Breakout &&
                activations.Count > 0 &&
                LastBreakoutActivation(activations, candidate.ZoneId, zones) is { } activatedAt &&
                candidate.TriggerConfirmedAt - activatedAt < cooldown)
            {
                result.Add(candidate with
                {
                    Disposition = CandidateDisposition.Rejected,
                    Plan = null,
                    RejectionCodes = Add(candidate.RejectionCodes, CodeBreakoutCooldown)
                });
                continue;
            }
            result.Add(candidate);
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// §10 돌파 쿨다운 표식. 세션·정책이 다르면 래치 자체가 승계되지 않으므로 키는 세션 안에서만 의미를 갖는다.
    /// <paramref name="activatedAt"/>은 발동한 돌파 후보의 TriggerConfirmedAt(확정된 완료 봉 종료)이다.
    /// </summary>
    public static string BreakoutCooldownKey(string symbol, DateTimeOffset sessionStart, string zoneId,
        DateTimeOffset activatedAt) =>
        string.Join('|', BreakoutCooldownKeyPrefix, symbol, StructureMath.Iso(sessionStart),
            SetupKinds.Name(SetupKind.Breakout), zoneId, StructureMath.Iso(activatedAt));

    static string? EpisodeConsumptionKey(EntryCandidate candidate) => candidate.EpisodeStartAt is not { } episodeStart
        ? null
        : string.Join('|', "episode-consumed", candidate.KindName, candidate.ZoneId, StructureMath.Iso(episodeStart));

    /// <summary>래치에 남은 쿨다운 표식을 ZoneId별 마지막 발동 시각으로 정리한다.</summary>
    static Dictionary<string, DateTimeOffset> BreakoutActivations(StructuralLatch latch)
    {
        var activations = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var key in latch.ConsumedGuardKeys)
        {
            if (!TryParseBreakoutCooldownKey(key, out var zoneId, out var activatedAt)) continue;
            if (!activations.TryGetValue(zoneId, out var existing) || activatedAt > existing)
                activations[zoneId] = activatedAt;
        }
        return activations;
    }

    /// <summary>
    /// 같은 lineage(현재 ZoneId + 병합으로 흡수된 Aliases)의 마지막 돌파 발동 시각.
    /// 무관한 원천으로 새로 만들어진 ZoneId에는 이전 쿨다운을 전달하지 않는다(§16B).
    /// </summary>
    static DateTimeOffset? LastBreakoutActivation(Dictionary<string, DateTimeOffset> activations, string zoneId,
        ImmutableArray<PriceZone> zones)
    {
        DateTimeOffset? last = activations.TryGetValue(zoneId, out var own) ? own : null;
        if (zones.IsDefaultOrEmpty) return last;
        var zone = zones.FirstOrDefault(x => string.Equals(x.Id, zoneId, StringComparison.Ordinal));
        if (zone is null) return last;
        foreach (var alias in zone.Aliases)
            if (activations.TryGetValue(alias, out var aliased) && (last is null || aliased > last.Value))
                last = aliased;
        return last;
    }

    static bool TryParseBreakoutCooldownKey(string key, out string zoneId, out DateTimeOffset activatedAt)
    {
        zoneId = string.Empty;
        activatedAt = default;
        if (!key.StartsWith(BreakoutCooldownKeyPrefix + "|", StringComparison.Ordinal)) return false;
        var lastSeparator = key.LastIndexOf('|');
        if (lastSeparator <= 0) return false;
        if (!DateTimeOffset.TryParse(key[(lastSeparator + 1)..], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out activatedAt)) return false;
        var head = key[..lastSeparator];
        var zoneSeparator = head.LastIndexOf('|');
        if (zoneSeparator <= 0) return false;
        zoneId = head[(zoneSeparator + 1)..];
        return zoneId.Length > 0;
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
        var activatedBreakoutId = ActivatedBreakout(candidates)?.EventId;
        foreach (var candidate in candidates)
        {
            if (CandidateSelection.IsTerminal(candidate.Disposition))
                tombstones = tombstones.SetItem(candidate.EventId, candidate.Disposition);
            if (candidate.Disposition is CandidateDisposition.Ready or CandidateDisposition.Entered)
            {
                guards = guards.Add(candidate.DuplicateGuardKey);
                if (candidate.Disposition == CandidateDisposition.Entered && candidate.Kind is SetupKind.Pullback or SetupKind.Rebound &&
                    EpisodeConsumptionKey(candidate) is { } episodeKey)
                    guards = guards.Add(episodeKey);
                // §10 돌파 쿨다운의 기준 시점은 "재발동"의 대상인 대표 발동이다. 같은 trigger에서 여러
                // BREAKOUT 후보가 READY여도 §8의 실제 신규 거래 후보는 1개뿐이므로, 대표로 선택된 zone만
                // cooldown을 소비한다. active에서 대표 후보가 ENTERED로 바뀐 뒤에도 같은 결론을 유지한다.
                if (candidate.Kind == SetupKind.Breakout &&
                    string.Equals(candidate.EventId, activatedBreakoutId, StringComparison.Ordinal))
                    guards = guards.Add(BreakoutCooldownKey(latch.Symbol, latch.SessionStart, candidate.ZoneId,
                        candidate.TriggerConfirmedAt));
            }
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

    static EntryCandidate? ActivatedBreakout(ImmutableArray<EntryCandidate> candidates)
    {
        var entered = SelectEnteredPreferred(candidates.Where(x => x.Kind == SetupKind.Breakout));
        if (entered is not null) return entered;
        var preferred = CandidateSelection.SelectPreferred(candidates);
        return preferred?.Kind == SetupKind.Breakout
            ? preferred
            : null;
    }

    static EntryCandidate? SelectEnteredPreferred(IEnumerable<EntryCandidate> candidates)
    {
        var entered = candidates.Where(x => x.Disposition == CandidateDisposition.Entered && x.EntryQuality is not null);
        return entered
            .OrderByDescending(x => x.EntryQuality!.Value)
            .ThenByDescending(x => x.NetR ?? decimal.MinValue)
            .ThenBy(x => x.ZoneId, StringComparer.Ordinal)
            .FirstOrDefault();
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

    /// <summary>
    /// §16 + 이슈 #64: 같은 봉 안에서도 이벤트 서명이 달라지면 다른 관측이다. 서명이 같으면 ID도 같아
    /// 주기 반복은 그대로 중복 폐기되고, 상태 전이만 새 ID를 얻는다. 서명은 후보 상태의 canonical 문자열이라
    /// 재시작 후에도 같은 값이 나온다.
    /// </summary>
    public static string ObservationId(string symbol, DateTimeOffset lastCompletedBarStart, string policyHash,
        string? eventSignature) =>
        string.IsNullOrEmpty(eventSignature)
            ? ObservationId(symbol, lastCompletedBarStart, policyHash)
            : StructureMath.SourceId("observation", symbol, StructureMath.Iso(lastCompletedBarStart), policyHash,
                eventSignature);

    static ImmutableArray<string> Add(ImmutableArray<string> values, string value) =>
        values.Contains(value, StringComparer.Ordinal)
            ? values
            : values.Append(value).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();
}
