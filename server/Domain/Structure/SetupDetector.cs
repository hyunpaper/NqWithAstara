using System.Collections.Immutable;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D2 — 설계 §8 진입 후보와 구조 가설 + §16B 시각/상태 계약.
// long-only다. DOWN 추세 점수는 공매도 진입 지시가 아니다(§8).
// structureCutoff=TriggerBarStart이며 트리거 봉은 조건 판정과 트리거 거래량에만 쓴다(§16B).

public enum SetupKind { Pullback, Breakout, Rebound }

/// <summary>§16B 상태의 단일 권위. 마지막 네 상태와 REJECTED는 같은 EventId에서 되살리지 않는다.</summary>
public enum CandidateDisposition { Wait, Ready, Rejected, Invalidated, Expired, Entered }

public static class SetupKinds
{
    /// <summary>저장·정렬용 종류 문자열. 종류 간 대표 선택은 이 문자열의 ordinal 순서를 쓴다(§8).</summary>
    public static string Name(SetupKind kind) => kind switch
    {
        SetupKind.Pullback => "PULLBACK",
        SetupKind.Breakout => "BREAKOUT",
        SetupKind.Rebound => "REBOUND",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

/// <summary>
/// 설계 §11 EntryCandidate. EventId는 (symbol,sessionStart,kind,zoneId,triggerBarStart)로 고정하고
/// 추가 중복 방지 키는 (symbol,sessionStart,kind,triggerBarStart)다(§8).
/// </summary>
public sealed record EntryCandidate(string EventId, string DuplicateGuardKey, SetupKind Kind, string KindName,
    string ZoneId, DateTimeOffset TriggerBarStart, DateTimeOffset TriggerConfirmedAt,
    DateTimeOffset StructureCutoff, DateTimeOffset AnalysisAsOf, DateTimeOffset ExpiresAt,
    CandidateDisposition Disposition, decimal EntryReference, decimal? InvalidationAnchor,
    double? EntryQuality, EntryQualityResult Quality, StructuralTradePlan? Plan, PlanEvaluation Planning,
    ImmutableArray<string> RejectionCodes, ImmutableArray<string> Notes, bool CounterTrend,
    bool RetestConfirmed, DateTimeOffset? EpisodeStartAt)
{
    public decimal? NetR => Planning.NetR;

    public string Fingerprint() => string.Join('|', EventId, DuplicateGuardKey, KindName, ZoneId,
        StructureMath.Iso(TriggerBarStart), StructureMath.Iso(TriggerConfirmedAt), StructureMath.Iso(StructureCutoff),
        StructureMath.Iso(AnalysisAsOf), StructureMath.Iso(ExpiresAt), Disposition.ToString(),
        StructureMath.Price(EntryReference),
        InvalidationAnchor is null ? "null" : StructureMath.Price(InvalidationAnchor.Value),
        StructureMath.Number(EntryQuality), Quality.Fingerprint(), Plan?.Fingerprint() ?? "null",
        string.Join(',', Planning.ReasonCodes), string.Join(',', RejectionCodes), string.Join(',', Notes),
        CounterTrend ? "1" : "0", RetestConfirmed ? "1" : "0",
        EpisodeStartAt is null ? "null" : StructureMath.Iso(EpisodeStartAt.Value));
}

/// <summary>
/// 후보 탐지 입력. Zones/Episodes는 structureCutoff(=TriggerBarStart) 기준으로 이미 평가된 스냅샷이며
/// 트리거 봉의 거래량·고가는 그 구조에 들어가 있지 않아야 한다(§5.3, §16B).
/// </summary>
public sealed record SetupDetectionRequest(string Symbol, DateTimeOffset SessionStart, DateTimeOffset SessionEnd,
    DateTimeOffset AnalysisAsOf, DateTimeOffset Now, ImmutableArray<StructureBar> OneMinuteBars,
    ImmutableArray<PriceZone> Zones, ImmutableArray<TouchEpisode> Episodes, TrendAssessment Trend,
    double? Atr1mAtStructureCutoff, decimal? LivePrice, DateTimeOffset? QuoteAt,
    StructureLiquidity? Liquidity, ImmutableArray<string> ExternalBlockers, bool PriceTickSupported = true)
{
    public static SetupDetectionRequest Create(string symbol, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
        DateTimeOffset analysisAsOf, DateTimeOffset now, ImmutableArray<StructureBar> bars,
        ImmutableArray<PriceZone> zones, ImmutableArray<TouchEpisode> episodes, TrendAssessment trend,
        double? atr1mAtStructureCutoff, decimal? livePrice, DateTimeOffset? quoteAt,
        StructureLiquidity? liquidity = null, ImmutableArray<string>? externalBlockers = null,
        bool priceTickSupported = true) =>
        new(symbol, sessionStart, sessionEnd, analysisAsOf, now, bars, zones, episodes, trend,
            atr1mAtStructureCutoff, livePrice, quoteAt, liquidity,
            externalBlockers ?? ImmutableArray<string>.Empty, priceTickSupported);
}

public sealed record SetupDetectionResult(ImmutableArray<EntryCandidate> Candidates, string? PreferredCandidateId,
    CandidateDisposition Summary, ImmutableArray<string> Warnings, ImmutableArray<string> ReadyBlockers,
    decimal? ValidSpread, ImmutableArray<string> SpreadReasons, DateTimeOffset? TriggerBarStart)
{
    public EntryCandidate? Preferred =>
        PreferredCandidateId is null ? null : Candidates.FirstOrDefault(x => x.EventId == PreferredCandidateId);
}

/// <summary>
/// 설계 §8 + §9 + §16B. 트리거 조건을 종류별 정의 그대로 판정하고, 계획/품질 검사를 모두 수행한 뒤
/// 후보 상태를 확정한다. 상태의 단일 권위는 후보 Disposition이다.
/// </summary>
public static class SetupDetector
{
    public const string BlockerStaleLatestBar = "STALE_LATEST_BAR";
    public const string BlockerAfterEntryCutoff = "AFTER_ENTRY_CUTOFF";
    public const string BlockerMissingQuote = "MISSING_QUOTE";
    public const string BlockerStaleQuote = "STALE_QUOTE";
    public const string BlockerQuoteInFuture = "QUOTE_IN_FUTURE";
    public const string BlockerOutsideSession = "OUTSIDE_REGULAR_SESSION";
    public const string WarningNoTriggerBar = "NO_COMPLETED_TRIGGER_BAR";
    public const string WarningNoPreviousBar = "NO_CONTIGUOUS_PREVIOUS_BAR";
    public const string WarningFutureBars = "FUTURE_BAR_INPUT_REJECTED";

    public const string NoteEntryFromTriggerClose = "ENTRY_REFERENCE_FROM_TRIGGER_CLOSE";
    public const string NoteChaseTriggerBelowAnchor = "TRIGGER_LOW_BELOW_INVALIDATION_ANCHOR";
    public const string NoteCounterTrend = "COUNTER_TREND_SETUP";
    public const string NoteRetestPending = "BREAKOUT_RETEST_NOT_CONFIRMED";
    public const string NoteLiveBelowSupportLower = "LIVE_PRICE_BELOW_SUPPORT_LOWER";
    public const string NoteLiveBelowBreakoutLevel = "LIVE_PRICE_NOT_ABOVE_BREAKOUT_LEVEL";
    public const string NoteLiveBelowStop = "LIVE_PRICE_AT_OR_BELOW_STRUCTURAL_STOP";
    public const string NotePullbackTrendState = "PULLBACK_REQUIRES_UP_OR_TRANSITION";

    public static SetupDetectionResult Detect(SetupDetectionRequest request, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var readyBlockers = new SortedSet<string>(request.ExternalBlockers, StringComparer.Ordinal);
        foreach (var blocker in request.Trend.BlockersForReady) readyBlockers.Add(blocker);

        var bars = request.OneMinuteBars.Where(x => x.End <= request.AnalysisAsOf).OrderBy(x => x.Start).ToImmutableArray();
        if (bars.Length != request.OneMinuteBars.Length) warnings.Add(WarningFutureBars);

        var (spread, spreadReasons) = StructureLiquidityRules.Validate(request.Liquidity, request.Now,
            request.SessionStart, request.SessionEnd, policy);

        var trigger = bars.Length > 0 && bars[^1].End == request.AnalysisAsOf ? bars[^1] : null;
        if (trigger is null)
        {
            warnings.Add(WarningNoTriggerBar);
            return new SetupDetectionResult(ImmutableArray<EntryCandidate>.Empty, null, CandidateDisposition.Wait,
                warnings.ToImmutableArray(), readyBlockers.ToImmutableArray(), spread, spreadReasons, null);
        }

        var structureCutoff = trigger.Start;
        var triggerConfirmedAt = trigger.End;
        var expiresAt = triggerConfirmedAt + policy.CandidateTtl();

        // §16B: 최신 완료 봉의 종료가 현재보다 오래됐으면 신규 후보를 만들지 않는다.
        if ((request.Now - triggerConfirmedAt).TotalSeconds > policy.LatestBarMaxAgeSeconds)
        {
            readyBlockers.Add(BlockerStaleLatestBar);
            warnings.Add(BlockerStaleLatestBar);
            return new SetupDetectionResult(ImmutableArray<EntryCandidate>.Empty, null, CandidateDisposition.Wait,
                warnings.ToImmutableArray(), readyBlockers.ToImmutableArray(), spread, spreadReasons, structureCutoff);
        }

        // §9.3 정규장 마감 40분 전 신규 진입 금지(v4 운영 제한 유지) / 정규장 밖 신규 진입 금지(§5.1).
        if (triggerConfirmedAt > request.SessionEnd - TimeSpan.FromMinutes(policy.EntryCutoffBeforeCloseMinutes))
            readyBlockers.Add(BlockerAfterEntryCutoff);
        if (trigger.Start < request.SessionStart || triggerConfirmedAt > request.SessionEnd)
            readyBlockers.Add(BlockerOutsideSession);

        // 신규 진입 후보의 quote 신선도는 표시·청산용과 별도 정책이다(§5.1).
        var entryNotes = new SortedSet<string>(StringComparer.Ordinal);
        decimal entryReference;
        if (request.LivePrice is { } live && live > 0)
        {
            entryReference = live;
            if (request.QuoteAt is { } quoteAt)
            {
                if ((request.Now - quoteAt).TotalSeconds > policy.NewEntryQuoteMaxAgeSeconds) readyBlockers.Add(BlockerStaleQuote);
                if ((quoteAt - request.Now).TotalSeconds > policy.QuoteFutureToleranceSeconds) readyBlockers.Add(BlockerQuoteInFuture);
            }
            else readyBlockers.Add(BlockerMissingQuote);
        }
        else
        {
            entryReference = trigger.Close;
            entryNotes.Add(NoteEntryFromTriggerClose);
            readyBlockers.Add(BlockerMissingQuote);
        }

        var previous = bars.LastOrDefault(x => x.End == structureCutoff);
        if (previous is null) warnings.Add(WarningNoPreviousBar);

        var candidates = ImmutableArray.CreateBuilder<EntryCandidate>();
        var zones = request.Zones.OrderBy(x => x.Lower).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();

        if (previous is not null && previous.End == trigger.Start)
        {
            foreach (var zone in zones)
            {
                var pullback = DetectPullback(request, zone, trigger, previous, bars, structureCutoff);
                if (pullback is not null) candidates.Add(Build(request, policy, pullback, trigger, bars, structureCutoff,
                    triggerConfirmedAt, expiresAt, entryReference, entryNotes, spread, readyBlockers));

                var breakout = DetectBreakout(zone, trigger, previous);
                if (breakout is not null) candidates.Add(Build(request, policy, breakout, trigger, bars, structureCutoff,
                    triggerConfirmedAt, expiresAt, entryReference, entryNotes, spread, readyBlockers));

                var rebound = DetectRebound(request, zone, trigger, previous, bars, structureCutoff);
                if (rebound is not null) candidates.Add(Build(request, policy, rebound, trigger, bars, structureCutoff,
                    triggerConfirmedAt, expiresAt, entryReference, entryNotes, spread, readyBlockers));
            }
        }

        var all = candidates.ToImmutable();
        var preferred = CandidateSelection.SelectPreferred(all);
        var summary = preferred is not null
            ? preferred.Disposition
            : all.Length == 0 ? CandidateDisposition.Wait : CandidateSelection.Summarize(all);

        return new SetupDetectionResult(all, preferred?.EventId, summary, warnings.ToImmutableArray(),
            readyBlockers.ToImmutableArray(), spread, spreadReasons, structureCutoff);
    }

    // ── 트리거 정의 (§8) ──

    sealed record Hypothesis(SetupKind Kind, PriceZone Zone, decimal? Anchor, DateTimeOffset? EpisodeStartAt,
        bool CounterTrend, bool RetestConfirmed, SortedSet<string> Notes, decimal? LiveFloorExclusive,
        decimal? LiveFloorInclusive);

    /// <summary>
    /// PULLBACK: UP/TRANSITION에서 확인된 support(또는 retest된 flipped-support) 접촉 episode 뒤,
    /// 완료 봉이 직전 봉 High 위에서 마감하고 support Upper 위로 회복한다(§8).
    /// </summary>
    static Hypothesis? DetectPullback(SetupDetectionRequest request, PriceZone zone,
        StructureBar trigger, StructureBar previous, ImmutableArray<StructureBar> bars, DateTimeOffset structureCutoff)
    {
        if (!IsUsableSupport(zone)) return null;
        if (request.Trend.State is not (TrendState.Up or TrendState.Transition)) return null;
        if (trigger.Close <= previous.High || trigger.Close <= zone.Upper) return null;

        var episode = LatestEpisode(request.Episodes, zone, structureCutoff);
        if (episode is null) return null;

        var notes = new SortedSet<string>(StringComparer.Ordinal);
        // §8/§16B: anchor=min(지지 Lower, 해당 눌림 episode의 확정된 Low). 트리거 저가로 anchor를 넓히지 않는다.
        var episodeLow = EpisodeLow(zone, episode, request.Episodes, bars, structureCutoff);
        var anchor = episodeLow is { } low && low < zone.Lower ? low : zone.Lower;
        if (trigger.Low < anchor) notes.Add(NoteChaseTriggerBelowAnchor);
        return new Hypothesis(SetupKind.Pullback, zone, anchor, episode.StartAt, false, false, notes,
            null, zone.Lower);
    }

    /// <summary>
    /// BREAKOUT: 트리거 직전 스냅샷의 자격 있는 resistance에서 직전 Close&lt;=Upper, 트리거 Close&gt;Upper이며 양봉,
    /// livePrice&gt;Upper여야 한다. 무효화 anchor=resistance Lower다(§8, §16B InvalidationZoneSnapshot).
    /// </summary>
    static Hypothesis? DetectBreakout(PriceZone zone, StructureBar trigger, StructureBar previous)
    {
        if (!zone.Eligible || zone.Retired || zone.ProfileOnly) return null;
        if (zone.Role is not (ZoneRole.Resistance or ZoneRole.FlippedResistance)) return null;
        if (previous.Close > zone.Upper) return null;
        if (trigger.Close <= zone.Upper) return null;
        if (trigger.Close <= trigger.Open) return null;          // 양봉 요구

        var notes = new SortedSet<string>(StringComparer.Ordinal);
        // retest 확인 여부는 별도 필드다. retest 전후를 같은 검증 수준으로 표시하지 않는다(§8).
        var retest = zone.RoleHistory.Any(x => x.Reason == "RETEST_HELD_ABOVE_UPPER");
        if (!retest) notes.Add(NoteRetestPending);
        // 트리거 봉 저점이 Lower 아래면 추격/넓은 위험으로 기록하되 손절을 더 먼 저점으로 옮기지 않는다.
        if (trigger.Low < zone.Lower) notes.Add(NoteChaseTriggerBelowAnchor);
        return new Hypothesis(SetupKind.Breakout, zone, zone.Lower, null, false, retest, notes, zone.Upper, null);
    }

    /// <summary>
    /// REBOUND: 확인된 support에서 실패한 하향 이탈(구간 아래로 내려갔지만 완료 종가는 Lower 아래로 마감하지 않음)
    /// 이후 완료 봉이 support Upper 및 직전 봉 High 위로 마감한다(§8).
    /// 추세 점수가 낮다는 이유로 거절하지 않고 CounterTrend=true로 분리한다.
    /// </summary>
    static Hypothesis? DetectRebound(SetupDetectionRequest request, PriceZone zone,
        StructureBar trigger, StructureBar previous, ImmutableArray<StructureBar> bars, DateTimeOffset structureCutoff)
    {
        if (!IsUsableSupport(zone)) return null;
        if (trigger.Close <= previous.High || trigger.Close <= zone.Upper) return null;

        var episode = FailedBreakdownEpisode(zone, request.Episodes, bars, structureCutoff);
        if (episode is null) return null;

        var notes = new SortedSet<string>(StringComparer.Ordinal) { NoteCounterTrend };
        var episodeLow = EpisodeLow(zone, episode, request.Episodes, bars, structureCutoff);
        var anchor = episodeLow is { } low && low < zone.Lower ? low : zone.Lower;
        if (trigger.Low < anchor) notes.Add(NoteChaseTriggerBelowAnchor);
        return new Hypothesis(SetupKind.Rebound, zone, anchor, episode.StartAt, true, false, notes, null, zone.Lower);
    }

    static bool IsUsableSupport(PriceZone zone) =>
        zone.Eligible && !zone.Retired && !zone.ProfileOnly &&
        zone.Role is ZoneRole.Support or ZoneRole.FlippedSupport;

    static TouchEpisode? LatestEpisode(ImmutableArray<TouchEpisode> episodes, PriceZone zone, DateTimeOffset structureCutoff) =>
        episodes.Where(x => x.ZoneId == zone.Id && x.StartAt < structureCutoff)
            .OrderBy(x => x.StartAt).LastOrDefault();

    /// <summary>
    /// 실패한 하향 이탈 episode: 구간 아래로 내려간 봉(Low&lt;Lower)이 있으나 완료 Close가 Lower 아래로 마감하지 않은 episode.
    /// 종가가 Lower 아래로 마감했다면 D1 규칙에 따라 그 구간은 BROKEN이며 원래 역할로 부활하지 않는다(§16B).
    /// </summary>
    static TouchEpisode? FailedBreakdownEpisode(PriceZone zone, ImmutableArray<TouchEpisode> episodes,
        ImmutableArray<StructureBar> bars, DateTimeOffset structureCutoff)
    {
        foreach (var episode in episodes.Where(x => x.ZoneId == zone.Id && x.StartAt < structureCutoff)
                     .OrderByDescending(x => x.StartAt))
        {
            var window = EpisodeBars(zone, episode, episodes, bars, structureCutoff);
            if (window.Count == 0) continue;
            if (window.Any(x => x.Close < zone.Lower)) continue;      // 완료 종가 이탈은 붕괴이지 실패한 이탈이 아니다
            if (window.Any(x => x.Low < zone.Lower)) return episode;
        }
        return null;
    }

    /// <summary>
    /// §16B: episode low는 트리거 직전까지의 완료 봉에서 구한다. 뒤에서 생긴 더 낮은 저점을 끌어다 넣지 않으려고
    /// 해당 episode의 접촉 봉만(다음 episode 시작 전까지) 사용한다.
    /// </summary>
    static List<StructureBar> EpisodeBars(PriceZone zone, TouchEpisode episode, ImmutableArray<TouchEpisode> episodes,
        ImmutableArray<StructureBar> bars, DateTimeOffset structureCutoff)
    {
        var next = episodes.Where(x => x.ZoneId == zone.Id && x.StartAt > episode.StartAt)
            .Select(x => (DateTimeOffset?)x.StartAt).Min() ?? structureCutoff;
        return bars.Where(x => x.End <= structureCutoff && x.Start >= episode.StartAt && x.Start < next
                               && x.Touches(zone.Lower, zone.Upper)).ToList();
    }

    static decimal? EpisodeLow(PriceZone zone, TouchEpisode episode, ImmutableArray<TouchEpisode> episodes,
        ImmutableArray<StructureBar> bars, DateTimeOffset structureCutoff)
    {
        var window = EpisodeBars(zone, episode, episodes, bars, structureCutoff);
        return window.Count == 0 ? null : window.Min(x => x.Low);
    }

    // ── 계획·품질·상태 ──

    static EntryCandidate Build(SetupDetectionRequest request, StructurePolicy policy, Hypothesis hypothesis,
        StructureBar trigger, ImmutableArray<StructureBar> bars, DateTimeOffset structureCutoff,
        DateTimeOffset triggerConfirmedAt, DateTimeOffset expiresAt, decimal entryReference,
        SortedSet<string> entryNotes, decimal? spread, SortedSet<string> readyBlockers)
    {
        var kindName = SetupKinds.Name(hypothesis.Kind);
        var eventId = EventId(request.Symbol, request.SessionStart, kindName, hypothesis.Zone.Id, structureCutoff);
        var guardKey = DuplicateGuardKey(request.Symbol, request.SessionStart, kindName, structureCutoff);

        var planning = StructuralPlanner.Evaluate(new PlanRequest(request.Symbol, eventId, kindName, entryReference,
            hypothesis.Anchor, hypothesis.Zone, request.Zones, request.Atr1mAtStructureCutoff, spread,
            triggerConfirmedAt, expiresAt, request.PriceTickSupported), policy);

        var relativeVolume = SessionIndicators.RelativeVolume(bars, trigger.Start, policy.RelativeVolumeLookbackBars);
        var quality = EntryQualityEvaluator.Evaluate(new EntryQualityInput(hypothesis.Kind,
            hypothesis.Zone.Strength?.Value, planning.TargetZone?.Strength?.Value, planning.NetR, entryReference,
            hypothesis.Anchor, request.Atr1mAtStructureCutoff, relativeVolume, request.Trend.SignedTrend,
            trigger.Close, hypothesis.Zone.Upper), policy);

        var notes = new SortedSet<string>(hypothesis.Notes, StringComparer.Ordinal);
        foreach (var note in entryNotes) notes.Add(note);

        var rejections = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var code in planning.ReasonCodes) rejections.Add(code);
        foreach (var code in readyBlockers) rejections.Add(code);
        foreach (var reason in quality.Reasons) rejections.Add(reason);

        // 실시간 유지 조건 붕괴는 INVALIDATED이며 재상승했다고 같은 이벤트를 되살리지 않는다(§10, §16B).
        var invalidated = false;
        if (request.LivePrice is { } live)
        {
            if (hypothesis.LiveFloorExclusive is { } exclusive && live <= exclusive)
            {
                invalidated = true;
                notes.Add(NoteLiveBelowBreakoutLevel);
            }
            if (hypothesis.LiveFloorInclusive is { } inclusive && live < inclusive)
            {
                invalidated = true;
                notes.Add(NoteLiveBelowSupportLower);
            }
            if (planning.Stop is { } stop && live <= stop)
            {
                invalidated = true;
                notes.Add(NoteLiveBelowStop);
            }
        }

        var expired = request.Now >= expiresAt;
        var disposition = invalidated ? CandidateDisposition.Invalidated
            : expired ? CandidateDisposition.Expired
            : !planning.Viable || !quality.ReadyAllowed || rejections.Count > 0 ? CandidateDisposition.Rejected
            : CandidateDisposition.Ready;

        return new EntryCandidate(eventId, guardKey, hypothesis.Kind, kindName, hypothesis.Zone.Id, trigger.Start,
            triggerConfirmedAt, structureCutoff, request.AnalysisAsOf, expiresAt, disposition, entryReference,
            hypothesis.Anchor, quality.Score, quality,
            disposition == CandidateDisposition.Ready ? planning.Plan : null, planning,
            rejections.ToImmutableArray(), notes.ToImmutableArray(), hypothesis.CounterTrend,
            hypothesis.RetestConfirmed, hypothesis.EpisodeStartAt);
    }

    /// <summary>§8 stable EventId=(symbol,sessionStart,kind,zoneId,triggerBarStart).</summary>
    public static string EventId(string symbol, DateTimeOffset sessionStart, string kindName, string zoneId,
        DateTimeOffset triggerBarStart) =>
        string.Join('|', symbol, StructureMath.Iso(sessionStart), kindName, zoneId, StructureMath.Iso(triggerBarStart));

    /// <summary>§8 추가 중복 방지 키=(symbol,sessionStart,kind,triggerBarStart). 이 키당 신규 거래는 최대 1개다.</summary>
    public static string DuplicateGuardKey(string symbol, DateTimeOffset sessionStart, string kindName,
        DateTimeOffset triggerBarStart) =>
        string.Join('|', symbol, StructureMath.Iso(sessionStart), kindName, StructureMath.Iso(triggerBarStart));
}

/// <summary>
/// 설계 §8 마지막 문단 + §16B PreferredCandidateId. READY만 대상으로 하고 없으면 null이다.
/// </summary>
public static class CandidateSelection
{
    /// <summary>
    /// 같은 종류: EntryQuality 내림차순 → netR 내림차순 → ZoneId ordinal.
    /// 종류 간: EntryQuality 내림차순 → 종류 문자열 ordinal → EventId ordinal.
    /// 같은 중복 방지 키에서는 실제 신규 거래 후보를 1개만 남긴다.
    /// </summary>
    public static EntryCandidate? SelectPreferred(IEnumerable<EntryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ready = candidates.Where(x => x.Disposition == CandidateDisposition.Ready && x.EntryQuality is not null).ToArray();
        if (ready.Length == 0) return null;

        var perKey = ready
            .GroupBy(x => x.DuplicateGuardKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(x => x.EntryQuality!.Value)
                .ThenByDescending(x => x.NetR ?? decimal.MinValue)
                .ThenBy(x => x.ZoneId, StringComparer.Ordinal)
                .First());

        return perKey
            .OrderByDescending(x => x.EntryQuality!.Value)
            .ThenBy(x => x.KindName, StringComparer.Ordinal)
            .ThenBy(x => x.EventId, StringComparer.Ordinal)
            .First();
    }

    /// <summary>READY가 없을 때 화면에 보일 대표 상태. UNKNOWN/WAIT를 실패나 0점으로 숨기지 않는다(§19-9).</summary>
    public static CandidateDisposition Summarize(IEnumerable<EntryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var list = candidates.ToArray();
        if (list.Length == 0) return CandidateDisposition.Wait;
        foreach (var disposition in new[]
                 {
                     CandidateDisposition.Entered, CandidateDisposition.Ready, CandidateDisposition.Invalidated,
                     CandidateDisposition.Expired, CandidateDisposition.Rejected
                 })
            if (list.Any(x => x.Disposition == disposition)) return disposition;
        return CandidateDisposition.Wait;
    }

    /// <summary>
    /// §16B: INVALIDATED/EXPIRED/ENTERED/REJECTED는 같은 EventId에서 되살리지 않는다.
    /// 새 트리거만 새 이벤트가 되며, 호가 개선이나 재상승으로 이전 거절이 살아나지 않는다.
    /// </summary>
    public static bool IsTerminal(CandidateDisposition disposition) => disposition is
        CandidateDisposition.Rejected or CandidateDisposition.Invalidated or
        CandidateDisposition.Expired or CandidateDisposition.Entered;

    public static CandidateDisposition Reconcile(CandidateDisposition? previous, CandidateDisposition computed) =>
        previous is { } prior && IsTerminal(prior) ? prior : computed;

    /// <summary>이전 상태가 종결됐다면 계획도 되살리지 않는다.</summary>
    public static EntryCandidate Reconcile(EntryCandidate? previous, EntryCandidate computed)
    {
        ArgumentNullException.ThrowIfNull(computed);
        if (previous is null || !IsTerminal(previous.Disposition)) return computed;
        if (previous.EventId != computed.EventId)
            throw new ArgumentException("Reconcile requires the same EventId.", nameof(previous));
        return computed with { Disposition = previous.Disposition, Plan = null };
    }
}
