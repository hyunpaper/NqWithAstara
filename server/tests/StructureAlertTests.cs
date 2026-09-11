using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #26 — v5 알림 이벤트 계약(승인 설계안 §4·§6 BE 테스트 계획).
/// 발행 주체는 StructureAnalysisService gate 안 commit 지점이고, dedup·영속·공개는 StructureAlertPublisher가
/// 소유한다. 이벤트는 표시·소리용일 뿐 거래를 만들지 않으며(simtrades 바이트 동일), off/shadow는 발행하지 않는다.
/// D6 결정적 합성 데이터를 재사용한다 — 임의 값이며 실제 종목 추천이 아니다.
/// </summary>
public sealed class StructureAlertTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    sealed record Harness(MonitorPollingService Poller, RecordingStore Store, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, StructureAnalysisService Structure, StructureAlertPublisher Alerts,
        MovableClock Clock, SilentDiagnostics Diagnostics);

    static Harness Build(StructureEngineMode mode, RecordingStore? store = null,
        MemoryObservationStore? observations = null, IStructuralTradeEntries? entries = null,
        bool withEntryPort = true, bool withAlerts = true)
    {
        var clock = new MovableClock(Fx.At(60));
        var recording = store ?? new RecordingStore();
        if (recording.Watch.Count == 0) recording.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var obs = observations ?? new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var alerts = new StructureAlertPublisher(obs);
        var port = entries ?? (withEntryPort ? new StructuralTradeEntryService(recording) : null);
        var structure = new StructureAnalysisService(recording, new StructureObservationWriter(obs, P), runtime,
            clock, diagnostics, new StructureEngineOptions(mode), P, port, withAlerts ? alerts : null);
        var gateway = new ScriptedGateway(D6.Session, () => D6.CompletedBars(clock.Now),
            () => (D6.QuotePrice(clock.Now), clock.Now), () => D6.Daily());
        var poller = new MonitorPollingService(recording, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure);
        runtime.CommitStart();
        return new Harness(poller, recording, obs, runtime, structure, alerts, clock, diagnostics);
    }

    static async Task PollAt(Harness harness, int minute, int seconds = 0)
    {
        harness.Clock.Now = Fx.At(minute).AddSeconds(seconds);
        await harness.Poller.PollAsync(default);
    }

    static Task<System.Collections.Immutable.ImmutableArray<StructureAlertEvent>> Recent(Harness harness) =>
        harness.Alerts.GetRecentAsync(Fx.SessionStart, default);

    static string Json(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    static StateQueryService State(Harness harness) => new(harness.Store, harness.Poller, new QuietStream(),
        harness.Runtime, harness.Clock, harness.Structure, harness.Alerts);

    /// <summary>진입 완료(READY→ENTERED) 상태까지 진행한 active harness. D6 fixture: m64 트리거 → REBOUND READY.</summary>
    static async Task<Harness> EnteredActive()
    {
        var harness = Build(StructureEngineMode.Active);
        await PollAt(harness, 64);                       // watermark seeding — 신규 트리거 없음(§16B)
        await PollAt(harness, 65);                       // 트리거 봉(m64) 평가 → REBOUND READY → 진입
        Assert.Single(harness.Store.Trades);
        return harness;
    }

    // ── 발행 (§6-1·2) ───────────────────────────────────────────────────────

    /// <summary>READY 첫 커밋 → V5_READY 1건, 같은 poll의 진입 커밋 → V5_ENTERED 1건. payload는 계획 값 그대로다.</summary>
    [Fact]
    public async Task ActiveReadyAndEntryPublishExactlyOneEventEach()
    {
        var harness = await EnteredActive();

        var events = await Recent(harness);
        Assert.Equal(2, events.Length);
        var ready = events[0];
        var entered = events[1];
        Assert.Equal(StructureAlertPublisher.TypeReady, ready.Type);
        Assert.Equal(StructureAlertPublisher.TypeEntered, entered.Type);
        Assert.Equal(1, ready.Seq);
        Assert.Equal(2, entered.Seq);
        Assert.Equal(ready.EventId, entered.EventId);
        Assert.Equal(Fx.Symbol, ready.Symbol);
        Assert.Equal("REBOUND", ready.Kind);
        Assert.NotNull(ready.EntryQuality);
        Assert.NotNull(ready.NetR);
        Assert.Null(ready.Reason);
        Assert.Null(ready.PlanId);                                       // planId/stop/target은 ENTERED 전용
        Assert.Equal(P.PolicyHash, ready.PolicyHash);
        Assert.Equal(Fx.SessionStart, ready.SessionStart);

        // ENTERED payload는 저장된 거래(FrozenPlan)와 같은 계획에서 온다.
        var trade = harness.Store.Trades[0];
        Assert.Equal(trade.Structure!.EntryEventId, entered.EventId);
        Assert.Equal(trade.Structure.PlanSnapshot.PlanId, entered.PlanId);
        Assert.Equal((double)entered.Stop!.Value, trade.Stop, 10);
        Assert.Equal((double)entered.Target!.Value, trade.Target, 10);
    }

    /// <summary>같은 EventId의 후속 poll(캐시 재평가·전체 재계산)에서 중복 발행이 없고 이벤트가 거래를 만들지 않는다.</summary>
    [Fact]
    public async Task RepeatPollsNeverDuplicateEventsAndEventsNeverCreateTrades()
    {
        // 같은 결정적 입력을 알림 발행자 없이 돌린 기준 harness — 거래·포지션이 바이트 동일해야 한다(설계 §5-③).
        var baseline = Build(StructureEngineMode.Active, withAlerts: false);
        var harness = Build(StructureEngineMode.Active);
        foreach (var (minute, seconds) in new[] { (64, 0), (65, 0), (65, 15), (66, 0), (66, 30) })
        {
            await PollAt(baseline, minute, seconds);
            await PollAt(harness, minute, seconds);
        }

        var events = await Recent(harness);
        Assert.Equal(2, events.Length);                                  // m64 episode의 READY+ENTERED만 남는다
        Assert.Equal(StructureAlertPublisher.TypeReady, events[0].Type);
        Assert.Equal(StructureAlertPublisher.TypeEntered, events[1].Type);

        // 알림 발행 유무와 무관하게 실제 거래 저장은 바이트 동일 — 이벤트는 거래를 만들지도 바꾸지도 않는다.
        Assert.Equal(Json(baseline.Store.Trades), Json(harness.Store.Trades));
        Assert.Equal(Json(baseline.Store.Positions), Json(harness.Store.Positions));
        Assert.Equal(baseline.Store.Writes, harness.Store.Writes);
        Assert.Single(harness.Store.Trades);
        Assert.Empty(harness.Diagnostics.Failures);
    }

    /// <summary>차단 3종 중 포트 부재·계획 무효 — 각 1회 발행되고 reason이 구분되며 거래는 만들지 않는다.</summary>
    [Theory]
    [InlineData(false, null, StructureAnalysisService.NoteEntryUnavailable)]
    [InlineData(true, StructuralEntryOutcome.InvalidPlan, StructureAnalysisService.NoteEntryPlanInvalid)]
    public async Task PortUnavailableAndInvalidPlanPublishBlockedWithTheirReason(bool withPort,
        StructuralEntryOutcome? scripted, string expectedReason)
    {
        var harness = Build(StructureEngineMode.Active,
            entries: scripted is { } outcome ? new ScriptedEntryPort(outcome) : null, withEntryPort: withPort);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        var events = await Recent(harness);
        Assert.Equal(2, events.Length);
        Assert.Equal(StructureAlertPublisher.TypeReady, events[0].Type);
        var blocked = events[1];
        Assert.Equal(StructureAlertPublisher.TypeBlocked, blocked.Type);
        Assert.Equal(expectedReason, blocked.Reason);
        Assert.Equal(events[0].EventId, blocked.EventId);
        Assert.Empty(harness.Store.Trades);

        await PollAt(harness, 65, seconds: 15);                          // 반복 poll에 중복 없음
        Assert.Equal(2, (await Recent(harness)).Length);
    }

    // ── 재시작·원자성 (§6-4) ─────────────────────────────────────────────────

    /// <summary>알림 영속 실패 → 이벤트 미발행(래치 미커밋), 다음 poll이 멱등 재시도해 정확히 1회 발행·거래 1개.</summary>
    [Fact]
    public async Task AFailedAlertPersistWithholdsTheEventAndTheNextPollRecoversExactlyOnce()
    {
        var harness = Build(StructureEngineMode.Active);
        await PollAt(harness, 64);
        harness.Observations.TextWriteFailure = file =>
            file == StructureAlertPublisher.AlertsFile ? new IOException("디스크 오류") : null;

        await PollAt(harness, 65);                                       // 거래는 저장, 알림 영속은 실패
        Assert.Single(harness.Store.Trades);
        Assert.Empty(await Recent(harness));
        Assert.Contains(harness.Diagnostics.Failures, x => x.Scope == "structure-v5");

        harness.Observations.TextWriteFailure = null;
        await PollAt(harness, 65, seconds: 15);                          // AlreadyEntered 멱등 회복 → 1회 발행
        var events = await Recent(harness);
        Assert.Equal(2, events.Length);
        Assert.Single(events, x => x.Type == StructureAlertPublisher.TypeEntered);
        Assert.Single(harness.Store.Trades);
    }

    /// <summary>재시작 복원 이벤트는 신규가 아니다 — seq 그대로 복원되고 재발행 0건, 이후 seq는 단조 증가를 잇는다.</summary>
    [Fact]
    public async Task RestartRestoresEventsWithoutReissuingAndKeepsSeqMonotonic()
    {
        var harness = await EnteredActive();
        var before = await Recent(harness);
        Assert.Equal(2, before.Length);

        // 재시작: 같은 저장소(거래·관측·래치·알림 파일)를 공유하는 새 인스턴스.
        var restarted = Build(StructureEngineMode.Active, harness.Store, harness.Observations);
        await PollAt(restarted, 65, seconds: 30);                        // watermark: 이미 평가한 봉 → 신규 트리거 없음

        var restored = await Recent(restarted);
        Assert.Equal(before.Select(Json), restored.Select(Json));        // 같은 seq·같은 이벤트, 재발행 없음
        Assert.Single(restarted.Store.Trades);

        await PollAt(restarted, 66);                                     // 새 트리거는 복원 최대 seq 다음부터
        var after = await Recent(restarted);
        Assert.Equal(2, after.Length);
    }

    /// <summary>관측 append 도중 stop되면(§12.6 generation 재검증) 늦은 알림 이벤트를 만들지 않는다.</summary>
    [Fact]
    public async Task AStopDuringTheCommitPublishesNoLateEvent()
    {
        var harness = Build(StructureEngineMode.Active);
        await PollAt(harness, 64);
        harness.Observations.BeforeAppend = () => harness.Runtime.CommitStop();

        await PollAt(harness, 65);

        Assert.Empty(await Recent(harness));
        Assert.DoesNotContain(StructureAlertPublisher.AlertsFile, harness.Observations.Texts.Keys);
    }

    // ── off/shadow (§6-5) ────────────────────────────────────────────────────

    /// <summary>shadow는 READY를 관측해도 v5 이벤트를 발행하지 않고 알림 파일도 만들지 않는다(§16B).</summary>
    [Fact]
    public async Task ShadowObservesReadyButPublishesNothing()
    {
        var harness = Build(StructureEngineMode.Shadow);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.Contains(view.Candidates, x => x.State == "READY");       // 관측은 그대로다
        Assert.Empty(await Recent(harness));
        Assert.DoesNotContain(StructureAlertPublisher.AlertsFile, harness.Observations.Texts.Keys);

        using var document = JsonDocument.Parse(Json(await State(harness).GetAsync()));
        Assert.Equal(0, document.RootElement.GetProperty("structureEvents").GetArrayLength());
    }

    /// <summary>off는 v5 저장소를 아예 건드리지 않고 structureEvents도 빈 배열이다.</summary>
    [Fact]
    public async Task OffModePublishesNothingAndTouchesNoAlertStorage()
    {
        var harness = Build(StructureEngineMode.Off);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        using var document = JsonDocument.Parse(Json(await State(harness).GetAsync()));
        Assert.Equal(0, document.RootElement.GetProperty("structureEvents").GetArrayLength());
        Assert.Equal(0, harness.Observations.Interactions);              // off 무동작 불변식 유지
        Assert.Empty(harness.Store.Trades);
    }

    // ── /api/state 계약 (§6-6) ──────────────────────────────────────────────

    /// <summary>structureEvents는 additive이며 기존 필드·structureSummary를 바꾸지 않는다. seq는 오름차순이다.</summary>
    [Fact]
    public async Task StateExposesAdditiveStructureEventsWithMonotonicSeq()
    {
        var harness = await EnteredActive();
        using var document = JsonDocument.Parse(Json(await State(harness).GetAsync()));
        var root = document.RootElement;

        foreach (var field in new[]
                 { "running", "connection", "transport", "market", "updatedAt", "watchlist", "signals", "events", "structureSummary" })
            Assert.True(root.TryGetProperty(field, out _), field);

        var events = root.GetProperty("structureEvents").EnumerateArray().ToArray();
        Assert.Equal(2, events.Length);
        var seqs = events.Select(x => x.GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal(seqs.OrderBy(x => x).ToArray(), seqs);
        foreach (var evt in events)
        {
            foreach (var field in new[]
                     { "seq", "type", "symbol", "eventId", "kind", "entryQuality", "netR", "quotePrice", "at", "planId", "stop", "target", "reason" })
                Assert.True(evt.TryGetProperty(field, out _), field);
            // v4 점수·확률 표현은 이벤트 계약에 없다(§29 오인 방지).
            Assert.False(evt.TryGetProperty("score", out _));
            Assert.False(evt.TryGetProperty("action", out _));
        }

        // 최근 50건 상한: publisher가 51번째 이벤트를 받으면 가장 오래된 것부터 잘라 공개한다.
        var alerts = new StructureAlertPublisher(new MemoryObservationStore());
        for (var i = 0; i < StructureAlertPublisher.RecentLimit + 5; i++)
            await alerts.PublishAsync(Fx.Symbol, Fx.SessionStart, P.PolicyHash,
                [new StructureAlertDraft(StructureAlertPublisher.TypeReady, $"TEST|event-{i:000}", "PULLBACK", 50.0, 1.2m)],
                100m, Fx.At(60), default);
        var recent = await alerts.GetRecentAsync(Fx.SessionStart, default);
        Assert.Equal(StructureAlertPublisher.RecentLimit, recent.Length);
        Assert.Equal(6, recent[0].Seq);                                  // 55건 중 마지막 50건
        Assert.Equal(55, recent[^1].Seq);
    }

    /// <summary>세션/정책이 바뀌면 지난 키·이벤트는 보관하지 않되 seq는 리셋하지 않는다.</summary>
    [Fact]
    public async Task ASessionChangeClearsOldKeysButNeverResetsSeq()
    {
        var store = new MemoryObservationStore();
        var alerts = new StructureAlertPublisher(store);
        var draft = new StructureAlertDraft(StructureAlertPublisher.TypeReady, "TEST|event-A", "PULLBACK", 50.0, 1.2m);
        await alerts.PublishAsync(Fx.Symbol, Fx.SessionStart, P.PolicyHash, [draft], 100m, Fx.At(60), default);

        // 다음 세션: 같은 EventId라도 세션이 다르면 새 키다. 이전 세션 이벤트는 목록에서 내려간다.
        var nextSession = Fx.SessionStart.AddDays(1);
        await alerts.PublishAsync(Fx.Symbol, nextSession, P.PolicyHash, [draft], 100m, Fx.At(60).AddDays(1), default);

        var recent = await alerts.GetRecentAsync(nextSession, default);
        var evt = Assert.Single(recent);
        Assert.Equal(2, evt.Seq);                                        // seq는 이어진다
        Assert.Equal(nextSession, evt.SessionStart);
    }

    // ── 정책 무변경 가드 (§5-②) ──────────────────────────────────────────────

    /// <summary>알림 발행자는 v4 진입점·점수 경로·실제 거래 저장소를 참조조차 하지 않는다(정적 불변식).</summary>
    [Fact]
    public void TheAlertPublisherNeverReferencesTheV4OrTradePaths()
    {
        var file = FindServerFile(Path.Combine("Application", "StructureAlertPublisher.cs"));
        var text = File.ReadAllText(file);
        foreach (var forbidden in new[]
                 {
                     "MarketRules.Enter", "PriceLevels.Enter", "MarketRules.FreezeRisk", "PriceLevels.Compute",
                     "SimulationEngine", "SimulationEntry", "simtrades.json", "positions.json",
                     "Indicators.Evaluate", "tradeEntries", "ILocalStore"
                 })
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
    }

    static string FindServerFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Astra.Server.csproj")))
                return Path.Combine(directory.FullName, relative);
            if (File.Exists(Path.Combine(directory.FullName, "server", "Astra.Server.csproj")))
                return Path.Combine(directory.FullName, "server", relative);
        }
        Assert.Fail("Astra.Server.csproj 기준의 서버 루트를 찾지 못했다.");
        return null!;
    }
}

/// <summary>정해진 결과만 돌려주는 진입 포트 — 계획 무효 등 차단 사유별 발행을 결정적으로 검증한다.</summary>
sealed class ScriptedEntryPort(StructuralEntryOutcome outcome) : IStructuralTradeEntries
{
    public Task<StructuralEntryResult> TryEnterAsync(StructuralEntryRequest request, CancellationToken ct) =>
        Task.FromResult(new StructuralEntryResult([], outcome, null));
}
