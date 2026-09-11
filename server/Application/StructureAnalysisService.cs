using System.Collections.Concurrent;
using System.Collections.Immutable;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

// v5 구조 엔진 D3 — 설계 §12 Application 통합 + §16 관측 + §16B 모드/상태 단일 권위.
// v4 경로는 이 파일을 통과하지 않는다. off/shadow에서 v5는 관측과 조회만 소유하고
// 신규 진입·알림·simtrades/positions는 계속 v4가 소유한다(§18).

/// <summary>설계 §18 실행 모드. 기본 off이며 active 신규 진입 배선은 D6이다.</summary>
public enum StructureEngineMode { Off, Shadow, Active }

/// <summary>설정 `StructureEngineMode`의 파싱 결과. 알 수 없는 값은 안전한 off로 떨어진다.</summary>
public sealed record StructureEngineOptions(StructureEngineMode Mode)
{
    public static readonly StructureEngineOptions Off = new(StructureEngineMode.Off);

    public static StructureEngineOptions Parse(string? configured) => (configured ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "shadow" => new StructureEngineOptions(StructureEngineMode.Shadow),
        "active" => new StructureEngineOptions(StructureEngineMode.Active),
        _ => Off
    };

    public string Name => Mode switch
    {
        StructureEngineMode.Shadow => "shadow",
        StructureEngineMode.Active => "active",
        _ => "off"
    };
}

// ── 공개 DTO (§11 의미 유지, §12 additive) ─────────────────────────────────────
// 모든 double은 유한값만 노출한다. NaN/Infinity는 JSON에 쓰지 않고 결측(null)으로 남긴다(§11).

public sealed record StructureTrendDto(string State, double? SignedTrend, double? PriceDirection,
    double? StructureDirection, double? Efficiency, double? Atr1m, double? Ema9, double? Ema21, double? Vwap,
    double? VwapSd, bool StructureEvidenceMissing, int BarCount, DateTimeOffset AnalysisCutoff,
    string[] UsedFamilies, string[] MissingComponents, string[] Warnings);

public sealed record StructureZoneDto(string Id, int BoundsRevision, int SnapshotRevision, decimal Lower,
    decimal Upper, string Role, string OriginalRole, DateTimeOffset FirstConfirmedAt, DateTimeOffset LastConfirmedAt,
    string[] SourceKinds, int SourceCount, int IndependentFamilies, double? Strength, double? TouchEvidence,
    double? ReactionEvidence, double? Recency, double? Confluence, double? BreachPenalty, int CompletedEpisodes,
    int SuccessEpisodes, int FailedEpisodes, int PendingEpisodes, string[] MissingEvidence, bool Eligible,
    string[] RejectReasons, string[] ApproximationFlags, bool ProfileOnly, bool Retired);

public sealed record StructurePlanDto(string PlanId, string Kind, decimal EntryReference, decimal InvalidationAnchor,
    decimal Stop, decimal Target, string InvalidationZoneId, decimal InvalidationLower, decimal InvalidationUpper,
    string TargetZoneId, decimal TargetLower, decimal TargetUpper, decimal Buffer, string BufferBasis,
    decimal FrontRunBuffer, decimal NetReward, decimal NetRisk, decimal NetR, double? RiskPercent,
    decimal FeePerShare, decimal ExtraCostPerShare, decimal? ValidSpread, bool MissingLiquidity,
    string EligibilityCostModelVersion, string RealizedFillCostModelVersion, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string EngineVersion, string PolicyHash, string[] ReasonCodes, string Explanation);

public sealed record StructureQualityComponentDto(string Name, double? Raw, double? Value, bool Required);

public sealed record StructureCandidateDto(string EventId, string Kind, string ZoneId,
    DateTimeOffset TriggerBarStart, DateTimeOffset TriggerConfirmedAt, DateTimeOffset StructureCutoff,
    DateTimeOffset ExpiresAt, string State, double? EntryQuality, decimal EntryReference,
    decimal? InvalidationAnchor, decimal? Stop, decimal? Target, decimal? NetR, StructurePlanDto? Plan,
    StructureQualityComponentDto[] Components, string[] RejectionCodes, string[] Notes, bool CounterTrend,
    bool RetestConfirmed);

public sealed record StructureSourceQualityDto(string Source, string Status, int Count, int? ExpectedCount,
    DateTimeOffset? First, DateTimeOffset? Last, string[] Gaps, string[] Conflicts, double? CoverageRatio,
    string[] Warnings);

public sealed record StructureQualityDto(StructureSourceQualityDto[] Sources, string[] Warnings,
    string[] BlockersForTrend, string[] BlockersForZone, string[] BlockersForCandidate, string[] BlockersForReady);

/// <summary>
/// 설계 §11 StructureAnalysis의 공개 형태. 마지막으로 공개된 snapshot이며 조회가 계산을 유발하지 않는다(§12).
/// </summary>
public sealed record StructureAnalysisView(string Symbol, string Mode, string Status, string EntryOwner,
    string EngineVersion, string RecordVersion, string PolicyHash, DateTimeOffset? SessionStart,
    DateTimeOffset? SessionEnd, DateTimeOffset? AnalysisAsOf, DateTimeOffset? LastCompletedBarStart,
    DateTimeOffset? QuoteAt, decimal? QuotePrice, DateTimeOffset EvaluatedAt, long Generation,
    string CandidateSummary, string? PreferredCandidateId, StructureTrendDto? Trend, StructureZoneDto[] Zones,
    StructureCandidateDto[] Candidates, StructureQualityDto Quality, string[] Warnings, string[] Notes);

/// <summary>폴링이 넘겨주는 v5 입력. v4가 이미 사용한 원본 응답을 그대로 재사용하고 추가 조회를 하지 않는다.</summary>
public sealed record StructureObservationRequest(string Symbol, long Generation, MarketSession Market,
    IReadOnlyList<Candle>? OneMinuteBars, IReadOnlyList<Candle>? DailyBars, double? QuotePrice,
    DateTimeOffset? QuoteAt, StructureLiquidity? Liquidity = null);

/// <summary>
/// 설계 §12. snapshot 구성 → 순수 계산(gate 밖) → 짧은 commit(gate 안, generation 재검증) → 공개 snapshot 갱신.
/// 새 완료 봉이 없으면 전체 구조를 재계산하지 않고 실시간 무효화·만료만 확인한다.
/// </summary>
public sealed class StructureAnalysisService(
    ILocalStore store,
    StructureObservationWriter observations,
    MonitorRuntimeState runtime,
    TimeProvider clock,
    IMonitorDiagnostics diagnostics,
    StructureEngineOptions? options = null,
    StructurePolicy? policy = null,
    IStructuralTradeEntries? tradeEntries = null,
    StructureAlertPublisher? alerts = null)
{
    public const string LatchFile = "structure-lifecycle.json";
    public const string EntryOwnerV4 = "v4";
    public const string EntryOwnerV5 = "v5";
    public const string NoteEntryCommitted = "V5_ENTRY_COMMITTED";
    public const string NoteEntryBlockedByOpenTrade = "V5_ENTRY_BLOCKED_BY_OPEN_TRADE";
    public const string NoteEntryPlanInvalid = "V5_ENTRY_PLAN_INVALID";
    public const string NoteEntryUnavailable = "V5_ENTRY_PORT_UNAVAILABLE";

    /// <summary>§16B 가격 단위: 관측된 가격이 정책 tick의 배수가 아니다(신규 READY 금지).</summary>
    public const string NotePriceTickUnsupported = "V5_PRICE_TICK_UNSUPPORTED";

    /// <summary>§16B 가격 단위: tick을 판정할 가격 근거가 없다. 모르면 허용이 아니라 차단이다.</summary>
    public const string NotePriceTickUnknown = "V5_PRICE_TICK_UNKNOWN";

    /// <summary>tick 판정에 쓰는 최근 완료 봉 수. 과거 한 건의 이상 호가가 하루 전체를 막지 않게 한다.</summary>
    public const int PriceTickSampleBars = 30;

    readonly StructureEngineOptions _options = options ?? StructureEngineOptions.Off;
    readonly StructurePolicy _policy = policy ?? StructurePolicy.Default;
    readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, StructuralLatch> _latches = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, StructureAnalysisView> _published = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _restoreGate = new(1, 1);
    int _restored;

    public StructureEngineMode Mode => _options.Mode;
    public string ModeName => _options.Name;
    public string EngineVersion => _policy.Version;
    public string PolicyHash => _policy.PolicyHash;

    /// <summary>
    /// §18/§16B: off·shadow는 v4가, active는 v5가 신규 진입을 소유한다(D6 배선).
    /// active에서 v5 오류/UNAVAILABLE은 진입 보류이며 v4로 자동 fallback하지 않는다.
    /// </summary>
    public string EntryOwner => _options.Mode == StructureEngineMode.Active ? EntryOwnerV5 : EntryOwnerV4;

    /// <summary>세션 종료·모니터링 중지 시 메모리를 정리한다(§16 "메모리도 세션 종료 시 정리한다").</summary>
    public void Clear() { _cache.Clear(); _published.Clear(); _latches.Clear(); }

    public void Remove(string symbol)
    {
        _cache.TryRemove(symbol, out _);
        _published.TryRemove(symbol, out _);
        _latches.TryRemove(symbol, out _);
    }

    public bool TryGetPublished(string symbol, out StructureAnalysisView view) =>
        _published.TryGetValue(symbol, out view!);

    // ── 관측 경로 ────────────────────────────────────────────────────────────

    public async Task ObserveAsync(StructureObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // off는 계산을 유발하지 않는다(§16B).
        if (_options.Mode == StructureEngineMode.Off) return;

        var now = clock.GetLocalNow();
        var build = StructureSnapshotFactory.Create(request.Symbol, request.Market, request.OneMinuteBars,
            request.DailyBars, request.QuotePrice, request.QuoteAt, now, request.Generation, _policy,
            request.Liquidity);

        if (build.Snapshot is null || build.LastCompletedBarStart is null || build.LastCompletedBarEnd is null)
        {
            PublishStatus(request, build, now);
            return;
        }

        var snapshot = build.Snapshot;
        var lastBarStart = build.LastCompletedBarStart.Value;
        var lastBarEnd = build.LastCompletedBarEnd.Value;

        await RestoreAsync(ct);
        var latch = Latch(request.Symbol, snapshot.SessionStart);

        _cache.TryGetValue(request.Symbol, out var cached);
        var reusable = cached is not null && cached.SessionStart == snapshot.SessionStart &&
                       string.Equals(cached.PolicyHash, PolicyHash, StringComparison.Ordinal);

        // §12.4: 새 봉이 없는 poll은 구조를 전체 재계산하지 않는다. live 무효화·신선도는 매 poll 확인한다.
        if (reusable && cached!.AnalysisAsOf == snapshot.AnalysisAsOf && cached.Generation == request.Generation)
        {
            var refreshed = StructuralLifecycle.ApplyLive(cached.Candidates, snapshot.QuotePrice, now);
            if (Signature(refreshed) == Signature(cached.Candidates)) return;
            var preferredId = CandidateSelection.SelectPreferred(refreshed)?.EventId;
            var liveView = View(request, build, snapshot, lastBarStart, cached.Trend, cached.Zones, refreshed,
                cached.Quality, cached.Notes, cached.Warnings, preferredId, now, ImmutableArray<string>.Empty);
            using (await runtime.EnterControlAsync(ct))
            {
                if (!Current(request, snapshot, now)) return;
                _cache[request.Symbol] = cached with { Candidates = refreshed, View = liveView };
                _published[request.Symbol] = liveView;
            }
            return;
        }

        // ── gate 밖 순수 계산: I/O 없음(§12.7) ──
        var structureCutoff = lastBarStart;
        var previousZones = reusable ? cached!.Zones : ImmutableArray<PriceZone>.Empty;
        // 후보 계층은 반드시 structureCutoff(=트리거 봉 시작) 기준이어야 한다. 표시 계층(cutoff=analysisAsOf)을
        // 재사용하면 트리거 봉이 자기 근거가 되어 §8 트리거 봉 격리가 깨진다. 그래서 두 계층을 따로 캐시한다.
        var candidateLayer = reusable && cached!.Cutoff == structureCutoff
            ? cached.CandidateLayer
            : BuildLayer(snapshot, structureCutoff, build, previousZones, latch.RetiredZoneIds);
        var displayLayer = BuildLayer(snapshot, snapshot.AnalysisAsOf, build, candidateLayer.Zones,
            candidateLayer.RetiredZoneIds);

        var trend = TrendEvaluator.Evaluate(TrendRequest.Create(snapshot.Symbol, snapshot.SessionStart,
            snapshot.AnalysisAsOf, build.Bars.Bars, build.FiveMinuteBars), _policy);

        var gate = StructuralLifecycle.Gate(latch, lastBarStart, lastBarEnd, lastBarEnd - lastBarStart, now, _policy);

        var blockers = build.Quality.BlockersForCandidate.Concat(gate.Blockers)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToImmutableArray();

        // #61 §16B: tick 지원 여부는 Application이 판정해서 넘긴다. Domain 기본값(허용)에 의존하지 않는다.
        var tickNote = PriceTickNote(build.Bars.Bars, snapshot.QuotePrice, _policy);

        var detection = SetupDetector.Detect(SetupDetectionRequest.Create(snapshot.Symbol, snapshot.SessionStart,
            snapshot.SessionEnd, snapshot.AnalysisAsOf, now, build.Bars.Bars, candidateLayer.Zones,
            candidateLayer.Episodes, trend, candidateLayer.Atr1m, snapshot.QuotePrice, snapshot.QuoteAt,
            snapshot.OptionalLiquidity, blockers, tickNote is null), _policy);

        var candidates = StructuralLifecycle.ApplyLive(
            // zones는 §10 돌파 쿨다운의 zone lineage(Aliases) 확인용이며 후보 계층(structureCutoff) 스냅샷이다.
            StructuralLifecycle.ApplyLatch(latch, detection.Candidates, gate.AllowNewTrigger, _policy,
                candidateLayer.Zones),
            snapshot.QuotePrice, now);
        var preferred = CandidateSelection.SelectPreferred(candidates)?.EventId;

        var quality = StructureSnapshotFactory.Complete(build.Quality, trend, displayLayer.Profile,
            displayLayer.Warnings.Concat(candidateLayer.Warnings), detection.SpreadReasons);

        var notes = new SortedSet<string>(gate.Notes, StringComparer.Ordinal);
        foreach (var warning in detection.Warnings) notes.Add(warning);
        if (gate.MissedBars > 0) notes.Add($"{StructuralLifecycle.NoteMissedBars}x{gate.MissedBars}");

        var warnings = new SortedSet<string>(build.Warnings, StringComparer.Ordinal);
        foreach (var blocker in detection.ReadyBlockers) warnings.Add(blocker);
        if (tickNote is not null) { notes.Add(tickNote); warnings.Add(tickNote); }

        var observationId = StructuralLifecycle.ObservationId(snapshot.Symbol, lastBarStart, PolicyHash);
        var signature = StructuralLifecycle.EventSignature(candidates, preferred);
        var full = !string.Equals(signature, latch.LastEventSignature, StringComparison.Ordinal);

        var zoneDtos = displayLayer.Zones.Select(StructureViewMapper.Zone).ToArray();
        var candidateDtos = candidates.Select(StructureViewMapper.Candidate).ToArray();
        var trendDto = StructureViewMapper.Trend(trend);
        var qualityDto = StructureViewMapper.Quality(quality);
        var summary = (detection.Candidates.Length == 0
            ? CandidateDisposition.Wait
            : CandidateSelection.Summarize(candidates)).ToString().ToUpperInvariant();

        var record = new StructureObservationRecord(observationId, RecordVersion, snapshot.Symbol, now,
            snapshot.SessionStart, snapshot.AnalysisAsOf, snapshot.QuoteAt, lastBarStart, PolicyHash, EngineVersion,
            ModeName, EntryOwner, full ? "full" : "summary", build.Status, summary, preferred, trendDto,
            full ? qualityDto : null, full ? zoneDtos : null, candidateDtos,
            warnings.ToArray(), notes.ToArray());

        // ── gate 안 짧은 commit: generation/session 재검증 후 저장, 저장 성공 뒤에만 래치·공개 snapshot 갱신 ──
        using (await runtime.EnterControlAsync(ct))
        {
            if (!Current(request, snapshot, clock.GetLocalNow())) return;

            // ── D6 active 진입(§18): READY 대표 후보 1개만 실제 시뮬 거래로 커밋한다. 거래 저장이 성공한 뒤에만
            // 후보를 ENTERED로 바꾸고 래치에 tombstone을 남기며, 실패하면 관측·래치도 갱신하지 않아 다음 poll이
            // 같은 이벤트를 멱등하게 재시도한다(§12.6). off/shadow에서는 이 경로 자체가 없다.
            // 이슈 #26: 이 commit에서 READY로 커밋되는 후보를 알림 초안으로 잡아 둔다(진입 성공 시 ENTERED로 바뀌기 전).
            var readyForAlerts = candidates.Where(x => x.Disposition == CandidateDisposition.Ready).ToArray();
            var activeEntry = await TryEnterPreferredAsync(candidates, preferred, snapshot, trend, now, ct);
            if (activeEntry is not null)
            {
                candidates = activeEntry.Candidates;
                if (activeEntry.Note is { } entryNote) notes.Add(entryNote);
                if (activeEntry.Entered)
                {
                    preferred = CandidateSelection.SelectPreferred(candidates)?.EventId;
                    signature = StructuralLifecycle.EventSignature(candidates, preferred);
                    full = !string.Equals(signature, latch.LastEventSignature, StringComparison.Ordinal);
                    candidateDtos = candidates.Select(StructureViewMapper.Candidate).ToArray();
                    summary = CandidateSelection.Summarize(candidates).ToString().ToUpperInvariant();
                }
                record = record with
                {
                    Detail = full ? "full" : "summary", CandidateSummary = summary, PreferredCandidateId = preferred,
                    Candidates = candidateDtos, Zones = full ? zoneDtos : null, Quality = full ? qualityDto : null,
                    Notes = notes.ToArray()
                };
            }

            var write = await observations.AppendAsync(record, ct);
            if (!Current(request, snapshot, clock.GetLocalNow())) return;

            // ── 이슈 #26 v5 알림 이벤트(승인 설계안 §4): 커밋 순서는 [진입 커밋 → 관측 append → 알림 키 append →
            // 래치 commit]. 알림 영속이 실패하면 예외로 래치 commit이 막히고 다음 poll이 멱등 재시도한다.
            // active에서만 발행한다 — off/shadow는 v5 이벤트를 만들지 않는다(§16B).
            if (_options.Mode == StructureEngineMode.Active && alerts is not null)
            {
                var drafts = AlertDrafts(readyForAlerts, activeEntry, candidates, preferred);
                if (drafts.Count > 0)
                    await alerts.PublishAsync(snapshot.Symbol, snapshot.SessionStart, PolicyHash, drafts,
                        snapshot.QuotePrice, now, ct);
            }

            // 이슈 #44: 한도 압박은 조용히 넘어가지 않는다. 주기 요약 희생은 ObservationStorageLimited로,
            // 핵심 관측까지 누락된 전체 검증 불가 상태는 ObservationCoreStorageLimited로 API에 드러난다.
            var storageWarnings = StructureObservationWriter.StorageWarnings(write);
            var view = View(request, build, snapshot, lastBarStart, trend, displayLayer.Zones, candidates, quality,
                notes.ToImmutableArray(), warnings.ToImmutableArray(), preferred, now, storageWarnings,
                zoneDtos, candidateDtos, trendDto, qualityDto, summary);

            // watermark는 신규 트리거 허용 여부와 무관하게 평가한 최신 완료 봉까지 전진한다(§16B seeding).
            var committed = StructuralLifecycle.Commit(latch, lastBarStart, candidates,
                displayLayer.RetiredZoneIds, signature, observationId);
            _latches[request.Symbol] = committed;
            await PersistAsync(ct);

            _cache[request.Symbol] = new CacheEntry(snapshot.SessionStart, snapshot.AnalysisAsOf, structureCutoff,
                PolicyHash, request.Generation, displayLayer, candidateLayer, candidates, trend, quality,
                notes.ToImmutableArray(), warnings.ToImmutableArray(), view);
            _published[request.Symbol] = view;
        }
    }

    /// <summary>
    /// §18 active에서 성립한 READY 대표 후보 1개를 실제 시뮬 거래로 옮긴다. 진입가·손절·목표는 전부
    /// 후보의 계획(StructuralPlanner 산출)에서 오고 여기서 어떤 가격도 만들지 않는다(§19-5).
    /// 반환 null = 이 poll에 진입 시도 자체가 없음(off/shadow, READY 없음). 오류·거절은 v4 fallback 없이
    /// 진입 보류로 남긴다(§16B).
    /// </summary>
    async Task<ActiveEntryResult?> TryEnterPreferredAsync(ImmutableArray<EntryCandidate> candidates,
        string? preferredId, StructureSnapshot snapshot, TrendAssessment? trend, DateTimeOffset now,
        CancellationToken ct)
    {
        if (_options.Mode != StructureEngineMode.Active || preferredId is null) return null;
        var chosen = candidates.FirstOrDefault(x => x.EventId == preferredId);
        if (chosen is null || chosen.Disposition != CandidateDisposition.Ready || chosen.Plan is null) return null;

        if (tradeEntries is null) return new ActiveEntryResult(candidates, false, NoteEntryUnavailable);

        // §10: 체결 시 FrozenPlan을 저장한다. 진입 이후 이 스냅샷은 다시 만들지 않는다.
        var context = Domain.StructuralSimulation.Freeze(chosen.Plan, chosen.EventId,
            (trend?.State ?? TrendState.Unknown).ToString().ToUpperInvariant(), trend?.SignedTrend,
            chosen.EntryQuality, snapshot.AnalysisAsOf, snapshot.QuoteAt);
        var result = await tradeEntries.TryEnterAsync(new Domain.StructuralEntryRequest(snapshot.Symbol,
            chosen.TriggerBarStart, now, snapshot.SessionEnd, context), ct);

        return result.Outcome switch
        {
            // AlreadyEntered = 이전 poll에서 거래는 저장됐지만 래치 커밋 전에 중단된 경우의 멱등 회복(§12.6).
            Domain.StructuralEntryOutcome.Entered or Domain.StructuralEntryOutcome.AlreadyEntered =>
                new ActiveEntryResult(candidates
                        .Select(x => x.EventId != chosen.EventId
                            ? x
                            : x with
                            {
                                Disposition = CandidateDisposition.Entered,
                                Notes = x.Notes.Contains(NoteEntryCommitted, StringComparer.Ordinal)
                                    ? x.Notes
                                    : x.Notes.Append(NoteEntryCommitted).OrderBy(n => n, StringComparer.Ordinal)
                                        .ToImmutableArray()
                            })
                        .ToImmutableArray(),
                    true, NoteEntryCommitted),
            // 한 종목 OPEN 하나 제한은 버전 공통이다(§18). 후보는 READY로 남고 새 거래는 만들지 않는다.
            Domain.StructuralEntryOutcome.BlockedByOpenTrade =>
                new ActiveEntryResult(candidates, false, NoteEntryBlockedByOpenTrade),
            _ => new ActiveEntryResult(candidates, false, NoteEntryPlanInvalid)
        };
    }

    sealed record ActiveEntryResult(ImmutableArray<EntryCandidate> Candidates, bool Entered, string? Note);

    /// <summary>
    /// 이슈 #26: commit 지점의 후보·진입 결과에서 알림 초안을 파생한다. 새 가격·점수를 만들지 않고
    /// 후보 계획의 값(EntryQuality/NetR/Stop/Target)만 옮긴다. INVALIDATED/EXPIRED는 상태 칩으로만
    /// 보이고 푸시 이벤트를 만들지 않는다(소음 방지, 승인 설계안 §4). 중복 제거는 publisher의 영속 키가 맡는다.
    /// </summary>
    static List<StructureAlertDraft> AlertDrafts(IReadOnlyList<EntryCandidate> readyAtCommit,
        ActiveEntryResult? activeEntry, ImmutableArray<EntryCandidate> candidates, string? preferred)
    {
        var drafts = new List<StructureAlertDraft>();
        // 이 commit에서 READY로 성립한 모든 후보. 같은 EventId의 재커밋은 publisher 키가 걸러낸다.
        foreach (var ready in readyAtCommit)
            drafts.Add(new StructureAlertDraft(StructureAlertPublisher.TypeReady, ready.EventId, ready.KindName,
                StructureViewMapper.Finite(ready.EntryQuality), ready.Plan?.NetR));
        if (activeEntry is null) return drafts;

        if (activeEntry.Entered)
        {
            // 진입 커밋(멱등 회복 포함): READY였다가 이 commit에서 ENTERED가 된 후보 하나다.
            // AlreadyEntered 회복의 재발행은 publisher 키가 막는다(첫 발행이 성공했다면 재발행 없음).
            var entered = candidates.FirstOrDefault(x => x.Disposition == CandidateDisposition.Entered &&
                readyAtCommit.Any(r => r.EventId == x.EventId));
            if (entered?.Plan is { } plan)
                drafts.Add(new StructureAlertDraft(StructureAlertPublisher.TypeEntered, entered.EventId,
                    entered.KindName, StructureViewMapper.Finite(entered.EntryQuality), plan.NetR,
                    plan.PlanId, plan.Stop, plan.Target));
            return drafts;
        }

        // 차단 3종(OPEN 제한/계획 무효/포트 부재). 진입이 없었으므로 preferred는 시도한 후보 그대로다.
        if (activeEntry.Note is NoteEntryBlockedByOpenTrade or NoteEntryPlanInvalid or NoteEntryUnavailable &&
            preferred is not null &&
            candidates.FirstOrDefault(x => x.EventId == preferred) is { } attempted)
            drafts.Add(new StructureAlertDraft(StructureAlertPublisher.TypeBlocked, attempted.EventId,
                attempted.KindName, StructureViewMapper.Finite(attempted.EntryQuality), attempted.Plan?.NetR,
                Reason: activeEntry.Note));
        return drafts;
    }

    string RecordVersion =>
        // §11/§16B: `v5-structure.1-shadow`는 shadow 관측 레코드 버전이며 SimTrade.Logic에 쓰지 않는다.
        // D6부터 active는 v5가 신규 진입을 소유하므로 관측 레코드도 본 버전으로 기록한다(§11 "별도 저장").
        _options.Mode == StructureEngineMode.Active ? _policy.Version : _policy.Version + "-shadow";

    bool Current(StructureObservationRequest request, StructureSnapshot snapshot, DateTimeOffset now)
    {
        if (!runtime.IsCurrent(request.Generation)) return false;
        var state = runtime.Snapshot();
        if (state.Market.Start != snapshot.SessionStart || state.Market.End != snapshot.SessionEnd) return false;
        return now < snapshot.SessionEnd;
    }

    /// <summary>
    /// #61 §16B: 첫 버전은 tick USD 0.01 종목만 신규 READY 대상이다. 앱에 tick metadata가 없으므로 관측된
    /// 가격(최근 완료 봉 OHLC + 현재 시세)이 전부 정책 tick의 배수인지로 판정하고, 근거가 없으면 차단한다.
    /// 반환 null이 지원이며 그 외는 관측에 남길 사유다.
    /// </summary>
    public static string? PriceTickNote(IEnumerable<StructureBar> bars, decimal? quotePrice, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(policy);
        var tick = policy.PriceTick;
        if (tick <= 0) return NotePriceTickUnknown;

        var observed = 0;
        foreach (var bar in bars.TakeLast(PriceTickSampleBars))
            foreach (var price in new[] { bar.Open, bar.High, bar.Low, bar.Close })
            {
                if (price <= 0) continue;
                observed++;
                if (decimal.Remainder(price, tick) != 0m) return NotePriceTickUnsupported;
            }

        if (quotePrice is { } quote && quote > 0)
        {
            observed++;
            if (decimal.Remainder(quote, tick) != 0m) return NotePriceTickUnsupported;
        }

        return observed == 0 ? NotePriceTickUnknown : null;
    }

    StructureLayer BuildLayer(StructureSnapshot snapshot, DateTimeOffset cutoff, StructureSnapshotBuild build,
        ImmutableArray<PriceZone> previousZones, IEnumerable<string> retired)
    {
        var retiredIds = retired.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableArray();
        var fiveMinute = cutoff == snapshot.AnalysisAsOf
            ? build.FiveMinuteBars
            : BarAggregator.Aggregate(build.Bars.Bars, snapshot.SessionStart, cutoff, _policy);
        var built = ZoneBuilder.Build(new ZoneBuildRequest(snapshot.Symbol, snapshot.SessionStart,
            snapshot.SessionEnd, cutoff, build.Bars.Bars, fiveMinute, build.DailyBars, previousZones, retiredIds),
            _policy);
        var evaluated = ZoneEvaluator.Evaluate(built.Zones, new ZoneEvaluationRequest(snapshot.SessionStart, cutoff,
            build.Bars.Bars, previousZones, built.RetiredZoneIds), _policy);
        return new StructureLayer(cutoff, evaluated.Zones, evaluated.Episodes, built.Profile,
            evaluated.RetiredZoneIds, built.Warnings, built.Atr1mAtCutoff);
    }

    void PublishStatus(StructureObservationRequest request, StructureSnapshotBuild build, DateTimeOffset now)
    {
        var view = new StructureAnalysisView(request.Symbol, ModeName, build.Status, EntryOwner, EngineVersion,
            RecordVersion, PolicyHash, request.Market.Start, request.Market.End, null,
            build.LastCompletedBarStart, request.QuoteAt, null, now, request.Generation,
            CandidateDisposition.Wait.ToString().ToUpperInvariant(), null, null, [], [],
            StructureViewMapper.Quality(build.Quality), build.Warnings.ToArray(), []);
        if (!runtime.IsCurrent(request.Generation)) return;
        _cache.TryRemove(request.Symbol, out _);
        _published[request.Symbol] = view;
    }

    StructureAnalysisView View(StructureObservationRequest request, StructureSnapshotBuild build,
        StructureSnapshot snapshot, DateTimeOffset lastBarStart, TrendAssessment? trend,
        ImmutableArray<PriceZone> zones, ImmutableArray<EntryCandidate> candidates, DataQuality quality,
        ImmutableArray<string> notes, ImmutableArray<string> warnings, string? preferred, DateTimeOffset now,
        ImmutableArray<string> extraWarnings, StructureZoneDto[]? zoneDtos = null,
        StructureCandidateDto[]? candidateDtos = null, StructureTrendDto? trendDto = null,
        StructureQualityDto? qualityDto = null, string? summary = null)
    {
        var allWarnings = warnings.Concat(extraWarnings).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new StructureAnalysisView(snapshot.Symbol, ModeName, build.Status, EntryOwner, EngineVersion,
            RecordVersion, PolicyHash, snapshot.SessionStart, snapshot.SessionEnd, snapshot.AnalysisAsOf,
            lastBarStart, snapshot.QuoteAt, snapshot.QuotePrice, now, request.Generation,
            summary ?? (candidates.Length == 0
                ? CandidateDisposition.Wait.ToString().ToUpperInvariant()
                : CandidateSelection.Summarize(candidates).ToString().ToUpperInvariant()),
            preferred, trendDto ?? (trend is null ? null : StructureViewMapper.Trend(trend)),
            zoneDtos ?? zones.Select(StructureViewMapper.Zone).ToArray(),
            candidateDtos ?? candidates.Select(StructureViewMapper.Candidate).ToArray(),
            qualityDto ?? StructureViewMapper.Quality(quality), allWarnings, notes.ToArray());
    }

    static string Signature(ImmutableArray<EntryCandidate> candidates) =>
        StructuralLifecycle.EventSignature(candidates, CandidateSelection.SelectPreferred(candidates)?.EventId);

    // ── 지속 래치 ────────────────────────────────────────────────────────────

    StructuralLatch Latch(string symbol, DateTimeOffset sessionStart)
    {
        if (_latches.TryGetValue(symbol, out var existing) && existing.Matches(sessionStart, PolicyHash))
            return existing;
        var fresh = StructuralLatch.Empty(symbol, sessionStart, PolicyHash);
        _latches[symbol] = fresh;
        return fresh;
    }

    async Task RestoreAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _restored) == 1) return;
        await _restoreGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _restored) == 1) return;
            var text = await observations.Store.ReadTextAsync(LatchFile, ct);
            foreach (var record in StructureLatchStorage.Parse(text))
                _latches.TryAdd(record.Symbol, record);
            Volatile.Write(ref _restored, 1);
        }
        catch (Exception ex)
        {
            // 복원 실패는 watermark를 새로 seed하는 보수적 결과가 된다. 조용히 이벤트를 만들지 않는다.
            Volatile.Write(ref _restored, 1);
            diagnostics.PollFailed("structure-latch-restore", ex);
        }
        finally { _restoreGate.Release(); }
    }

    Task PersistAsync(CancellationToken ct) =>
        observations.Store.WriteTextAsync(LatchFile, StructureLatchStorage.Serialize(_latches.Values), ct);

    // ── 조회 (§12: 요청이 계산을 유발하지 않는다) ──────────────────────────────

    public async Task<(int HttpStatus, StructureQueryResponse? Response)> GetAsync(string input, CancellationToken ct)
    {
        var symbol = (input ?? string.Empty).Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Z0-9.-]{1,12}$")) return (400, null);
        var watch = await store.Read("watchlist.json", new List<WatchItem>());
        if (watch.All(x => !x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) return (404, null);

        var now = clock.GetLocalNow();
        if (_options.Mode == StructureEngineMode.Off)
            return (200, Response(symbol, StructureAnalysisStatus.Disabled, now,
                "구조 엔진이 off입니다. v4 신호가 신규 진입을 소유합니다.", null));

        var state = runtime.Snapshot();
        if (!state.Running)
            return (200, Response(symbol, StructureAnalysisStatus.Stopped, now, "모니터링이 중지되어 있습니다.", null));
        if (state.Market.Start is not { } start || state.Market.End is not { } end || !MarketRules.IsOpen(now, start, end))
            return (200, Response(symbol, StructureAnalysisStatus.MarketClosed, now,
                "미국 정규장 외에는 구조 분석을 갱신하지 않습니다.", null));

        if (!_published.TryGetValue(symbol, out var view))
            return (200, Response(symbol, StructureAnalysisStatus.Warmup, now,
                "완료된 정규장 1분봉이 모이면 구조 분석을 표시합니다.", null));

        return (200, Response(symbol, view.Status, now, null, view));
    }

    StructureQueryResponse Response(string symbol, string status, DateTimeOffset now, string? message,
        StructureAnalysisView? view) =>
        new(symbol, ModeName, status, EntryOwner, EngineVersion, PolicyHash, message, view, now);

    /// <summary>§12: `GET /api/state`에 붙는 additive 요약. 기존 score/action의 v4 의미를 덮어쓰지 않는다.</summary>
    public object Summary(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var rows = symbols
            .Select(symbol =>
            {
                _published.TryGetValue(symbol, out var view);
                return (object)new
                {
                    symbol,
                    status = view?.Status ?? (_options.Mode == StructureEngineMode.Off
                        ? StructureAnalysisStatus.Disabled
                        : StructureAnalysisStatus.Warmup),
                    trendState = view?.Trend?.State,
                    signedTrend = view?.Trend?.SignedTrend,
                    candidateState = view?.CandidateSummary,
                    preferredCandidateId = view?.PreferredCandidateId,
                    entryQuality = view?.Candidates
                        .Where(x => x.EventId == view.PreferredCandidateId)
                        .Select(x => x.EntryQuality).FirstOrDefault(),
                    // 이슈 #26: 대표 후보의 종류(PULLBACK/BREAKOUT/REBOUND). 새 계산 없이 후보 값을 옮긴다.
                    preferredKind = view?.Candidates
                        .Where(x => x.EventId == view.PreferredCandidateId)
                        .Select(x => x.Kind).FirstOrDefault(),
                    analysisAsOf = view?.AnalysisAsOf,
                    quoteAt = view?.QuoteAt,
                    warnings = view?.Warnings ?? []
                };
            })
            .ToArray();
        return new
        {
            mode = ModeName,
            entryOwner = EntryOwner,
            legacyScoreEngine = "v4",
            engineVersion = EngineVersion,
            policyHash = PolicyHash,
            notes = Array.Empty<string>(),
            symbols = rows
        };
    }

    sealed record StructureLayer(DateTimeOffset Cutoff, ImmutableArray<PriceZone> Zones,
        ImmutableArray<TouchEpisode> Episodes, VolumeProfile Profile, ImmutableArray<string> RetiredZoneIds,
        ImmutableArray<string> Warnings, double? Atr1m);

    sealed record CacheEntry(DateTimeOffset SessionStart, DateTimeOffset AnalysisAsOf, DateTimeOffset Cutoff,
        string PolicyHash, long Generation, StructureLayer Layer, StructureLayer CandidateLayer,
        ImmutableArray<EntryCandidate> Candidates,
        TrendAssessment? Trend, DataQuality Quality, ImmutableArray<string> Notes,
        ImmutableArray<string> Warnings, StructureAnalysisView View)
    {
        public ImmutableArray<PriceZone> Zones => Layer.Zones;
        public ImmutableArray<TouchEpisode> Episodes => Layer.Episodes;
    }
}

public sealed record StructureQueryResponse(string Symbol, string Mode, string Status, string EntryOwner,
    string EngineVersion, string PolicyHash, string? Message, StructureAnalysisView? Analysis,
    DateTimeOffset UpdatedAt);

/// <summary>Domain 계산 결과를 공개 DTO로 옮긴다. NaN/Infinity는 결측으로 바꾸고 0으로 대체하지 않는다(§11).</summary>
public static class StructureViewMapper
{
    public static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    public static StructureTrendDto Trend(TrendAssessment trend)
    {
        ArgumentNullException.ThrowIfNull(trend);
        return new StructureTrendDto(trend.State.ToString().ToUpperInvariant(), Finite(trend.SignedTrend),
            Finite(trend.PriceDirection), Finite(trend.StructureDirection), Finite(trend.Efficiency),
            Finite(trend.Atr1m), Finite(trend.Ema9), Finite(trend.Ema21), Finite(trend.Vwap), Finite(trend.VwapSd),
            trend.StructureEvidenceMissing, trend.BarCount, trend.AnalysisCutoff, trend.UsedFamilies.ToArray(),
            trend.MissingComponents.ToArray(), trend.Warnings.ToArray());
    }

    public static StructureZoneDto Zone(PriceZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var strength = zone.Strength;
        return new StructureZoneDto(zone.Id, zone.BoundsRevision, zone.SnapshotRevision, zone.Lower, zone.Upper,
            zone.Role.ToString().ToUpperInvariant(), zone.OriginalRole.ToString().ToUpperInvariant(),
            zone.FirstConfirmedAt, zone.LastConfirmedAt,
            zone.Sources.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            zone.Sources.Length, strength?.IndependentFamilies ?? 0, Finite(strength?.Value),
            Finite(strength?.TouchEvidence), Finite(strength?.ReactionEvidence), Finite(strength?.Recency),
            Finite(strength?.Confluence), Finite(strength?.BreachPenalty), strength?.CompletedEpisodes ?? 0,
            strength?.SuccessEpisodes ?? 0, strength?.FailedEpisodes ?? 0, strength?.PendingEpisodes ?? 0,
            strength?.MissingComponents.ToArray() ?? [], zone.Eligible, zone.RejectReasons.ToArray(),
            zone.ApproximationFlags.ToArray(), zone.ProfileOnly, zone.Retired);
    }

    public static StructurePlanDto? Plan(StructuralTradePlan? plan) => plan is null
        ? null
        : new StructurePlanDto(plan.PlanId, plan.Kind, plan.EntryReference, plan.InvalidationAnchor, plan.Stop,
            plan.Target, plan.InvalidationZoneSnapshot.Id, plan.InvalidationZoneSnapshot.Lower,
            plan.InvalidationZoneSnapshot.Upper, plan.TargetZoneSnapshot.Id, plan.TargetZoneSnapshot.Lower,
            plan.TargetZoneSnapshot.Upper, plan.Buffer, plan.BufferBasis, plan.FrontRunBuffer, plan.NetReward,
            plan.NetRisk, plan.NetR, Finite(plan.RiskPercent), plan.Costs.FeePerShare,
            plan.Costs.ExtraCostPerShare, plan.Costs.ValidSpread, plan.Costs.MissingLiquidity,
            plan.Costs.EligibilityCostModelVersion, plan.Costs.RealizedFillCostModelVersion, plan.CreatedAt,
            plan.ExpiresAt, plan.EngineVersion, plan.PolicyHash, plan.ReasonCodes.ToArray(),
            plan.HumanExplanation);

    public static StructureCandidateDto Candidate(EntryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        // §16B: plan은 READY/ENTERED에서만 존재한다. 거절·무효화된 후보에 Stop/Target=0을 넣지 않는다(§11).
        var plan = candidate.Plan;
        return new StructureCandidateDto(candidate.EventId, candidate.KindName, candidate.ZoneId,
            candidate.TriggerBarStart, candidate.TriggerConfirmedAt, candidate.StructureCutoff,
            candidate.ExpiresAt, candidate.Disposition.ToString().ToUpperInvariant(),
            Finite(candidate.EntryQuality), candidate.EntryReference, candidate.InvalidationAnchor,
            plan?.Stop, plan?.Target, plan?.NetR, Plan(plan),
            candidate.Quality.Components
                .Select(x => new StructureQualityComponentDto(x.Name, Finite(x.Raw), Finite(x.Value), x.Required))
                .ToArray(),
            candidate.RejectionCodes.ToArray(), candidate.Notes.ToArray(), candidate.CounterTrend,
            candidate.RetestConfirmed);
    }

    public static StructureQualityDto Quality(DataQuality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        return new StructureQualityDto(
            quality.Sources.Select(x => new StructureSourceQualityDto(x.Source,
                x.Status.ToString().ToLowerInvariant(), x.Count, x.ExpectedCount, x.First, x.Last,
                x.Gaps.ToArray(), x.Conflicts.ToArray(), Finite(x.CoverageRatio), x.Warnings.ToArray())).ToArray(),
            quality.Warnings.ToArray(), quality.BlockersForTrend.ToArray(), quality.BlockersForZone.ToArray(),
            quality.BlockersForCandidate.ToArray(), quality.BlockersForReady.ToArray());
    }
}
