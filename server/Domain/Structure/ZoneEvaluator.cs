using System.Collections.Immutable;

namespace Astra.Server.Domain.Structure;

/// <summary>Zone 접촉·반응·강도 평가 입력. cutoff 이후 미래 움직임은 사용하지 않는다(§6.4).</summary>
public sealed record ZoneEvaluationRequest(DateTimeOffset SessionStart, DateTimeOffset Cutoff,
    ImmutableArray<StructureBar> OneMinuteBars, ImmutableArray<PriceZone> PreviousZones,
    ImmutableArray<string> RetiredZoneIds)
{
    public static ZoneEvaluationRequest Create(DateTimeOffset sessionStart, DateTimeOffset cutoff,
        ImmutableArray<StructureBar> oneMinute, ImmutableArray<PriceZone>? previous = null,
        ImmutableArray<string>? retired = null) =>
        new(sessionStart, cutoff, oneMinute, previous ?? ImmutableArray<PriceZone>.Empty,
            retired ?? ImmutableArray<string>.Empty);
}

public sealed record ZoneEvaluationResult(ImmutableArray<PriceZone> Zones, ImmutableArray<TouchEpisode> Episodes,
    ImmutableArray<string> RetiredZoneIds);

/// <summary>
/// 설계 §6.4~§6.5 및 §16B. 접촉 episode·반응·역할 전이·강도·구조 사용 자격을 계산한다.
/// 값은 0~1이며 확률이 아니다. 반복 터치가 많아도 실패 반응이 늘면 강도가 약해진다.
/// </summary>
public static class ZoneEvaluator
{
    public const string ReasonBroken = "ZONE_BROKEN";
    public const string ReasonRetired = "ZONE_RETIRED";
    public const string ReasonProfileOnly = "PROFILE_ONLY";
    public const string ReasonUnresolvedRole = "ROLE_UNRESOLVED";
    public const string ReasonEvidence = "INSUFFICIENT_INDEPENDENT_EVIDENCE";
    public const string ReasonStrength = "STRENGTH_BELOW_MINIMUM";
    public const string ReasonNotConfirmed = "SOURCE_NOT_CONFIRMED_AT_CUTOFF";

    public static ZoneEvaluationResult Evaluate(ImmutableArray<PriceZone> zones, ZoneEvaluationRequest request, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);

        var bars = request.OneMinuteBars.Where(x => x.End <= request.Cutoff).OrderBy(x => x.Start).ToImmutableArray();
        var atrSeries = SessionAtr.Series(bars, policy);
        var previousById = request.PreviousZones.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var retired = new SortedSet<string>(request.RetiredZoneIds, StringComparer.Ordinal);

        var evaluated = ImmutableArray.CreateBuilder<PriceZone>(zones.Length);
        var episodes = ImmutableArray.CreateBuilder<TouchEpisode>();

        foreach (var zone in zones)
        {
            var originalRole = InitialRole(zone, bars);
            var forward = bars.Where(x => x.Start >= zone.FirstConfirmedAt).ToImmutableArray();
            var (role, history, retiredNow) = Roles(zone, originalRole, forward);
            var zoneEpisodes = Episodes(zone, originalRole, history, forward, bars, atrSeries, policy);
            episodes.AddRange(zoneEpisodes);

            var strength = Strength(zone, zoneEpisodes, request, policy);
            var isRetired = zone.Retired || retiredNow || retired.Contains(zone.Id);
            if (isRetired) retired.Add(zone.Id);
            var reasons = RejectReasons(zone, role, strength, isRetired, request.Cutoff, policy);

            var next = zone with
            {
                Role = role,
                OriginalRole = originalRole,
                RoleHistory = history,
                Strength = strength,
                Eligible = reasons.Length == 0,
                RejectReasons = reasons,
                Retired = isRetired
            };
            if (previousById.TryGetValue(zone.Id, out var previous) && ContentKey(previous) != ContentKey(next))
                next = next with { SnapshotRevision = previous.SnapshotRevision + 1 };
            evaluated.Add(next);
        }

        return new ZoneEvaluationResult(evaluated.ToImmutable(), episodes.ToImmutable(), retired.ToImmutableArray());
    }

    // ── 역할 ──

    /// <summary>
    /// 형성 시점의 마지막 완료 종가 위치로 최초 역할을 정한다. 구간 안에서 형성되면 UNRESOLVED다.
    /// 이는 검증된 역할 전환(flip)이 아니라 최초 분류이며 flip은 별도 retest로만 확정된다(§6.4).
    /// </summary>
    static ZoneRole InitialRole(PriceZone zone, ImmutableArray<StructureBar> bars)
    {
        decimal? close = null;
        foreach (var bar in bars)
        {
            if (bar.End > zone.FirstConfirmedAt) break;
            close = bar.Close;
        }
        if (close is null) return ZoneRole.Unresolved;
        if (zone.Upper < close.Value) return ZoneRole.Support;
        if (zone.Lower > close.Value) return ZoneRole.Resistance;
        return ZoneRole.Unresolved;
    }

    static (ZoneRole Role, ImmutableArray<ZoneRoleChange> History, bool Retired) Roles(PriceZone zone,
        ZoneRole originalRole, ImmutableArray<StructureBar> forward)
    {
        var role = originalRole;
        var history = ImmutableArray.CreateBuilder<ZoneRoleChange>();
        var breachDirection = 0;                                       // +1 상향 돌파, -1 하향 이탈
        var retired = false;

        void Change(DateTimeOffset at, ZoneRole to, string reason)
        {
            if (role == to) return;
            history.Add(new ZoneRoleChange(at, role, to, reason));
            role = to;
        }

        foreach (var bar in forward)
        {
            // 등호는 breach가 아니다(§16B).
            switch (role)
            {
                case ZoneRole.Support when bar.Close < zone.Lower:
                    breachDirection = -1;
                    Change(bar.End, ZoneRole.Broken, "SUPPORT_CLOSE_BELOW_LOWER");
                    continue;
                case ZoneRole.Resistance when bar.Close > zone.Upper:
                    breachDirection = 1;
                    Change(bar.End, ZoneRole.Broken, "RESISTANCE_CLOSE_ABOVE_UPPER");
                    continue;
                case ZoneRole.FlippedSupport when bar.Close < zone.Lower:
                    breachDirection = -1;
                    Change(bar.End, ZoneRole.Broken, "FLIPPED_SUPPORT_BROKEN");
                    retired = true;
                    continue;
                case ZoneRole.FlippedResistance when bar.Close > zone.Upper:
                    breachDirection = 1;
                    Change(bar.End, ZoneRole.Broken, "FLIPPED_RESISTANCE_BROKEN");
                    retired = true;
                    continue;
                case ZoneRole.Unresolved when breachDirection == 0:
                    // 구간 안에서 형성된 구간의 최초 분류. 위/아래로 마감하면 위치에 따라 역할을 정한다.
                    if (bar.Close > zone.Upper) Change(bar.End, ZoneRole.Support, "RESOLVED_ABOVE_UPPER");
                    else if (bar.Close < zone.Lower) Change(bar.End, ZoneRole.Resistance, "RESOLVED_BELOW_LOWER");
                    continue;
            }

            if (breachDirection == 0 || retired) continue;
            if (!bar.Touches(zone.Lower, zone.Upper))
            {
                if (role == ZoneRole.Unresolved) Change(bar.End, ZoneRole.Broken, "FLIP_ATTEMPT_LEFT_ZONE");
                continue;
            }

            if (breachDirection > 0)
            {
                if (bar.Close > zone.Upper) { Change(bar.End, ZoneRole.FlippedSupport, "RETEST_HELD_ABOVE_UPPER"); breachDirection = 0; }
                else if (bar.Close < zone.Lower) Change(bar.End, ZoneRole.Broken, "FLIP_ATTEMPT_CLOSED_BELOW");
                else if (role != ZoneRole.Unresolved) Change(bar.End, ZoneRole.Unresolved, "RETEST_IN_PROGRESS");
            }
            else
            {
                if (bar.Close < zone.Lower) { Change(bar.End, ZoneRole.FlippedResistance, "RETEST_HELD_BELOW_LOWER"); breachDirection = 0; }
                else if (bar.Close > zone.Upper) Change(bar.End, ZoneRole.Broken, "FLIP_ATTEMPT_CLOSED_ABOVE");
                else if (role != ZoneRole.Unresolved) Change(bar.End, ZoneRole.Unresolved, "RETEST_IN_PROGRESS");
            }
        }

        return (role, history.ToImmutable(), retired);
    }

    static ZoneRole RoleAt(ZoneRole originalRole, ImmutableArray<ZoneRoleChange> history, DateTimeOffset at)
    {
        var role = originalRole;
        foreach (var change in history)
        {
            if (change.At > at) break;
            role = change.To;
        }
        return role;
    }

    // ── episode ──

    /// <summary>
    /// §6.4 한 봉이 구간과 겹친 것이 Touch, 연속 접촉 봉은 하나의 episode다.
    /// 구간에서 완전히 벗어난 완료 봉 2개가 나온 뒤 재접촉해야 새 episode다.
    /// 형성 자체는 재접촉 episode가 아니므로 확정 이후 시작한 접촉만 집계한다(§16A/§16B).
    /// </summary>
    static ImmutableArray<TouchEpisode> Episodes(PriceZone zone, ZoneRole originalRole,
        ImmutableArray<ZoneRoleChange> history, ImmutableArray<StructureBar> forward,
        ImmutableArray<StructureBar> allBars, ImmutableArray<double?> atrSeries, StructurePolicy policy)
    {
        var starts = new List<int>();
        var active = false;
        var outside = 0;
        for (var i = 0; i < forward.Length; i++)
        {
            if (forward[i].Touches(zone.Lower, zone.Upper))
            {
                if (!active) { starts.Add(i); active = true; }
                outside = 0;
            }
            else
            {
                outside++;
                if (active && outside >= policy.EpisodeExitBars) active = false;
            }
        }

        var result = ImmutableArray.CreateBuilder<TouchEpisode>(starts.Count);
        foreach (var start in starts)
        {
            var touch = forward[start];
            var notes = new SortedSet<string>(StringComparer.Ordinal);
            // 접촉 시점의 역할만 본다. BROKEN/UNRESOLVED로 방향이 없으면 최초 역할로 되돌리지 않는다(§16B:
            // BROKEN/retired ID는 원래 역할로 부활하지 않고, 확인 전 상태는 방향 평가 대상이 아니다).
            var roleAtTouch = Directional(RoleAt(originalRole, history, touch.Start));
            var atr = SessionAtr.At(allBars, atrSeries, touch.End);
            if (atr is null or <= 0) notes.Add("ATR_MISSING_AT_TOUCH");
            var margin = StructureMath.ScaledFloor(policy.ReactionSuccessFloor, policy.ReactionSuccessAtrFactor, atr);

            var window = new List<StructureBar>();
            for (var i = start + 1; i < forward.Length && window.Count < policy.ReactionWindowBars; i++) window.Add(forward[i]);
            var closed = window.Count >= policy.ReactionWindowBars;

            var outcome = EpisodeOutcome.Pending;
            DateTimeOffset? resolvedAt = null;
            var resolutionIndex = -1;
            if (roleAtTouch is null) notes.Add("NO_DIRECTIONAL_ROLE");
            else
                for (var i = 0; i < window.Count; i++)
                {
                    var bar = window[i];
                    var support = roleAtTouch is ZoneRole.Support or ZoneRole.FlippedSupport;
                    var failed = support ? bar.Close < zone.Lower : bar.Close > zone.Upper;
                    var succeeded = support ? bar.Close >= zone.Upper + margin : bar.Close <= zone.Lower - margin;
                    if (failed) { outcome = EpisodeOutcome.Failure; resolvedAt = bar.End; resolutionIndex = i; break; }
                    if (succeeded) { outcome = EpisodeOutcome.Success; resolvedAt = bar.End; resolutionIndex = i; break; }
                }
            if (outcome == EpisodeOutcome.Pending && closed)
            {
                outcome = EpisodeOutcome.Neutral;
                resolvedAt = window[^1].End;
            }

            double? excursion = null;
            var extreme = 0m;
            if (outcome == EpisodeOutcome.Success && resolutionIndex >= 0)
            {
                // 성공/실패가 처음 확정된 봉에서 episode를 닫고 이후 극값을 덧붙이지 않는다(§16B).
                var support = roleAtTouch is ZoneRole.Support or ZoneRole.FlippedSupport;
                extreme = support
                    ? window.Take(resolutionIndex + 1).Max(x => x.High)
                    : window.Take(resolutionIndex + 1).Min(x => x.Low);
                var favorable = support ? extreme - zone.Upper : zone.Lower - extreme;
                if (favorable < 0) favorable = 0;
                if (atr is > 0 && double.IsFinite(atr.Value)) excursion = (double)favorable / atr.Value;
                else notes.Add("EXCURSION_ATR_UNAVAILABLE");
            }

            result.Add(new TouchEpisode(zone.Id, touch.Start, resolvedAt, outcome,
                roleAtTouch ?? ZoneRole.Unresolved, atr, excursion, extreme, notes.ToImmutableArray()));
        }
        return result.ToImmutable();
    }

    static ZoneRole? Directional(ZoneRole role) => role switch
    {
        ZoneRole.Support or ZoneRole.FlippedSupport => ZoneRole.Support,
        ZoneRole.Resistance or ZoneRole.FlippedResistance => ZoneRole.Resistance,
        _ => null
    };

    // ── 강도 (§6.5) ──

    static ZoneStrength Strength(PriceZone zone, ImmutableArray<TouchEpisode> episodes, ZoneEvaluationRequest request, StructurePolicy policy)
    {
        var completed = episodes.Count(x => x.Outcome != EpisodeOutcome.Pending);
        var success = episodes.Count(x => x.Outcome == EpisodeOutcome.Success);
        var failed = episodes.Count(x => x.Outcome == EpisodeOutcome.Failure);
        var pending = episodes.Count(x => x.Outcome == EpisodeOutcome.Pending);

        double? touch = completed == 0 ? null : 1 - Math.Exp(-completed / policy.TouchEvidenceScale);
        var reactions = episodes.Where(x => x.Outcome == EpisodeOutcome.Success && x.FavorableExcursionAtr.HasValue)
            .Select(x => Math.Tanh(x.FavorableExcursionAtr!.Value / policy.ReactionAtrScale)).ToArray();
        double? reaction = reactions.Length == 0 ? null : reactions.Average();

        double? recency = null;
        foreach (var source in zone.Sources)
        {
            var axis = source.AgeSessions is { } ageSessions
                ? Math.Exp(-ageSessions / policy.RecencySessions)
                : Math.Exp(-Math.Max((request.Cutoff - Later(source.ConfirmedAt, request.SessionStart)).TotalMinutes, 0) / policy.RecencyTradingMinutes);
            if (double.IsFinite(axis) && (recency is null || axis > recency)) recency = axis;
        }

        var families = zone.Sources.Select(x => x.Family).Distinct().Count();
        var nonProfileFamilies = zone.Sources.Where(x => x.Family != ZoneSourceFamily.Profile)
            .Select(x => x.Family).Distinct().Count();
        double? confluence = families == 0 ? null : 1 - Math.Exp(-families / policy.ConfluenceScale);
        var breachPenalty = Math.Exp(-failed);

        var used = ImmutableArray.CreateBuilder<string>();
        var missing = ImmutableArray.CreateBuilder<string>();
        void Track(string name, double? value) { if (value.HasValue) used.Add(name); else missing.Add(name); }
        Track("touchEvidence", touch);
        Track("reactionEvidence", reaction);
        Track("recency", recency);
        Track("confluence", confluence);

        var mean = StructureMath.GeometricMeanOfAvailable([touch, reaction, recency, confluence]);
        double? value = mean is null ? null : mean.Value * breachPenalty;

        return new ZoneStrength(touch, reaction, recency, confluence, breachPenalty, value, completed, success,
            failed, pending, families, nonProfileFamilies, used.ToImmutable(), missing.ToImmutable());
    }

    static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    // ── 자격 (§6.5, §16B) ──

    static ImmutableArray<string> RejectReasons(PriceZone zone, ZoneRole role, ZoneStrength strength, bool retired,
        DateTimeOffset cutoff, StructurePolicy policy)
    {
        var reasons = new List<string>();
        if (zone.Sources.Length == 0 || zone.Sources.Max(x => x.ConfirmedAt) > cutoff) reasons.Add(ReasonNotConfirmed);
        if (retired) reasons.Add(ReasonRetired);
        if (role == ZoneRole.Broken) reasons.Add(ReasonBroken);
        if (role == ZoneRole.Unresolved) reasons.Add(ReasonUnresolvedRole);
        if (zone.ProfileOnly) reasons.Add(ReasonProfileOnly);
        // 가격 반응 1개 이상 또는 독립 source family 2개 이상(profile-only 제외).
        if (strength.SuccessEpisodes < 1 && strength.IndependentNonProfileFamilies < 2) reasons.Add(ReasonEvidence);
        if (strength.Value is null || strength.Value.Value < policy.ZoneEligibilityStrength) reasons.Add(ReasonStrength);
        return reasons.ToImmutableArray();
    }

    /// <summary>SnapshotRevision 판단용. 자기 자신의 revision 번호는 비교에서 제외한다.</summary>
    static string ContentKey(PriceZone zone) =>
        (zone with { SnapshotRevision = 0 }).Fingerprint();
}
