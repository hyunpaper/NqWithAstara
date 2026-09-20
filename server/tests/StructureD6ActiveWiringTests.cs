using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// v5 구조 엔진 D6 — 설계 §18 active 배선의 Domain 규칙.
/// FrozenPlan에서만 거래 숫자가 나오고, 멱등·OPEN 제한·v4 CUT 격리·기존 청산 재사용을 고정한다.
/// </summary>
public sealed class StructureD6StructuralSimulationTests
{
    static readonly StructurePolicy P = D6.WiringPolicy;

    static StructuralTradePlan PlanA()
    {
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), P);
        Assert.True(evaluation.Viable);
        return evaluation.Plan!;
    }

    static FrozenStructureContext ContextA(StructuralTradePlan? plan = null) =>
        StructuralSimulation.Freeze(plan ?? PlanA(), "TEST|event-A", "UP", 41.0, 55.5, Fx.At(40), Fx.At(40));

    static StructuralEntryRequest RequestA(FrozenStructureContext? context = null) =>
        new(Fx.Symbol, Fx.At(39), Fx.At(40), Fx.SessionEnd, context ?? ContextA());

    static SimTrade V4Open(string symbol = Fx.Symbol, string kind = "SETUP") =>
        new("v4-open-1", symbol, kind, Fx.At(1), 100.0, 200.0, 1.0, "저항", "ATR", "OPEN", null, null, null, 100.0,
            Score: 80, Logic: "v4", SessionEnd: Fx.SessionEnd);

    /// <summary>§10/§11: 진입가·손절·목표는 전부 동결 계획에서 오고, Score에 EntryQuality를 끼워 넣지 않는다.</summary>
    [Fact]
    public void EnterCreatesTheTradeEntirelyFromTheFrozenPlan()
    {
        var plan = PlanA();
        var result = StructuralSimulation.Enter([], RequestA(ContextA(plan)));

        Assert.Equal(StructuralEntryOutcome.Entered, result.Outcome);
        var trade = Assert.Single(result.Trades);
        Assert.Same(result.Trade, trade);
        Assert.Equal("OPEN", trade.Status);
        Assert.Equal(plan.Kind, trade.Kind);
        Assert.Equal((double)plan.EntryReference, trade.EntryPrice, 10);
        Assert.Equal((double)plan.Stop, trade.Stop, 10);
        Assert.Equal((double)plan.Target, trade.Target, 10);
        Assert.Equal(plan.EngineVersion, trade.Logic);                    // §11 "신규 활성 v5 거래는 v5-structure.1"
        Assert.Null(trade.Score);                                         // §11 Score 의미 보존
        Assert.Null(trade.ExtSigma); Assert.Null(trade.Rsi); Assert.Null(trade.BuyShare);
        Assert.Equal(Fx.At(39), trade.TriggerBarAt);
        Assert.Equal(Fx.At(40), trade.EnteredAt);
        Assert.Equal(Fx.SessionEnd, trade.SessionEnd);
        var execution = Assert.IsType<ExecutionProvenance>(trade.Execution);
        Assert.Equal(Fx.At(40), execution.EntryBarStart);
        Assert.Equal(Fx.At(41), execution.EntryBarCloseAt);
        Assert.Equal("UNOBSERVED", execution.EntryMinuteCoverage);
        Assert.NotNull(trade.Structure);
        Assert.Equal("TEST|event-A", trade.Structure!.EntryEventId);
        Assert.Equal(plan.PlanId, trade.Structure.PlanSnapshot.PlanId);
        Assert.Equal(plan.PolicyHash, trade.Structure.PlanSnapshot.PolicyHash);
        Assert.Equal(StructuralSimulation.ExitPolicyVersion, trade.Structure.StructuralExitPolicyVersion);
        Assert.Equal("UP", trade.Structure.TrendAtEntry);
        Assert.Equal(55.5, trade.Structure.EntryQualityAtEntry);
        Assert.True(StructuralSimulation.OwnsTrade(trade));
        Assert.Equal([plan.HumanExplanation], trade.Reasons ?? []);
    }

    [Fact]
    public void ConfirmedFillOverridesPlannedReferenceAndKeepsObservationProvenance()
    {
        var plan = PlanA();
        var fill = (double)plan.EntryReference + 0.05;
        var pending = new PendingEntry("TEST|event-fill", Fx.Symbol, TradeSide.Long,
            Fx.At(39), Fx.At(40), Fx.At(42), (double)plan.Stop, (double)plan.Target,
            (double)plan.EntryReference, plan.PlanId, plan.PolicyHash);
        var confirmation = new EntryConfirmation(pending, PendingEntryDecision.Confirmed,
            Fx.At(41), fill, 0.01, "REPLAY_CONFIRMATION_BAR_CLOSE", "OBSERVED");

        var result = StructuralSimulation.Enter([], new StructuralEntryRequest(
            Fx.Symbol, Fx.At(39), Fx.At(41), Fx.SessionEnd, ContextA(plan),
            [Fx.At(39), Fx.At(40), Fx.At(41)], Fx.At(0), null, confirmation));

        var trade = Assert.Single(result.Trades);
        Assert.Equal(fill, trade.EntryPrice, 10);
        Assert.Equal("OBSERVED_CONFIRMATION_BAR", trade.Execution!.EntryMinuteCoverage);
        Assert.Equal(Fx.At(41), trade.Execution.EntryMinuteEvidenceAt);
        Assert.Equal((double)plan.Stop, trade.Stop, 10);
        var closed = Assert.Single(SimulationEngine.ReplayBars(result.Trades, Fx.Symbol,
            [new Candle(Fx.At(42), fill, (double)plan.Target + 0.01, fill, fill, 100)]));
        Assert.Equal("TARGET", closed.Status);
        Assert.Equal(Math.Round(((double)plan.Target / fill - 1) * 100 - MarketRules.RoundTripFeePercent, 2), closed.PnlPercent);
    }

    /// <summary>§10 "트리거 1개로 여러 번 진입하지 않는다" — 같은 EntryEventId는 재시도돼도 거래가 1개다.</summary>
    [Fact]
    public void TheSameEntryEventIdIsIdempotent()
    {
        var first = StructuralSimulation.Enter([], RequestA());
        var second = StructuralSimulation.Enter(first.Trades, RequestA());

        Assert.Equal(StructuralEntryOutcome.AlreadyEntered, second.Outcome);
        Assert.Single(second.Trades);
        Assert.Equal(first.Trade!.Id, second.Trade!.Id);
        // 결정적 ID: 재시작 후 재시도가 다른 거래처럼 보이지 않는다.
        Assert.Equal(first.Trade.Id, StructuralSimulation.Enter([], RequestA()).Trade!.Id);
    }

    /// <summary>§18 "한 종목 OPEN 하나 제한은 버전 공통이다" — v4 OPEN이 있으면 v5도 진입하지 않는다.</summary>
    [Fact]
    public void AnyOpenTradeOnTheSymbolBlocksAStructuralEntry()
    {
        var blocked = StructuralSimulation.Enter([V4Open()], RequestA());
        Assert.Equal(StructuralEntryOutcome.BlockedByOpenTrade, blocked.Outcome);
        Assert.Single(blocked.Trades);
        Assert.Null(blocked.Trade);

        // 다른 종목의 OPEN이나 이 종목의 CLOSED 기록은 막지 않는다.
        var closed = V4Open() with { Id = "v4-done", Status = "TARGET" };
        var other = V4Open("OTHER") with { Id = "v4-other" };
        var allowed = StructuralSimulation.Enter([closed, other], RequestA());
        Assert.Equal(StructuralEntryOutcome.Entered, allowed.Outcome);
        Assert.Equal(3, allowed.Trades.Count);
    }

    /// <summary>동결 계획의 가격 순서가 깨져 있으면 어떤 거래도 만들지 않는다(§19-5 폴백 금지 방향).</summary>
    [Fact]
    public void ABrokenFrozenPlanNeverBecomesATrade()
    {
        var plan = PlanA();
        var inverted = ContextA(plan) with
        {
            PlanSnapshot = StructuralSimulation.Freeze(plan, "TEST|event-A", "UP", null, null, Fx.At(40), null)
                .PlanSnapshot with { Stop = plan.EntryReference + 1m }
        };
        var result = StructuralSimulation.Enter([], RequestA(inverted));
        Assert.Equal(StructuralEntryOutcome.InvalidPlan, result.Outcome);
        Assert.Empty(result.Trades);
    }

    /// <summary>§10 "v5에 v4 score&lt;40 CUT을 적용하지 않는다". 같은 조건의 v4 거래는 여전히 CUT된다.</summary>
    [Fact]
    public void TheV4ScoreCutNeverTouchesAV5Trade()
    {
        var v5 = StructuralSimulation.Enter([], RequestA()).Trade!;            // PULLBACK, stop 99.12 / target 101.78
        var v4 = v5 with { Id = "v4-twin", Logic = "v4", Structure = null, Kind = "SETUP", Score = 35 };
        var bar = Fx.Candle(41, 100.10, 100.20, 100.00, 100.05);               // close 100.05 < vwap
        var quote = 100.05;                                                    // stop과 target 사이

        var v5After = SimulationEngine.Process([v5], Fx.Symbol, [bar], quote, Fx.At(42), score: 10, vwap: 100.50, []);
        Assert.Equal("OPEN", Assert.Single(v5After).Status);

        var v4After = SimulationEngine.Process([v4], Fx.Symbol, [bar], quote, Fx.At(42), score: 10, vwap: 100.50, []);
        Assert.Equal("CUT", Assert.Single(v4After).Status);
    }

    /// <summary>
    /// §10 "v5 청산은 기존의 시간순 봉 replay·gap stop·same-bar stop-first·EOD 복구를 재사용한다".
    /// 새 청산 규칙을 만들지 않고 동결된 Stop/Target 그대로 기존 엔진이 관리한다.
    /// </summary>
    [Fact]
    public void V5TradesExitThroughTheExistingReplayAndEodPaths()
    {
        var open = StructuralSimulation.Enter([], RequestA()).Trade!;
        var stop = open.Stop;

        // 봉 저가가 손절에 닿으면 손절가로 청산된다.
        var stopHit = SimulationEngine.ReplayBars([open], Fx.Symbol,
            [Fx.Candle(41, 99.60, 99.70, stop - .01, 99.65)]);
        Assert.Equal("STOP", Assert.Single(stopHit).Status);
        Assert.Equal(stop, stopHit[0].ExitPrice!.Value, 10);

        // 손절 아래 갭 시가는 시가 체결로 본다(낙관 금지).
        var gap = SimulationEngine.ReplayBars([open], Fx.Symbol,
            [Fx.Candle(41, stop - .50, stop - .40, stop - .60, stop - .45)]);
        Assert.Equal("STOP", Assert.Single(gap).Status);
        Assert.Equal(stop - .50, gap[0].ExitPrice!.Value, 10);

        // 목표 도달은 목표가 체결.
        var target = SimulationEngine.ReplayBars([open], Fx.Symbol,
            [Fx.Candle(41, 101.00, open.Target + .10, 100.90, open.Target)]);
        Assert.Equal("TARGET", Assert.Single(target).Status);
        Assert.Equal(open.Target, target[0].ExitPrice!.Value, 10);

        // 세션이 끝나면 EOD 복구.
        var eod = SimulationEngine.CloseExpiredSessions([open], Fx.SessionEnd.AddMinutes(1));
        Assert.Equal("EOD", Assert.Single(eod).Status);
    }

    /// <summary>§18: v5 OPEN이 남아 있는 동안 v4 재진입도 같은 종목에서 막힌다(기존 엔진의 OPEN 제한 재사용).</summary>
    [Fact]
    public void AV5OpenTradeBlocksV4ReEntryThroughTheSharedEngine()
    {
        var open = StructuralSimulation.Enter([], RequestA()).Trade!;
        var entry = new SimulationEntry("SETUP", 100.0, 101.0, 99.5, null, null, 80, 0, 1, null, 50, [],
            Fx.At(42), Fx.SessionEnd, Fx.At(41));
        var after = SimulationEngine.Process([open], Fx.Symbol,
            [Fx.Candle(41, 100.00, 100.10, 99.90, 100.05)], 100.05, Fx.At(42), 80, 99.0, [entry]);
        Assert.Single(after);
        Assert.Equal(open.Id, after[0].Id);
    }

    /// <summary>동결 컨텍스트는 기존 저장 파일(simtrades.json) 직렬화를 그대로 왕복해야 한다(재시작 복원).</summary>
    [Fact]
    public void TheFrozenContextSurvivesAJsonRoundTrip()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var trades = StructuralSimulation.Enter([V4Open("OTHER") with { Id = "v4-old" }], RequestA()).Trades;

        var restored = JsonSerializer.Deserialize<List<SimTrade>>(JsonSerializer.Serialize(trades, json), json)!;

        Assert.Equal(2, restored.Count);
        var v4 = restored.Single(x => x.Id == "v4-old");
        Assert.Null(v4.Structure);
        Assert.False(StructuralSimulation.OwnsTrade(v4));
        var v5 = restored.Single(x => x.Id != "v4-old");
        Assert.True(StructuralSimulation.OwnsTrade(v5));
        Assert.Equal(JsonSerializer.Serialize(trades.Single(x => x.Id != "v4-old").Structure, json),
            JsonSerializer.Serialize(v5.Structure, json));
        // 재시작 후에도 같은 EventId는 다시 진입하지 않는다(§10 "프로세스 재시작 후에도 조회한다").
        Assert.Equal(StructuralEntryOutcome.AlreadyEntered,
            StructuralSimulation.Enter(restored, RequestA()).Outcome);
    }
}

/// <summary>
/// v5 구조 엔진 D6 — 설계 §18 active 배선의 통합 경로. 실제 파이프라인(집계→Zone→추세→후보→계획)이
/// READY를 만들면 폴링→관측→진입 커밋→래치가 이어지는 것을 결정적 합성 데이터로 검증한다.
/// 임의 값이며 실제 종목 추천이 아니다.
/// </summary>
public sealed class StructureD6ActiveWiringTests
{
    static readonly StructurePolicy P = D6.WiringPolicy;

    sealed record Harness(MonitorPollingService Poller, RecordingStore Store, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, StructureAnalysisService Structure, MovableClock Clock,
        CountingEntryPort Entries, SilentDiagnostics Diagnostics);

    static Harness Build(StructureEngineMode mode, RecordingStore? store = null,
        MemoryObservationStore? observations = null, StructurePolicy? policy = null)
    {
        var selectedPolicy = policy ?? P;
        var defaultFixture = ReferenceEquals(selectedPolicy, StructurePolicy.Default);
        var clock = new MovableClock(Fx.At(60));
        var recording = store ?? new RecordingStore();
        if (recording.Watch.Count == 0) recording.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var obs = observations ?? new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var entries = new CountingEntryPort(new StructuralTradeEntryService(recording));
        var structure = new StructureAnalysisService(recording, new StructureObservationWriter(obs, selectedPolicy), runtime,
            clock, diagnostics, new StructureEngineOptions(mode), selectedPolicy, entries);
        var gateway = new ScriptedGateway(D6.Session, () => defaultFixture ? DefaultBars(clock.Now) : D6.CompletedBars(clock.Now),
            () => (defaultFixture ? DefaultPrice(clock.Now) : D6.QuotePrice(clock.Now), clock.Now), () => defaultFixture ? DefaultDaily() : D6.Daily());
        var poller = new MonitorPollingService(recording, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure);
        runtime.CommitStart();
        return new Harness(poller, recording, obs, runtime, structure, clock, entries, diagnostics);
    }

    static Candle[] DefaultBars(DateTimeOffset now) => D6.CompletedBars(now).Select(Compress).ToArray();
    static double DefaultPrice(DateTimeOffset now) => 99.90 + (D6.QuotePrice(now) - 99.90) * 0.35;
    static Candle[] DefaultDaily() => D6.Daily().Select(Compress).ToArray();
    static Candle Compress(Candle c) => c with
    {
        Open = 99.90 + (c.Open - 99.90) * 0.35,
        High = 99.90 + (c.High - 99.90) * 0.35,
        Low = 99.90 + (c.Low - 99.90) * 0.35,
        Close = 99.90 + (c.Close - 99.90) * 0.35,
    };

    static async Task PollAt(Harness harness, int minute, int seconds = 0)
    {
        harness.Clock.Now = Fx.At(minute).AddSeconds(seconds);
        await harness.Poller.PollAsync(default);
    }

    static string Describe(StructureAnalysisView view) =>
        $"summary={view.CandidateSummary} notes=[{string.Join(",", view.Notes)}] warnings=[{string.Join(",", view.Warnings)}] " +
        $"readyBlockers=[{string.Join(",", view.Quality.BlockersForReady)}] candidates=[" +
        string.Join(" | ", view.Candidates.Select(x =>
            $"{x.Kind}:{x.State}:rej[{string.Join(",", x.RejectionCodes)}]:notes[{string.Join(",", x.Notes)}]")) + "]";

    static async Task<(Harness Harness, SimTrade Trade, StructureAnalysisView View)> EnteredActive()
    {
        var harness = Build(StructureEngineMode.Active);
        await PollAt(harness, 64);                       // watermark seeding — 이 봉으로는 신규 트리거 없음(§16B)
        await PollAt(harness, 65);                       // 트리거 봉(m64) 평가 → REBOUND READY → 진입
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.True(harness.Store.Trades.Count == 1, "진입 없음 → " + Describe(view));
        return (harness, harness.Store.Trades[0], view);
    }

    [Fact]
    public async Task DefaultPolicyLivePollingRecordsTheSamePolicyRejectionBoundary()
    {
        var harness = Build(StructureEngineMode.Active, policy: StructurePolicy.Default);
        await PollAt(harness, 64);
        await PollAt(harness, 65);
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.Empty(harness.Store.Trades);
        Assert.Equal(StructurePolicy.Default.PolicyHash, view.PolicyHash);
        Assert.Equal("REJECTED", view.CandidateSummary);
        var candidate = Assert.Single(view.Candidates);
        Assert.Contains("TREND_DEEPLY_OPPOSES_REBOUND", candidate.RejectionCodes);
        Assert.Contains("STOP_INSIDE_COST", candidate.RejectionCodes);
    }

    /// <summary>
    /// §18 active: 실제 파이프라인이 만든 READY 계획(REBOUND)이 그대로 시뮬 거래가 된다.
    /// 진입가·손절·목표가 공개된 계획과 일치하고 FrozenPlan이 저장되며 v4 진입은 만들어지지 않는다.
    /// </summary>
    [Fact]
    public async Task ActiveModeCommitsTheReadyStructuralPlanAsTheOnlyNewTrade()
    {
        var (harness, trade, view) = await EnteredActive();

        Assert.Equal("active", view.Mode);
        Assert.Equal(StructureAnalysisService.EntryOwnerV5, view.EntryOwner);
        Assert.Equal("ENTERED", view.CandidateSummary);
        Assert.Contains(StructureAnalysisService.NoteEntryCommitted, view.Notes);

        var candidate = view.Candidates.SingleOrDefault(x => x.State == "ENTERED");
        Assert.True(candidate is not null, Describe(view));
        Assert.Equal("REBOUND", candidate!.Kind);
        Assert.NotNull(candidate.Plan);                                  // §16B: plan은 READY/ENTERED에서 존재

        Assert.Equal("OPEN", trade.Status);
        Assert.Equal("REBOUND", trade.Kind);
        Assert.Equal(P.Version, trade.Logic);
        Assert.Null(trade.Score);
        Assert.Equal((double)candidate.Plan!.EntryReference, trade.EntryPrice, 10);
        Assert.Equal((double)candidate.Plan.Stop, trade.Stop, 10);
        Assert.Equal((double)candidate.Plan.Target, trade.Target, 10);
        Assert.NotNull(trade.Structure);
        Assert.Equal(candidate.EventId, trade.Structure!.EntryEventId);
        Assert.Equal(candidate.Plan.PlanId, trade.Structure.PlanSnapshot.PlanId);
        Assert.Equal(view.PolicyHash, trade.Structure.PlanSnapshot.PolicyHash);
        Assert.Equal(StructuralSimulation.ExitPolicyVersion, trade.Structure.StructuralExitPolicyVersion);

        // 신규 진입은 v5 소유뿐이다 — v4 SimulationEntry는 생성되지 않았다(§18).
        Assert.All(harness.Store.Trades, x => Assert.True(StructuralSimulation.OwnsTrade(x)));
        Assert.Equal(1, harness.Entries.Calls);
        Assert.Empty(harness.Diagnostics.Failures);
    }

    /// <summary>같은 데이터의 shadow는 READY까지만 만들고 진입 포트를 호출하지 않는다(§18 shadow 무쓰기).</summary>
    [Fact]
    public async Task ShadowSeesTheSameReadyPlanButNeverEnters()
    {
        var harness = Build(StructureEngineMode.Shadow);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        var ready = view.Candidates.SingleOrDefault(x => x.State == "READY");
        Assert.True(ready is not null, Describe(view));
        Assert.NotNull(ready!.Plan);
        Assert.Equal(StructureAnalysisService.EntryOwnerV4, view.EntryOwner);
        Assert.Equal(0, harness.Entries.Calls);
        Assert.Empty(harness.Store.Trades);
    }

    /// <summary>
    /// 재평가(같은 봉)·다음 트리거(OPEN 제한)·프로세스 재시작 어느 경로로도 두 번째 거래가 생기지 않는다
    /// (§10 되살리기 금지, §16B 재시작, §18 OPEN 제한).
    /// </summary>
    [Fact]
    public async Task OneTriggerNeverEntersTwiceAcrossPollsAndRestarts()
    {
        var (harness, trade, _) = await EnteredActive();

        await PollAt(harness, 65, seconds: 15);                    // 같은 봉 재평가(캐시 경로)
        Assert.Single(harness.Store.Trades);

        await PollAt(harness, 66);                                 // 같은 episode의 새 트리거는 소비 표식에 막힌다
        Assert.Single(harness.Store.Trades);
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var afterNext));
        Assert.Contains(afterNext.Candidates, x => x.RejectionCodes.Contains(StructuralLifecycle.CodeEpisodeConsumed));

        // 재시작: 같은 저장소(거래·관측·래치)를 공유하는 새 인스턴스가 같은 결론을 낸다.
        var restarted = Build(StructureEngineMode.Active, harness.Store, harness.Observations);
        await PollAt(restarted, 66, seconds: 30);
        Assert.Single(restarted.Store.Trades);
        Assert.Equal(trade.Id, restarted.Store.Trades[0].Id);
    }

    /// <summary>
    /// §18 rollback: active→shadow(또는 off)로 되돌려도 v5 OPEN은 동결된 계획의 Stop/Target/EOD대로
    /// 기존 청산 경로가 계속 관리하고, 설정 변경이 버전 태그·목표·손절·동결 컨텍스트를 바꾸지 않는다.
    /// </summary>
    [Fact]
    public async Task RollbackToShadowKeepsManagingTheV5OpenTradeByItsFrozenPlan()
    {
        var (active, entered, _) = await EnteredActive();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var frozenBefore = JsonSerializer.Serialize(entered.Structure, json);

        var shadow = Build(StructureEngineMode.Shadow, active.Store, new MemoryObservationStore());
        await PollAt(shadow, 67);                                  // m66 저가 99.30이 동결 손절 아래로 내려간다

        var trade = Assert.Single(shadow.Store.Trades, x => StructuralSimulation.OwnsTrade(x));
        Assert.Equal(entered.Id, trade.Id);
        Assert.Equal("STOP", trade.Status);
        Assert.Equal(entered.Stop, trade.ExitPrice!.Value, 10);    // 동결 손절 그대로 체결
        Assert.Equal(entered.Stop, trade.Stop, 10);                // rollback이 손절을 다시 쓰지 않는다
        Assert.Equal(entered.Target, trade.Target, 10);
        Assert.Equal(entered.Logic, trade.Logic);
        Assert.Equal(frozenBefore, JsonSerializer.Serialize(trade.Structure, json));
        Assert.Equal(0, shadow.Entries.Calls);                     // shadow는 신규 진입을 소유하지 않는다
    }

    /// <summary>
    /// §18 "기존 v4 OPEN의 청산은 v4 유지": active 전환 시점에 열려 있던 v4 거래는 그대로 남고
    /// (Logic·계획 불변) 종목 공통 OPEN 제한이 v5 신규 진입을 막는다.
    /// </summary>
    [Fact]
    public async Task AnExistingV4OpenTradeIsPreservedAndBlocksTheV5Entry()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var v4 = new SimTrade("v4-keep-1", Fx.Symbol, "REBOUND", Fx.At(10), 99.90, 150.0, 90.0, "저항", "ATR",
            "OPEN", null, null, null, 99.90, Score: 75, Logic: "v4", SessionEnd: Fx.SessionEnd);
        store.Seed(v4);

        var harness = Build(StructureEngineMode.Active, store);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        var trade = Assert.Single(harness.Store.Trades);
        Assert.Equal("v4-keep-1", trade.Id);
        Assert.Equal("v4", trade.Logic);
        Assert.Equal("OPEN", trade.Status);
        Assert.Equal(150.0, trade.Target); Assert.Equal(90.0, trade.Stop);
        Assert.Null(trade.Structure);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.Contains(StructureAnalysisService.NoteEntryBlockedByOpenTrade, view.Notes);
        var ready = view.Candidates.SingleOrDefault(x => x.State == "READY");
        Assert.True(ready is not null, Describe(view));            // 후보는 남지만 진입하지 않는다
    }

    [Fact]
    public async Task AnExitInTheSamePollSuppressesTheNewEntryAndLeavesTheCandidateReady()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        store.Seed(new SimTrade("v4-exiting-1", Fx.Symbol, "REBOUND", Fx.At(64), 100.00, 150.0, 99.55, "저항", "ATR",
            "OPEN", null, null, null, 100.00, Score: 75, Logic: "v4", SessionEnd: Fx.SessionEnd));

        var harness = Build(StructureEngineMode.Active, store);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        var exited = Assert.Single(harness.Store.Trades);
        Assert.Equal("v4-exiting-1", exited.Id);
        Assert.Equal("STOP", exited.Status);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.Contains(StructureAnalysisService.NoteEntrySuppressedBySamePollExit, view.Notes);
        Assert.DoesNotContain(StructureAnalysisService.NoteEntryCommitted, view.Notes);
        Assert.Equal("READY", view.CandidateSummary);
        var ready = view.Candidates.SingleOrDefault(x => x.State == "READY");
        Assert.True(ready is not null, Describe(view));
        Assert.NotNull(ready!.Plan);
        Assert.Equal(0, harness.Entries.Calls);
    }

    [Fact]
    public async Task TheNextPollWithoutAnExitEntersNormally()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        store.Seed(new SimTrade("v4-exiting-1", Fx.Symbol, "REBOUND", Fx.At(64), 99.00, 99.60, 90.0, "저항", "ATR",
            "OPEN", null, null, null, 99.00, Score: 75, Logic: "v4", SessionEnd: Fx.SessionEnd));

        var harness = Build(StructureEngineMode.Active, store);
        await PollAt(harness, 64);
        await PollAt(harness, 65);
        Assert.Equal(0, harness.Entries.Calls);
        Assert.Equal("TARGET", Assert.Single(harness.Store.Trades).Status);

        await PollAt(harness, 66);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.DoesNotContain(StructureAnalysisService.NoteEntrySuppressedBySamePollExit, view.Notes);
        Assert.Equal(1, harness.Entries.Calls);
        var entered = Assert.Single(harness.Store.Trades, StructuralSimulation.OwnsTrade);
        Assert.Equal("OPEN", entered.Status);
    }

    [Fact]
    public async Task AnExitOnAnotherSymbolDoesNotSuppressThisSymbolsEntry()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        store.Watch.Add(new WatchItem("OTHER", "다른 종목"));
        store.Seed(new SimTrade("v4-exiting-other", "OTHER", "REBOUND", Fx.At(64), 100.00, 150.0, 99.55, "저항", "ATR",
            "OPEN", null, null, null, 100.00, Score: 75, Logic: "v4", SessionEnd: Fx.SessionEnd));

        var harness = Build(StructureEngineMode.Active, store);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.Equal("STOP", Assert.Single(harness.Store.Trades, x => x.Id == "v4-exiting-other").Status);

        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.DoesNotContain(StructureAnalysisService.NoteEntrySuppressedBySamePollExit, view.Notes);
        Assert.Contains(StructureAnalysisService.NoteEntryCommitted, view.Notes);
        var entered = Assert.Single(harness.Store.Trades, x => StructuralSimulation.OwnsTrade(x) && x.Symbol == Fx.Symbol);
        Assert.Equal("OPEN", entered.Status);

        Assert.True(harness.Structure.TryGetPublished("OTHER", out var other));
        Assert.Contains(StructureAnalysisService.NoteEntrySuppressedBySamePollExit, other.Notes);
        Assert.DoesNotContain(harness.Store.Trades, x => StructuralSimulation.OwnsTrade(x) && x.Symbol == "OTHER");
    }

    /// <summary>
    /// active에서 관측 저장이 실패하면 거래·관측·래치 어느 것도 남기지 않거나(진입 전 실패) 이미 저장된
    /// 거래를 다음 poll이 멱등하게 회복한다(§12.6). 어느 쪽이든 거래는 정확히 1개다.
    /// </summary>
    [Fact]
    public async Task AFailedObservationWriteStillEndsWithExactlyOneTrade()
    {
        var harness = Build(StructureEngineMode.Active);
        await PollAt(harness, 64);
        harness.Observations.AppendFailure = new IOException("디스크 오류");
        await PollAt(harness, 65);                                 // 거래는 저장됐지만 관측·래치 실패

        Assert.Single(harness.Store.Trades);
        Assert.Contains(harness.Diagnostics.Failures, x => x.Scope == "structure-v5");

        harness.Observations.AppendFailure = null;
        await PollAt(harness, 65, seconds: 15);                    // 재시도 → AlreadyEntered로 회복
        Assert.Single(harness.Store.Trades);
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.Equal("ENTERED", view.CandidateSummary);
    }
}

/// <summary>
/// D6 통합 fixture. §14 예시와 같은 구조 문법(전일 저가+확정 피벗 지지, 실패한 하향 이탈, 전일 고가 저항)을
/// 1분봉 65+개로 결정적으로 재현한다. 임의 값이며 실제 종목 추천이 아니다.
/// - 지지: 전일 저가 99.48 + 1m 확정 피벗 저점(99.48) → 병합 Zone, 반응 성공 2회.
/// - 저항(목표): 전일 고가/20일 고가/ORB 고가 101.60 + 1m 확정 피벗 고점 → 반응 성공 1회.
/// - m46: 실패한 하향 이탈(저가 99.36 &lt; Lower, 종가는 위) → m64 트리거가 REBOUND로 성립.
/// - m66: 동결 손절 아래 저가(rollback 청산 검증용).
///
/// [#43에서 m46/m66 저가 갱신] 종전 m46 저가 99.44는 무효화 anchor를 진입(99.62)에서 $0.19 아래에 둬,
/// 왕복 수수료 $0.1992보다 좁은 손절이 됐다 — #43이 거절 대상으로 규정한 초근접 손절 그 자체다.
/// 배선(진입 포트·알림·호가)을 검증하는 fixture가 비용 하한에 걸려 계획 자체를 잃지 않도록
/// 하향 이탈을 99.36까지 깊게 하고(손절 99.35, 손절폭 $0.27) m66 저가도 그만큼 내렸다.
/// 손절 위치를 규칙이 옮긴 것이 아니라 fixture의 무효화 구조를 비용 밖으로 옮긴 것이다.
/// </summary>
static class D6
{
    /// <summary>
    /// 배선 검증용 정책. 이 fixture는 signedTrend -57의 하락 국면이라 #208 REBOUND 추세 하한에 걸리고,
    /// 계획 netR이 약 3.7이라 #209 MaxNetR 상한에도 걸린다. 둘 다 이 파일들의 검증 대상이 아니므로 함께 푼다.
    /// </summary>
    public static StructurePolicy WiringPolicy { get; } = D2.WideNetR with { TrendStateThreshold = 1000 };

    public static MarketSession Session => new(true, "정규장", null, Fx.SessionStart, Fx.SessionEnd);

    // (High, Low, Close) — Open은 직전 종가(첫 봉은 100.30). 검증은 Bars()가 수행한다.
    static readonly (double H, double L, double C)[] Table =
    [
        (100.40, 100.28, 100.35), (100.50, 100.33, 100.48), (100.62, 100.46, 100.60), (100.74, 100.58, 100.72),
        (100.87, 100.70, 100.85), (101.00, 100.83, 100.98), (101.12, 100.96, 101.10), (101.24, 101.08, 101.22),
        (101.34, 101.20, 101.32), (101.42, 101.30, 101.40), (101.50, 101.38, 101.48), (101.56, 101.46, 101.54),
        (101.60, 101.52, 101.58),                                                   // m12: 저항(101.60) 접촉 피벗 고점
        (101.58, 101.28, 101.30), (101.30, 101.05, 101.10), (101.10, 100.92, 100.95), (100.95, 100.77, 100.80),
        (100.80, 100.63, 100.66), (100.66, 100.49, 100.52), (100.52, 100.35, 100.38), (100.38, 100.21, 100.24),
        (100.24, 100.07, 100.10), (100.10, 99.94, 99.97), (99.97, 99.82, 99.85), (99.85, 99.71, 99.74),
        (99.74, 99.63, 99.66), (99.66, 99.60, 99.62), (99.62, 99.57, 99.59), (99.59, 99.55, 99.57),
        (99.57, 99.54, 99.56),
        (99.58, 99.53, 99.56), (99.56, 99.50, 99.52), (99.54, 99.48, 99.50),        // m32: 피벗 저점 99.48
        (99.55, 99.50, 99.53), (99.58, 99.53, 99.57),                               // m34: 반응 성공(종가 99.57)
        (99.67, 99.55, 99.65), (99.80, 99.63, 99.78), (99.92, 99.76, 99.90),        // m37: 피벗 고점 99.92
        (99.90, 99.83, 99.85), (99.85, 99.73, 99.75), (99.76, 99.66, 99.68), (99.69, 99.62, 99.64),
        (99.65, 99.60, 99.62), (99.63, 99.58, 99.60), (99.61, 99.56, 99.58),
        (99.58, 99.50, 99.52), (99.53, 99.36, 99.50),                               // m46: 실패한 하향 이탈
        (99.54, 99.49, 99.52), (99.57, 99.52, 99.56), (99.58, 99.53, 99.56),
        (99.58, 99.53, 99.56), (99.58, 99.53, 99.55), (99.58, 99.53, 99.56), (99.58, 99.53, 99.55),
        (99.58, 99.53, 99.56), (99.58, 99.53, 99.55), (99.58, 99.53, 99.56), (99.58, 99.53, 99.55),
        (99.58, 99.53, 99.56), (99.58, 99.53, 99.55), (99.58, 99.53, 99.56), (99.58, 99.53, 99.55),
        (99.58, 99.53, 99.56), (99.58, 99.53, 99.56),
        (99.63, 99.54, 99.62),                                                      // m64: REBOUND 트리거
        (99.66, 99.60, 99.64),                                                      // m65: 두 번째 트리거(OPEN 제한 검증)
        (99.64, 99.30, 99.40)                                                       // m66: 동결 손절 아래 저가
    ];

    /// <summary>시각 기준으로 완료된 봉만 돌려준다(실제 게이트웨이와 같은 관찰 조건).</summary>
    public static Candle[] CompletedBars(DateTimeOffset now)
    {
        var minutes = (int)Math.Floor((now - Fx.SessionStart).TotalMinutes);
        return Bars(Math.Clamp(minutes, 0, Table.Length));
    }

    public static Candle[] Bars(int count)
    {
        var bars = new Candle[count];
        var open = 100.30;
        for (var i = 0; i < count; i++)
        {
            var (high, low, close) = Table[i];
            if (high < Math.Max(open, close) || low > Math.Min(open, close) || high < low)
                throw new InvalidOperationException($"D6 fixture 봉 {i}의 OHLC가 유효하지 않다.");
            bars[i] = new Candle(Fx.At(i), open, high, low, close, i == 64 ? 1500 : 1000);
            open = close;
        }
        return bars;
    }

    public static double QuotePrice(DateTimeOffset now)
    {
        var minutes = Math.Clamp((int)Math.Floor((now - Fx.SessionStart).TotalMinutes), 1, Table.Length);
        return Table[minutes - 1].C;
    }

    /// <summary>전일: 고가 101.60(=20일 고가)·저가 99.48(=20일 저가)·종가 100.40. 진행 중 일봉은 factory가 제거한다.</summary>
    public static Candle[] Daily()
    {
        var days = new List<Candle>();
        for (var i = 5; i >= 2; i--)
            days.Add(new Candle(Fx.SessionStart.AddDays(-i), 100.00, 101.20, 99.80, 100.00, 1_000_000));
        days.Add(new Candle(Fx.SessionStart.AddDays(-1), 100.20, 101.60, 99.48, 100.40, 1_200_000));
        days.Add(new Candle(Fx.SessionStart.AddMinutes(1), 100.30, 101.70, 99.30, 99.60, 400_000));
        return [.. days];
    }
}

/// <summary>진입 포트 호출을 세는 데코레이터. shadow/off가 포트를 호출하지 않음을 증명한다.</summary>
sealed class CountingEntryPort(IStructuralTradeEntries inner) : IStructuralTradeEntries
{
    public int Calls { get; private set; }
    public List<StructuralEntryOutcome> Outcomes { get; } = [];

    public async Task<StructuralEntryResult> TryEnterAsync(StructuralEntryRequest request, CancellationToken ct)
    {
        Calls++;
        var result = await inner.TryEnterAsync(request, ct);
        Outcomes.Add(result.Outcome);
        return result;
    }
}
