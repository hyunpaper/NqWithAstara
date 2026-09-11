using System.Collections.Immutable;
using System.Globalization;
using Astra.Server;

namespace Astra.Server.Domain.Structure;

/// <summary>Zone 재구성 입력. cutoff는 §16B structureCutoff이며 트리거 봉 자체는 포함하지 않는다.</summary>
public sealed record ZoneBuildRequest(string Symbol, DateTimeOffset SessionStart, DateTimeOffset SessionEnd,
    DateTimeOffset Cutoff, ImmutableArray<StructureBar> OneMinuteBars, ImmutableArray<StructureBar> FiveMinuteBars,
    ImmutableArray<StructureDailyBar> DailyBars, ImmutableArray<PriceZone> PreviousZones,
    ImmutableArray<string> RetiredZoneIds)
{
    public static ZoneBuildRequest Create(string symbol, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
        DateTimeOffset cutoff, ImmutableArray<StructureBar> oneMinute, ImmutableArray<StructureBar> fiveMinute,
        ImmutableArray<StructureDailyBar>? daily = null, ImmutableArray<PriceZone>? previous = null,
        ImmutableArray<string>? retired = null) =>
        new(symbol, sessionStart, sessionEnd, cutoff, oneMinute, fiveMinute,
            daily ?? ImmutableArray<StructureDailyBar>.Empty, previous ?? ImmutableArray<PriceZone>.Empty,
            retired ?? ImmutableArray<string>.Empty);
}

public sealed record ZoneBuildResult(ImmutableArray<PriceZone> Zones, VolumeProfile Profile,
    ImmutableArray<ConfirmedPivot> Pivots1m, ImmutableArray<ConfirmedPivot> Pivots5m,
    ImmutableArray<string> RetiredZoneIds, ImmutableArray<string> Warnings, double? Atr1mAtCutoff);

/// <summary>병합 전 구간 후보. 가격선 후보는 §6.3 반폭으로, 프로파일 구간은 bin 경계 그대로 만들어진다.</summary>
public sealed record ZoneCandidate(decimal Lower, decimal Upper, ImmutableArray<ZoneSource> Sources,
    ImmutableArray<string> Flags)
{
    public static ZoneCandidate FromLevel(decimal price, decimal halfWidth, ZoneSource source, params string[] flags) =>
        new(price - halfWidth, price + halfWidth, [source], flags.ToImmutableArray());

    public static ZoneCandidate FromBounds(decimal lower, decimal upper, ZoneSource source, params string[] flags) =>
        new(lower, upper, [source], flags.ToImmutableArray());
}

public sealed record ZoneAssembly(ImmutableArray<PriceZone> Zones, ImmutableArray<string> Warnings);

/// <summary>
/// 설계 §6.1~§6.3, §16B. 확정 원천만으로 구간을 만들고 병합·lineage를 유지한다.
/// 매 평가마다 ID를 새로 발급하지 않으며, 연결식 병합으로 먼 가격까지 하나가 되는 것을 막는다.
/// </summary>
public static class ZoneBuilder
{
    /// <summary>§6.2 "bin 합 = 입력 거래량 합(허용 오차 내)" 위반. 배분에서 빠진 거래량이 있다는 뜻이다.</summary>
    public const string WarningProfileVolumeMismatch = "PROFILE_VOLUME_MISMATCH";

    /// <summary>ATR 결측으로 bin 폭이 tick 하한으로 대체됐다(§6.2).</summary>
    public const string FlagBinWidthFromTickOnly = "BinWidthFromTickOnly";

    /// <summary>ATR 결측으로 병합 간격·최대 폭이 tick 하한으로 대체됐다(§6.3).</summary>
    public const string FlagMergeParamsFromTickOnly = "MergeParamsFromTickOnly";

    sealed class RawZone
    {
        public decimal Lower;
        public decimal Upper;
        public readonly List<ZoneSource> Sources = [];
        public readonly SortedSet<string> Flags = new(StringComparer.Ordinal);
        public decimal Width => Upper - Lower;
        public string MinSourceId => Sources.Select(x => x.Id).Min(StringComparer.Ordinal)!;
    }

    public static ZoneBuildResult Build(ZoneBuildRequest request, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);

        // Domain도 cutoff 검증으로 미래 입력을 거절한다(§16B).
        var bars1m = Completed(request.OneMinuteBars, request.Cutoff, warnings);
        var bars5m = Completed(request.FiveMinuteBars, request.Cutoff, warnings);
        var sessionDate = MarketRules.TradingDate(request.SessionStart);
        var daily = request.DailyBars
            .Where(x => x.TradingDate < sessionDate)
            .OrderBy(x => x.TradingDate)
            .TakeLast(policy.DailyLookbackSessions)
            .ToImmutableArray();
        if (daily.Length < request.DailyBars.Length) warnings.Add("DAILY_INPUT_TRIMMED");
        if (request.DailyBars.Any(x => x.TradingDate >= sessionDate)) warnings.Add("CURRENT_DAILY_BAR_REJECTED");

        var atrSeries = SessionAtr.Series(bars1m, policy);
        var atrAtCutoff = SessionAtr.At(bars1m, atrSeries, request.Cutoff);

        var pivots1m = PivotDetector.Detect(request.Symbol, request.SessionStart, BarTimeframe.OneMinute, bars1m, request.Cutoff, policy);
        var pivots5m = PivotDetector.Detect(request.Symbol, request.SessionStart, BarTimeframe.FiveMinute, bars5m, request.Cutoff, policy);

        var profile = BuildVolumeProfile(bars1m, atrAtCutoff, policy);
        foreach (var w in profile.Warnings) warnings.Add(w);

        var candidates = new List<ZoneCandidate>();
        foreach (var pivot in pivots1m.Concat(pivots5m))
            candidates.Add(LineCandidate(pivot.Price, new ZoneSource(pivot.SourceId, ZoneSourceFamily.Pivot,
                $"pivot-{(pivot.Timeframe == BarTimeframe.OneMinute ? "1m" : "5m")}-{(pivot.Kind == PivotKind.High ? "H" : "L")}",
                pivot.Price, pivot.OccurredAt, pivot.ConfirmedAt, false), bars1m, atrSeries, request.Cutoff, policy));

        foreach (var source in ContextSources(request, daily, policy))
            candidates.Add(LineCandidate(source.Price, source, bars1m, atrSeries, request.Cutoff, policy));

        foreach (var node in profile.Nodes)
        {
            // 프로파일은 세션 누적 보조 근거다. cutoff로 재스탬프하면 ID와 recency가 매 봉 흔들린다(§6.2).
            var id = StructureMath.SourceId("profile", request.Symbol, sessionDate.ToString("yyyy-MM-dd"),
                StructureMath.Price(profile.BinWidth), node.StartIndex.ToString(CultureInfo.InvariantCulture));
            var source = new ZoneSource(id, ZoneSourceFamily.Profile, node.IsPoc ? "profile-poc" : "profile-node",
                node.Lower + (node.Upper - node.Lower) / 2m, request.SessionStart, request.SessionStart, true);
            candidates.Add(profile.Coarsened
                ? ZoneCandidate.FromBounds(node.Lower, node.Upper, source, "EstimatedVolumeProfile", "CoarsenedProfile")
                : ZoneCandidate.FromBounds(node.Lower, node.Upper, source, "EstimatedVolumeProfile"));
        }

        var assembly = Assemble(candidates, request, atrAtCutoff, policy);
        foreach (var w in assembly.Warnings) warnings.Add(w);
        var retired = Retire(request, assembly.Zones);

        return new ZoneBuildResult(assembly.Zones, profile, pivots1m, pivots5m, retired, warnings.ToImmutableArray(), atrAtCutoff);
    }

    /// <summary>구간 후보를 §6.3 규칙으로 병합하고 §16B lineage로 ID·revision을 부여한다.</summary>
    public static ZoneAssembly Assemble(IEnumerable<ZoneCandidate> candidates, ZoneBuildRequest request,
        double? atrAtCutoff, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var raw = new List<RawZone>();
        foreach (var candidate in candidates)
        {
            var zone = new RawZone { Lower = candidate.Lower, Upper = candidate.Upper };
            zone.Sources.AddRange(candidate.Sources);
            foreach (var flag in candidate.Flags) zone.Flags.Add(flag);
            if (zone.Sources.Count > 0) raw.Add(zone);
        }
        // 병합 파라미터가 ATR 없이 tick 하한으로 대체된 사실은 값을 추정하지 않고 플래그로만 남긴다(§6.3).
        if (!Usable(atrAtCutoff))
        {
            warnings.Add(FlagMergeParamsFromTickOnly);
            foreach (var zone in raw) zone.Flags.Add(FlagMergeParamsFromTickOnly);
        }
        var zones = AssignIdentity(Merge(raw, atrAtCutoff, policy), request, warnings);
        return new ZoneAssembly(zones, warnings.ToImmutableArray());
    }

    /// <summary>폭·간격 계산에 실제로 쓸 수 있는 ATR인지(§16A: 결측·0·비유한은 쓰지 않는다).</summary>
    static bool Usable(double? atr) => StructureMath.ToPriceDelta(atr) is not null && atr is > 0;

    /// <summary>§6.3 반폭. 생성 시점 ATR이 없으면 tick만 쓴다.</summary>
    public static decimal HalfWidth(double? atrAtConfirmation, StructurePolicy policy) =>
        StructureMath.ScaledFloor(policy.ZoneHalfWidthFloor, policy.ZoneHalfWidthAtrFactor, atrAtConfirmation);

    // ── 원천 ──

    static IEnumerable<ZoneSource> ContextSources(ZoneBuildRequest request, ImmutableArray<StructureDailyBar> daily, StructurePolicy policy)
    {
        var symbol = request.Symbol;
        var sessionKey = MarketRules.TradingDate(request.SessionStart).ToString("yyyy-MM-dd");

        ZoneSource Daily(StructureDailyBar bar, string part, decimal price, int ageSessions) => new(
            StructureMath.SourceId("daily", symbol, bar.TradingDate.ToString("yyyy-MM-dd"), part),
            ZoneSourceFamily.ContextLevel, $"daily-{part}", price, request.SessionStart, request.SessionStart, false, ageSessions);

        if (daily.Length > 0)
        {
            var previous = daily[^1];
            yield return Daily(previous, "H", previous.High, 1);
            yield return Daily(previous, "L", previous.Low, 1);
            yield return Daily(previous, "C", previous.Close, 1);

            // 완료 20일 extrema는 실제 극값 원천 일봉의 H/L ID를 재사용한다. 동률은 최신 거래일(§16B).
            var highIndex = -1;
            var lowIndex = -1;
            for (var i = 0; i < daily.Length; i++)
            {
                if (highIndex < 0 || daily[i].High >= daily[highIndex].High) highIndex = i;
                if (lowIndex < 0 || daily[i].Low <= daily[lowIndex].Low) lowIndex = i;
            }
            if (highIndex != daily.Length - 1) yield return Daily(daily[highIndex], "H", daily[highIndex].High, daily.Length - highIndex);
            if (lowIndex != daily.Length - 1) yield return Daily(daily[lowIndex], "L", daily[lowIndex].Low, daily.Length - lowIndex);
        }

        // 개장 15분 고저는 15분이 끝난 후에만 원천이 된다(§6.1).
        var orbEnd = request.SessionStart + policy.OpeningRangeSpan();
        if (orbEnd > request.Cutoff) yield break;
        var opening = request.OneMinuteBars.Where(x => x.Start >= request.SessionStart && x.End <= orbEnd).OrderBy(x => x.Start).ToArray();
        if (opening.Length != policy.OpeningRangeMinutes || opening[0].Start != request.SessionStart || opening[^1].End != orbEnd) yield break;
        yield return new ZoneSource(StructureMath.SourceId("orb", symbol, sessionKey, "ORB15", "H"),
            ZoneSourceFamily.ContextLevel, "orb15-H", opening.Max(x => x.High), request.SessionStart, orbEnd, false);
        yield return new ZoneSource(StructureMath.SourceId("orb", symbol, sessionKey, "ORB15", "L"),
            ZoneSourceFamily.ContextLevel, "orb15-L", opening.Min(x => x.Low), request.SessionStart, orbEnd, false);
    }

    /// <summary>
    /// §6.3 반폭 = max(0.01, 0.15*ATR1mAtConfirmation). 생성 당시 ATR이 없으면 tick만 쓰고 근사 플래그를 남긴다.
    /// 일봉 context level은 장 시작에 이미 알려져 있으므로 세션의 최초 사용 가능 ATR을 생성 폭으로 고정한다.
    /// </summary>
    static ZoneCandidate LineCandidate(decimal price, ZoneSource source, IReadOnlyList<StructureBar> bars1m,
        ImmutableArray<double?> atrSeries, DateTimeOffset cutoff, StructurePolicy policy)
    {
        var atr = AtrForLineWidth(source, bars1m, atrSeries, cutoff);
        var half = HalfWidth(atr, policy);
        return Usable(atr)
            ? ZoneCandidate.FromLevel(price, half, source)
            : ZoneCandidate.FromLevel(price, half, source, "WidthFromTickOnly");
    }

    static double? AtrForLineWidth(ZoneSource source, IReadOnlyList<StructureBar> bars1m,
        ImmutableArray<double?> atrSeries, DateTimeOffset cutoff)
    {
        if (source.Family != ZoneSourceFamily.ContextLevel ||
            !source.Kind.StartsWith("daily-", StringComparison.Ordinal))
            return SessionAtr.At(bars1m, atrSeries, source.ConfirmedAt);

        for (var i = 0; i < bars1m.Count && i < atrSeries.Length; i++)
        {
            if (bars1m[i].End > cutoff) break;
            if (Usable(atrSeries[i])) return atrSeries[i];
        }
        return null;
    }

    // ── 병합 ──

    static List<RawZone> Merge(List<RawZone> raw, double? atrAtCutoff, StructurePolicy policy)
    {
        var gap = StructureMath.ScaledFloor(policy.ZoneMergeGapFloor, policy.ZoneMergeGapAtrFactor, atrAtCutoff);
        var maxWidth = StructureMath.ScaledFloor(policy.ZoneMergeMaxWidthFloor, policy.ZoneMergeMaxWidthAtrFactor, atrAtCutoff);
        var ordered = raw.OrderBy(x => x.Lower).ThenBy(x => x.Upper).ThenBy(x => x.MinSourceId, StringComparer.Ordinal).ToList();
        var groups = new List<RawZone>();

        foreach (var candidate in ordered)
        {
            if (groups.Count == 0) { groups.Add(Clone(candidate)); continue; }
            var current = groups[^1];
            // 원래 폭이 큰 프로파일 구간을 강제로 줄이거나 다른 구간을 흡수하게 만들지 않는다(§6.3).
            if (current.Width > maxWidth || candidate.Width > maxWidth) { groups.Add(Clone(candidate)); continue; }
            if (candidate.Lower - current.Upper > gap) { groups.Add(Clone(candidate)); continue; }
            var lower = Math.Min(current.Lower, candidate.Lower);
            var upper = Math.Max(current.Upper, candidate.Upper);
            if (upper - lower > maxWidth) { groups.Add(Clone(candidate)); continue; }   // 연결식 과병합 차단
            current.Lower = lower;
            current.Upper = upper;
            foreach (var source in candidate.Sources)
                if (!current.Sources.Any(x => x.Id == source.Id)) current.Sources.Add(source);
            foreach (var flag in candidate.Flags) current.Flags.Add(flag);
        }
        return groups;
    }

    static RawZone Clone(RawZone source)
    {
        var copy = new RawZone { Lower = source.Lower, Upper = source.Upper };
        copy.Sources.AddRange(source.Sources);
        foreach (var flag in source.Flags) copy.Flags.Add(flag);
        return copy;
    }

    // ── ID·lineage ──

    static ImmutableArray<PriceZone> AssignIdentity(List<RawZone> groups, ZoneBuildRequest request, SortedSet<string> warnings)
    {
        var previousBySource = new Dictionary<string, List<PriceZone>>(StringComparer.Ordinal);
        foreach (var zone in request.PreviousZones)
            foreach (var source in zone.Sources.Where(x => !x.Temporary))
            {
                if (!previousBySource.TryGetValue(source.Id, out var list)) previousBySource[source.Id] = list = [];
                list.Add(zone);
            }

        var retiredIds = request.RetiredZoneIds.ToHashSet(StringComparer.Ordinal);
        var sessionKey = MarketRules.TradingDate(request.SessionStart).ToString("yyyy-MM-dd");
        var result = ImmutableArray.CreateBuilder<PriceZone>();

        // 조각별 원천을 먼저 확정한다. lineage 소유자 결정과 ID 발급이 같은 결정적 순서를 쓴다.
        var pieces = groups
            .Select(group => (Group: group, Sources: group.Sources
                .OrderBy(x => x.ConfirmedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray()))
            .ToList();

        var inherited = pieces
            .Select(piece => piece.Sources
                .Where(x => !x.Temporary)
                .SelectMany(x => previousBySource.TryGetValue(x.Id, out var list) ? list : Enumerable.Empty<PriceZone>())
                .DistinctBy(x => x.Id)
                .OrderBy(x => x.FirstConfirmedAt).ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToArray())
            .ToList();

        // §16A 분리 규칙: 한 이전 Zone의 ID는 최대 한 조각만 물려받는다.
        // 대표 원천(= 이전 ZoneId와 같은 원천 ID)을 가진 조각이 우선이고, 없으면 결정적 순서상 첫 조각이다.
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < pieces.Count; i++)
            foreach (var previous in inherited[i])
            {
                if (!owner.TryGetValue(previous.Id, out var current)) { owner[previous.Id] = i; continue; }
                warnings.Add("ZONE_LINEAGE_SPLIT");
                if (HoldsRepresentative(pieces[i].Sources, previous.Id) &&
                    !HoldsRepresentative(pieces[current].Sources, previous.Id)) owner[previous.Id] = i;
            }

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < pieces.Count; index++)
        {
            var (group, sources) = pieces[index];
            var profileOnly = sources.All(x => x.Family == ZoneSourceFamily.Profile);
            // 임시 프로파일 원천은 Zone의 확정 lineage를 정하지 않는다(§16B). 영구 원천이 없을 때만 cutoff를 쓴다.
            var durable = sources.Where(x => !x.Temporary).ToArray();
            var firstConfirmed = durable.Length > 0 ? durable.Min(x => x.ConfirmedAt) : request.Cutoff;
            var lastConfirmed = durable.Length > 0 ? durable.Max(x => x.ConfirmedAt) : request.Cutoff;

            var matched = inherited[index].Where(x => owner[x.Id] == index).ToArray();

            string id;
            var aliases = new SortedSet<string>(StringComparer.Ordinal);
            int boundsRevision, snapshotRevision;
            if (matched.Length > 0)
            {
                var representative = matched[0];
                id = representative.Id;
                foreach (var other in matched.Skip(1)) { aliases.Add(other.Id); foreach (var a in other.Aliases) aliases.Add(a); }
                foreach (var a in representative.Aliases) aliases.Add(a);
                var sameBounds = representative.Lower == group.Lower && representative.Upper == group.Upper;
                boundsRevision = sameBounds ? representative.BoundsRevision : representative.BoundsRevision + 1;
                snapshotRevision = representative.SnapshotRevision;
                if (matched.Length > 1) warnings.Add("ZONE_LINEAGE_MERGED");
            }
            else
            {
                // profile 임시 ID는 Zone의 영구 대표 원천이 될 수 없다(§16B).
                // 대체 ID에도 cutoff를 넣지 않는다 — 평가마다 새 ID가 나오면 돌파 쿨다운이 매번 초기화된다(§6.3).
                var representativeSource = sources.FirstOrDefault(x => !x.Temporary);
                id = representativeSource is not null
                    ? representativeSource.Id
                    : StructureMath.SourceId("profile-zone", request.Symbol, sessionKey, SourceKey(sources));
                boundsRevision = 1;
                snapshotRevision = 1;
            }

            // 한 스냅샷 안에서 ZoneId는 유일해야 한다. 남은 충돌은 원천 기반 새 ID로 분리한다(§16A).
            // 조각의 원천 집합은 서로 겹치지 않으므로 가격 문자열 없이도 결정적으로 유일하다(§6.3).
            if (used.Contains(id))
            {
                warnings.Add("ZONE_LINEAGE_SPLIT");
                var key = SourceKey(sources);
                id = StructureMath.SourceId("zone-split", request.Symbol, sessionKey, key);
                for (var suffix = 2; used.Contains(id); suffix++)
                    id = StructureMath.SourceId("zone-split", request.Symbol, sessionKey, key,
                        suffix.ToString(CultureInfo.InvariantCulture));
                boundsRevision = 1;
                snapshotRevision = 1;
            }
            used.Add(id);
            aliases.Remove(id);

            var flags = new SortedSet<string>(group.Flags, StringComparer.Ordinal);
            if (profileOnly) flags.Add("ProfileOnlyTemporaryId");

            result.Add(new PriceZone(id, boundsRevision, snapshotRevision, group.Lower, group.Upper,
                ZoneRole.Unresolved, ZoneRole.Unresolved, firstConfirmed, lastConfirmed, sources,
                BuildEvidenceGroups(sources), aliases.ToImmutableArray(), null, false,
                ImmutableArray<string>.Empty, flags.ToImmutableArray(), ImmutableArray<ZoneRoleChange>.Empty,
                profileOnly, retiredIds.Contains(id)));
        }

        // 결과 순서는 Lower, Upper, source ID 순으로 결정한다(§6.3).
        return result
            .OrderBy(x => x.Lower).ThenBy(x => x.Upper).ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>조각의 원천 ID 집합. 대체 ZoneId의 유일성·안정성 근거이며 시각·가격 문자열을 쓰지 않는다(§6.3).</summary>
    static string SourceKey(ImmutableArray<ZoneSource> sources) =>
        string.Join(',', sources.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>이 조각이 이전 Zone의 대표 원천(ZoneId와 같은 원천 ID)을 그대로 갖고 있는지(§16A 분리 규칙).</summary>
    static bool HoldsRepresentative(ImmutableArray<ZoneSource> sources, string previousZoneId) =>
        sources.Any(x => !x.Temporary && string.Equals(x.Id, previousZoneId, StringComparison.Ordinal));

    /// <summary>§6.4 같은 family에서 시간 구간을 공유하는 원천은 하나의 evidence group이다.</summary>
    static ImmutableArray<EvidenceGroup> BuildEvidenceGroups(ImmutableArray<ZoneSource> sources)
    {
        var groups = ImmutableArray.CreateBuilder<EvidenceGroup>();
        foreach (var family in sources.Select(x => x.Family).Distinct().OrderBy(x => x))
        {
            var ordered = sources.Where(x => x.Family == family)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var index = 0;
            while (index < ordered.Length)
            {
                var from = ordered[index].OccurredAt;
                var to = ordered[index].ConfirmedAt;
                var members = new List<string> { ordered[index].Id };
                index++;
                while (index < ordered.Length && ordered[index].OccurredAt <= to)
                {
                    if (ordered[index].ConfirmedAt > to) to = ordered[index].ConfirmedAt;
                    members.Add(ordered[index].Id);
                    index++;
                }
                groups.Add(new EvidenceGroup(
                    StructureMath.SourceId("evidence", family.ToString(), StructureMath.Iso(from), StructureMath.Iso(to)),
                    family, from, to, members.Order(StringComparer.Ordinal).ToImmutableArray()));
            }
        }
        return groups.ToImmutable();
    }

    /// <summary>마지막 비-profile 원천이 사라지면 Zone을 retired로 처리한다(§16B).</summary>
    static ImmutableArray<string> Retire(ZoneBuildRequest request, ImmutableArray<PriceZone> zones)
    {
        var live = zones.SelectMany(x => x.Sources).Where(x => !x.Temporary).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var retired = new SortedSet<string>(request.RetiredZoneIds, StringComparer.Ordinal);
        foreach (var previous in request.PreviousZones)
        {
            var durable = previous.Sources.Where(x => !x.Temporary).Select(x => x.Id).ToArray();
            if (durable.Length == 0 || durable.Any(live.Contains)) continue;
            retired.Add(previous.Id);
        }
        return retired.ToImmutableArray();
    }

    static ImmutableArray<StructureBar> Completed(ImmutableArray<StructureBar> bars, DateTimeOffset cutoff, SortedSet<string> warnings)
    {
        var kept = bars.Where(x => x.End <= cutoff).OrderBy(x => x.Start).ToImmutableArray();
        if (kept.Length != bars.Length) warnings.Add("FUTURE_BAR_INPUT_REJECTED");
        return kept;
    }

    // ── 거래량 프로파일 (§6.2) ──

    /// <summary>
    /// 봉 기반 추정 분포다. 실제 가격별 체결량·기관 원가·주문장 매물량이 아니다(§6.1).
    /// binWidth=max(0.01, ATR1m*0.5), 기준 가격축은 0에서 정렬하고 같은 스냅샷의 모든 봉에 같은 폭을 쓴다.
    /// </summary>
    public static VolumeProfile BuildVolumeProfile(IReadOnlyList<StructureBar> bars, double? atr1m, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(policy);
        var baseWidth = StructureMath.ScaledFloor(policy.PriceTick, policy.ProfileBinAtrFactor, atr1m);
        if (baseWidth <= 0) baseWidth = policy.PriceTick;
        var tickOnlyWidth = !Usable(atr1m);
        if (bars.Count == 0) return EmptyProfile(baseWidth, 1, 0, false, tickOnlyWidth, "PROFILE_NO_BARS");

        var inputVolume = bars.Sum(x => x.Volume);
        var minLow = bars.Min(x => x.Low);
        var maxHigh = bars.Max(x => x.High);

        // 정수배 확장으로 bin 상한 안에 넣는다. 소수점/0 ATR로 무한 루프를 만들지 않는다.
        var multiple = 1;
        var width = baseWidth;
        for (var guard = 0; guard < 64; guard++)
        {
            var span = Span(minLow, maxHigh, width);
            if (span <= policy.ProfileMaxBins) break;
            var factor = (int)Math.Ceiling(span / (double)policy.ProfileMaxBins);
            if (factor < 2) factor = 2;
            multiple *= factor;
            width = baseWidth * multiple;
        }
        var coarsened = multiple > 1;
        if (Span(minLow, maxHigh, width) > policy.ProfileMaxBins)
            return EmptyProfile(width, multiple, inputVolume, coarsened, tickOnlyWidth,
                "PROFILE_BIN_LIMIT_UNRESOLVED");

        if (inputVolume <= 0)
            return EmptyProfile(width, multiple, inputVolume, coarsened, tickOnlyWidth, "ZERO_VOLUME_PROFILE");

        var minIndex = Index(minLow, width);
        var maxIndex = Index(maxHigh, width);
        var volumes = new double[maxIndex - minIndex + 1];

        foreach (var bar in bars)
        {
            if (bar.Volume <= 0) continue;
            if (bar.High == bar.Low) { volumes[Index(bar.Low, width) - minIndex] += bar.Volume; continue; }
            var range = bar.High - bar.Low;
            for (var i = Index(bar.Low, width); i <= Index(bar.High, width); i++)
            {
                var binLower = i * width;
                var overlap = Math.Min(bar.High, binLower + width) - Math.Max(bar.Low, binLower);
                if (overlap <= 0) continue;
                volumes[i - minIndex] += bar.Volume * (double)(overlap / range);
            }
        }

        var bins = ImmutableArray.CreateBuilder<VolumeProfileBin>(volumes.Length);
        for (var i = 0; i < volumes.Length; i++)
        {
            var lower = (minIndex + i) * width;
            bins.Add(new VolumeProfileBin(minIndex + i, lower, lower + width, volumes[i]));
        }

        var poc = 0;
        for (var i = 1; i < volumes.Length; i++)
            if (volumes[i] > volumes[poc]) poc = i;                 // 동률이면 낮은 가격 bin을 유지한다
        var pocVolume = volumes[poc];
        var warnings = new SortedSet<string>(StringComparer.Ordinal) { "EstimatedVolumeProfile" };
        if (coarsened) warnings.Add("CoarsenedProfile");
        if (tickOnlyWidth) warnings.Add(FlagBinWidthFromTickOnly);

        // §6.2 bin 합 = 입력 거래량 합(허용 오차 내). 배분 로직은 그대로 두고 불일치만 노출한다.
        var allocated = volumes.Sum();
        if (Math.Abs(allocated - inputVolume) > policy.ProfileVolumeTolerance * Math.Max(1, Math.Abs(inputVolume)))
            warnings.Add(WarningProfileVolumeMismatch);

        var nodes = ImmutableArray<ProfileNode>.Empty;
        if (pocVolume > 0)
        {
            var threshold = pocVolume * policy.ProfileNodeThreshold;
            var selected = new List<int>();
            for (var i = 0; i < volumes.Length; i++)
            {
                if (volumes[i] <= 0 || volumes[i] < threshold) continue;
                var leftOk = i == 0 || volumes[i] >= volumes[i - 1];
                var rightOk = i == volumes.Length - 1 || volumes[i] >= volumes[i + 1];
                if (leftOk && rightOk) selected.Add(i);
            }
            var candidates = new List<ProfileNode>();
            var cursor = 0;
            while (cursor < selected.Count)
            {
                var start = cursor;
                while (cursor + 1 < selected.Count && selected[cursor + 1] == selected[cursor] + 1) cursor++;
                var from = selected[start];
                var to = selected[cursor];
                double volume = 0;
                for (var i = from; i <= to; i++) volume += volumes[i];
                candidates.Add(new ProfileNode(minIndex + from, minIndex + to, bins[from].Lower, bins[to].Upper, volume,
                    poc >= from && poc <= to));
                cursor++;
            }
            nodes = candidates
                .OrderByDescending(x => x.Volume).ThenBy(x => x.StartIndex)
                .Take(policy.ProfileMaxNodes)
                .OrderBy(x => x.StartIndex)
                .ToImmutableArray();
        }

        return new VolumeProfile(width, multiple, bins.ToImmutable(), nodes, minIndex + poc, inputVolume,
            allocated, coarsened, warnings.ToImmutableArray());
    }

    /// <summary>coarsened 사실은 필드와 경고 문자열이 함께 가도록 한 곳에서 만든다(§16A, #65).</summary>
    static VolumeProfile EmptyProfile(decimal width, int multiple, double inputVolume, bool coarsened,
        bool tickOnlyWidth, params string[] warnings)
    {
        var all = new SortedSet<string>(warnings, StringComparer.Ordinal);
        if (coarsened) all.Add("CoarsenedProfile");
        if (tickOnlyWidth) all.Add(FlagBinWidthFromTickOnly);
        return VolumeProfile.Empty(width, multiple, inputVolume, coarsened, [.. all]);
    }

    static int Index(decimal price, decimal width) => (int)decimal.Floor(price / width);

    static long Span(decimal minLow, decimal maxHigh, decimal width) =>
        (long)decimal.Floor(maxHigh / width) - (long)decimal.Floor(minLow / width) + 1;
}
