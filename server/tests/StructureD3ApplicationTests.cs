using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §12 Application 통합 + §15 "Application / 저장" 승인 기준 + §16B 모드 규칙.
/// v4 실행·저장 불변, generation·idempotency, off/shadow 모드 규칙, API 상태를 고정한다.
/// </summary>
public sealed class StructureD3ApplicationTests
{
    static readonly StructurePolicy P = D3.P;
    const int Bars = 46;              // 0..44 완료, 45는 진행 중
    const int NowMinute = 45;

    sealed record Harness(MonitorPollingService Poller, RecordingStore Store, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, StructureAnalysisService Structure, MovableClock Clock, SilentDiagnostics Diagnostics);

    static Harness Build(StructureEngineMode mode, Func<int>? barCount = null, RecordingStore? store = null)
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var recording = store ?? new RecordingStore();
        recording.Watch.Add(new WatchItem(D3.Symbol, "테스트"));
        var observations = new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var structure = new StructureAnalysisService(recording,
            new StructureObservationWriter(observations, P), runtime, clock, diagnostics,
            new StructureEngineOptions(mode), P);
        var gateway = new ScriptedGateway(D3.Session, () => D3.Candles(barCount?.Invoke() ?? Bars),
            () => (100.0, clock.Now), () => D3.Daily());
        var poller = new MonitorPollingService(recording, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure);
        return new Harness(poller, recording, observations, runtime, structure, clock, diagnostics);
    }

    static async Task<Harness> Polled(StructureEngineMode mode, int polls = 1)
    {
        var minute = NowMinute;
        var harness = Build(mode, () => minute + 1);
        harness.Runtime.CommitStart();
        for (var i = 0; i < polls; i++)
        {
            harness.Clock.Now = D3.At(minute);
            await harness.Poller.PollAsync(default);
            minute++;
        }
        return harness;
    }

    static string Json(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    static string SignalsJson(MonitorPollingService poller) =>
        Json(poller.Signals.Values.OrderBy(x => x.Symbol, StringComparer.Ordinal).ToArray());

    // ── 모드 게이트 (§18, §16B) ────────────────────────────────────────────────

    /// <summary>off는 v5 계산·패널을 비활성화한다. 관측 저장소에 어떤 상호작용도 없어야 한다(§16B).</summary>
    [Fact]
    public async Task OffModeNeverComputesAndTheApiReturnsDisabledWithoutTouchingStorage()
    {
        var harness = await Polled(StructureEngineMode.Off, polls: 2);

        Assert.Equal(0, harness.Observations.Interactions);
        Assert.Equal(0, harness.Observations.Appends);
        Assert.Equal(0, harness.Observations.TextWrites);
        Assert.False(harness.Structure.TryGetPublished(D3.Symbol, out _));

        var (status, response) = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.Equal(200, status);
        Assert.Equal(StructureAnalysisStatus.Disabled, response!.Status);
        Assert.Null(response.Analysis);
        Assert.Equal("off", response.Mode);
        Assert.Equal(0, harness.Observations.Interactions);

        // v4는 그대로 신호를 계산한다.
        Assert.Single(harness.Poller.Signals);
    }

    [Fact]
    public async Task ShadowModeObservesAndPublishesWithoutOwningEntries()
    {
        var harness = await Polled(StructureEngineMode.Shadow);

        Assert.True(harness.Observations.Appends >= 1);
        Assert.True(harness.Structure.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal("shadow", view.Mode);
        Assert.Equal(StructureAnalysisService.EntryOwnerV4, view.EntryOwner);
        Assert.Equal(P.Version + "-shadow", view.RecordVersion);
        Assert.Equal(D3.At(NowMinute), view.AnalysisAsOf);
        Assert.Equal(D3.At(NowMinute - 1), view.LastCompletedBarStart);
        Assert.NotNull(view.Trend);
        Assert.Empty(harness.Diagnostics.Failures);
    }

    /// <summary>D6(§18): active는 v5가 신규 진입을 소유한다. READY 계획이 없으면 진입 보류이며 v4 진입으로 fallback하지 않는다.</summary>
    [Fact]
    public async Task ActiveModeIsOwnedByV5AndHoldsBackWithoutAReadyPlan()
    {
        var harness = await Polled(StructureEngineMode.Active);

        Assert.True(harness.Structure.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal("active", view.Mode);
        Assert.Equal(StructureAnalysisService.EntryOwnerV5, view.EntryOwner);
        // §11: shadow 관측 버전(-shadow)은 active 레코드에 쓰지 않는다.
        Assert.Equal(P.Version, view.RecordVersion);
        // 구조 근거(READY 계획)가 없으면 v4·v5 어느 쪽도 새 거래를 만들지 않는다(§19-5).
        Assert.Empty(harness.Store.Trades);
        // 실제 거래 저장소에는 v4가 쓰는 두 파일 외에 어떤 파일도 생기지 않는다.
        Assert.All(harness.Store.Writes, file => Assert.Contains(file, new[] { "simtrades.json", "positions.json" }));
    }

    [Theory]
    [InlineData("off", StructureEngineMode.Off)]
    [InlineData("OFF", StructureEngineMode.Off)]
    [InlineData("shadow", StructureEngineMode.Shadow)]
    [InlineData(" Shadow ", StructureEngineMode.Shadow)]
    [InlineData("active", StructureEngineMode.Active)]
    [InlineData("nonsense", StructureEngineMode.Off)]
    [InlineData(null, StructureEngineMode.Off)]
    [InlineData("", StructureEngineMode.Off)]
    public void ConfiguredModeFallsBackToOffForAnythingUnknown(string? configured, StructureEngineMode expected)
        => Assert.Equal(expected, StructureEngineOptions.Parse(configured).Mode);

    // ── shadow 불변성 (§15, §16B) ─────────────────────────────────────────────

    /// <summary>
    /// v4 실행·저장 결과가 off와 shadow에서 바이트 단위로 같아야 한다.
    /// simtrades/positions/신호(=UI 알림 근거)의 직렬화 결과와 저장 호출 순서를 함께 비교한다.
    /// </summary>
    [Fact]
    public async Task ShadowLeavesTradesPositionsAndSignalsByteIdenticalToOff()
    {
        var off = await Polled(StructureEngineMode.Off, polls: 3);
        var shadow = await Polled(StructureEngineMode.Shadow, polls: 3);

        Assert.Equal(Json(off.Store.Trades), Json(shadow.Store.Trades));
        Assert.Equal(Json(off.Store.Positions), Json(shadow.Store.Positions));
        Assert.Equal(SignalsJson(off.Poller), SignalsJson(shadow.Poller));
        Assert.Equal(off.Store.Writes, shadow.Store.Writes);
        Assert.DoesNotContain("structure-observations", string.Join('|', shadow.Store.Writes));

        Assert.Empty(off.Observations.AllLines);
        Assert.NotEmpty(shadow.Observations.AllLines);
    }

    /// <summary>
    /// 입력과 무관한 구조적 증명: v5 경로가 실제 거래 저장소에 쓰기를 시도하면 예외가 난다.
    /// shadow 관측이 끝까지 성공하면 v5는 simtrades/positions를 한 번도 건드리지 않은 것이다.
    /// </summary>
    [Fact]
    public async Task StructurePathPerformsNoWriteThroughTheTradeStoreAtAll()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = new RecordingStore { RejectWrites = true };
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);

        Assert.Empty(store.Writes);
        Assert.Equal(1, observations.Appends);
        Assert.Equal(1, observations.TextWrites);
        Assert.True(service.TryGetPublished(D3.Symbol, out _));
    }

    /// <summary>
    /// §15: v5에서 구조가 없을 때 `MarketRules.Enter`/`PriceLevels.Enter` 폴백이 호출되지 않는다.
    /// v5 소유 소스는 그 진입점을 참조조차 하지 않는다(정적 불변식).
    /// </summary>
    [Fact]
    public void V5SourcesNeverReferenceTheV4AtrOrLevelFallbacks()
    {
        var serverRoot = ServerRoot();
        string[] owned =
        [
            Path.Combine(serverRoot, "Application", "StructureAnalysisService.cs"),
            Path.Combine(serverRoot, "Application", "StructureSnapshotFactory.cs"),
            Path.Combine(serverRoot, "Application", "StructureObservationWriter.cs"),
            Path.Combine(serverRoot, "Application", "StructureAlertPublisher.cs"),
            .. Directory.GetFiles(Path.Combine(serverRoot, "Domain", "Structure"), "*.cs")
        ];
        Assert.True(owned.Length >= 12, $"v5 소유 파일을 찾지 못했다: {owned.Length}");

        foreach (var file in owned)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[]
                     {
                         "MarketRules.Enter", "PriceLevels.Enter", "MarketRules.FreezeRisk", "PriceLevels.Compute",
                         "SimulationEngine", "SimulationEntry", "simtrades.json", "positions.json",
                         "Indicators.Evaluate"
                     })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        // D6 배선 파일(v5 계획 → 기존 거래 저장소): 거래 저장 연결은 이 두 파일에만 허용하되,
        // ATR/레벨 폴백과 v4 점수 경로 참조는 여전히 금지다(§19-5, README §5.1).
        string[] bridges =
        [
            Path.Combine(serverRoot, "Domain", "StructuralSimulation.cs"),
            Path.Combine(serverRoot, "Application", "StructuralEntryService.cs")
        ];
        foreach (var file in bridges)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[]
                     {
                         "MarketRules.Enter", "PriceLevels.Enter", "MarketRules.FreezeRisk", "PriceLevels.Compute",
                         "Indicators.Evaluate"
                     })
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        // 폴링의 v5 호출은 off 게이트 뒤에 있고 종목 단위로 격리돼 있다.
        var polling = File.ReadAllText(Path.Combine(serverRoot, "Application", "MonitorPollingService.cs"));
        Assert.Contains("structure.Mode == StructureEngineMode.Off", polling, StringComparison.Ordinal);
        Assert.Contains("structure-v5", polling, StringComparison.Ordinal);
    }

    static string ServerRoot()
    {
        // 로컬은 server/tests/bin/... 상위에 Astra.Server.csproj가 있고,
        // CI는 아티팩트 폴더에서 실행되므로 각 상위 디렉터리의 server/ 하위도 함께 확인한다.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Astra.Server.csproj"))) return directory.FullName;
            if (File.Exists(Path.Combine(directory.FullName, "server", "Astra.Server.csproj"))) return Path.Combine(directory.FullName, "server");
        }
        Assert.Fail("Astra.Server.csproj 기준의 서버 루트를 찾지 못했다.");
        return null!;
    }

    // ── 캐시·generation·저장 실패 (§12.3~§12.6) ────────────────────────────────

    /// <summary>새 완료 봉이 없는 poll은 구조를 전체 재계산하지 않는다(§12.4). 저장 시도조차 하지 않는다.</summary>
    [Fact]
    public async Task PollWithoutANewCompletedBarSkipsTheFullRecomputation()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);
        Assert.Equal(1, observations.Appends);
        var first = observations.Interactions;

        // 두 번째 호출이 재계산·저장을 시도하면 이 예외가 표면화된다.
        observations.AppendFailure = new IOException("재계산하면 안 된다");
        await service.ObserveAsync(D3.Request(generation, Bars), default);

        Assert.Equal(1, observations.Appends);
        Assert.Equal(first, observations.Interactions);
    }

    [Fact]
    public async Task ANewCompletedBarDoesTriggerAFreshObservation()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);
        clock.Now = D3.At(NowMinute + 1);
        await service.ObserveAsync(D3.Request(generation, Bars + 1, quoteAt: D3.At(NowMinute + 1)), default);

        Assert.Equal(2, observations.Appends);
        Assert.True(service.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal(D3.At(NowMinute + 1), view.AnalysisAsOf);
    }

    /// <summary>stop 중에 v5 계산이 끝나도 늦은 저장·공개가 없어야 한다(§15).</summary>
    [Fact]
    public async Task StopDuringAnalysisPreventsLateObservationAndPublish()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);
        runtime.CommitStop();

        await service.ObserveAsync(D3.Request(generation, Bars), default);

        Assert.Equal(0, observations.Appends);
        Assert.Equal(0, observations.TextWrites);
        Assert.False(service.TryGetPublished(D3.Symbol, out _));
    }

    [Fact]
    public async Task WatchlistChangeInvalidatesTheGenerationSoNothingIsCommitted()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);
        runtime.CommitWatchlistChange();

        await service.ObserveAsync(D3.Request(generation, Bars), default);

        Assert.Equal(0, observations.Appends);
        Assert.False(service.TryGetPublished(D3.Symbol, out _));
    }

    [Fact]
    public async Task SessionTransitionDuringAnalysisPreventsTheCommit()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);
        // 세션이 바뀌면 이전 세션 기준으로 계산한 결과를 commit하지 않는다.
        runtime.TryCommit(generation, s => s with { Market = D3.Session with { Start = D3.SessionStart.AddDays(1), End = D3.SessionEnd.AddDays(1) } });

        await service.ObserveAsync(D3.Request(generation, Bars), default);

        Assert.Equal(0, observations.Appends);
        Assert.False(service.TryGetPublished(D3.Symbol, out _));
    }

    /// <summary>저장이 실패하면 이벤트를 소비하지 않는다(§12.6, §15). 다음 poll에서 그대로 다시 시도한다.</summary>
    [Fact]
    public async Task FailedObservationWriteDoesNotConsumeTheEventOrPublishASnapshot()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore { AppendFailure = new IOException("디스크 오류") };
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await Assert.ThrowsAsync<IOException>(() => service.ObserveAsync(D3.Request(generation, Bars), default));
        Assert.Equal(0, observations.Appends);
        Assert.Equal(0, observations.TextWrites);
        Assert.False(service.TryGetPublished(D3.Symbol, out _));

        observations.AppendFailure = null;
        await service.ObserveAsync(D3.Request(generation, Bars), default);
        Assert.Equal(1, observations.Appends);
        Assert.Equal(1, observations.TextWrites);
        Assert.True(service.TryGetPublished(D3.Symbol, out _));
    }

    /// <summary>저장 성공 뒤 generation이 바뀌면 래치·공개 snapshot을 갱신하지 않는다(§12.6).</summary>
    [Fact]
    public async Task GenerationChangeAfterTheWriteStillBlocksTheLatchAndPublishUpdate()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);
        observations.BeforeAppend = () => runtime.CommitStop();

        await service.ObserveAsync(D3.Request(generation, Bars), default);

        Assert.Equal(1, observations.Appends);
        Assert.Equal(0, observations.TextWrites);
        Assert.False(service.TryGetPublished(D3.Symbol, out _));
    }

    /// <summary>symbol 단위 오류 격리: v5 실패가 v4 신호·거래를 훼손하지 않는다(§16).</summary>
    [Fact]
    public async Task StructureFailureIsIsolatedFromTheV4Signal()
    {
        var minute = NowMinute;
        var harness = Build(StructureEngineMode.Shadow, () => minute + 1);
        harness.Observations.AppendFailure = new IOException("디스크 오류");
        harness.Runtime.CommitStart();

        await harness.Poller.PollAsync(default);

        Assert.Single(harness.Poller.Signals);
        Assert.Equal("connected", harness.Poller.ConnectionStatus);
        Assert.Contains(harness.Diagnostics.Failures, x => x.Scope == "structure-v5");
        Assert.False(harness.Structure.TryGetPublished(D3.Symbol, out _));
    }

    // ── watermark · 재시작 (§16B) ─────────────────────────────────────────────

    /// <summary>첫 관측은 watermark만 seed하고 신규 트리거를 만들지 않는다. 다음 봉부터 평가한다.</summary>
    [Fact]
    public async Task FirstObservationSeedsTheWatermarkAndTheNextBarIsEvaluated()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);
        Assert.True(service.TryGetPublished(D3.Symbol, out var seeded));
        Assert.Contains(StructuralLifecycle.NoteWatermarkSeeded, seeded.Notes);

        clock.Now = D3.At(NowMinute + 1);
        await service.ObserveAsync(D3.Request(generation, Bars + 1, quoteAt: D3.At(NowMinute + 1)), default);
        Assert.True(service.TryGetPublished(D3.Symbol, out var next));
        Assert.DoesNotContain(StructuralLifecycle.NoteWatermarkSeeded, next.Notes);
    }

    /// <summary>놓친 중간 봉은 구조 복원에만 쓰이고 신규 트리거·알림을 만들지 않는다(§16B).</summary>
    [Fact]
    public async Task MissedBarsAreReportedAsReplayOnly()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);
        clock.Now = D3.At(NowMinute + 5);
        await service.ObserveAsync(D3.Request(generation, Bars + 5, quoteAt: D3.At(NowMinute + 5)), default);

        Assert.True(service.TryGetPublished(D3.Symbol, out var view));
        Assert.Contains(view.Notes, x => x.StartsWith(StructuralLifecycle.NoteMissedBars, StringComparison.Ordinal));
        Assert.Contains($"{StructuralLifecycle.NoteMissedBars}x4", view.Notes);
    }

    /// <summary>재시작 후 같은 봉으로 동일 이벤트가 다시 만들어지거나 관측이 중복 append되지 않는다(§15, §16).</summary>
    [Fact]
    public async Task RestartRestoresTheWatermarkAndProducesNoDuplicateObservationOrEvent()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var before = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await before.ObserveAsync(D3.Request(generation, Bars), default);
        clock.Now = D3.At(NowMinute + 1);
        await before.ObserveAsync(D3.Request(generation, Bars + 1, quoteAt: D3.At(NowMinute + 1)), default);
        Assert.Equal(2, observations.Appends);
        var persisted = observations.Texts[StructureAnalysisService.LatchFile];

        // 재시작: 새 서비스 인스턴스가 같은 v5 저장소에서 래치를 복원한다.
        var afterRuntime = new MonitorRuntimeState();
        var afterGeneration = D3.StartedRuntime(afterRuntime);
        var after = D3.Service(StructureEngineMode.Shadow, observations, afterRuntime, clock);

        await after.ObserveAsync(D3.Request(afterGeneration, Bars + 1, quoteAt: D3.At(NowMinute + 1)), default);

        Assert.Equal(2, observations.Appends);
        Assert.True(after.TryGetPublished(D3.Symbol, out var view));
        Assert.Contains(StructuralLifecycle.NoteBarAlreadyEvaluated, view.Notes);
        Assert.All(view.Candidates, x => Assert.NotEqual("READY", x.State));
        Assert.Equal(StructureLatchStorage.Parse(persisted).Single().WatermarkBarStart,
            StructureLatchStorage.Parse(observations.Texts[StructureAnalysisService.LatchFile]).Single().WatermarkBarStart);
    }

    /// <summary>이벤트/정책 변경 시 full snapshot, 그 외 요약이다(§16). 매 15초 전체 Zone 배열을 덤프하지 않는다.</summary>
    [Fact]
    public async Task ObservationDetailIsFullOnEventChangeAndSummaryOtherwise()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);
        clock.Now = D3.At(NowMinute + 1);
        await service.ObserveAsync(D3.Request(generation, Bars + 1, quoteAt: D3.At(NowMinute + 1)), default);

        var records = observations.AllLines.Select(x => JsonDocument.Parse(x).RootElement).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal("full", records[0].GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Array, records[0].GetProperty("zones").ValueKind);
        Assert.Equal("summary", records[1].GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("zones").ValueKind);
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("quality").ValueKind);
    }

    [Fact]
    public async Task SessionCloseClearsTheV5MemoryAndPublishedSnapshots()
    {
        var harness = await Polled(StructureEngineMode.Shadow);
        Assert.True(harness.Structure.TryGetPublished(D3.Symbol, out _));

        harness.Poller.Clear();
        Assert.False(harness.Structure.TryGetPublished(D3.Symbol, out _));

        harness.Poller.Remove(D3.Symbol);
        Assert.False(harness.Structure.TryGetPublished(D3.Symbol, out _));
    }

    // ── API (§12) ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("!!")]
    [InlineData("")]
    [InlineData("TOOOOOOOOOOOOLONG")]
    public async Task InvalidSymbolSyntaxIsFourHundred(string symbol)
    {
        var harness = await Polled(StructureEngineMode.Shadow);
        Assert.Equal(400, (await harness.Structure.GetAsync(symbol, default)).HttpStatus);
    }

    [Fact]
    public async Task UnknownSymbolIsFourHundredFour()
    {
        var harness = await Polled(StructureEngineMode.Shadow);
        Assert.Equal(404, (await harness.Structure.GetAsync("NVDA", default)).HttpStatus);
    }

    /// <summary>조회는 마지막 공개 snapshot만 돌려주고 전체 분석이나 새 거래를 실행하지 않는다(§12).</summary>
    [Fact]
    public async Task QueryReturnsTheLastPublishedSnapshotWithoutRecomputing()
    {
        var harness = await Polled(StructureEngineMode.Shadow);
        var interactions = harness.Observations.Interactions;
        var writes = harness.Store.Writes.Count;

        var (status, response) = await harness.Structure.GetAsync(D3.Symbol.ToLowerInvariant(), default);

        Assert.Equal(200, status);
        Assert.Equal(StructureAnalysisStatus.Available, response!.Status);
        Assert.NotNull(response.Analysis);
        Assert.Equal(D3.At(NowMinute), response.Analysis!.AnalysisAsOf);
        Assert.Equal(interactions, harness.Observations.Interactions);
        Assert.Equal(writes, harness.Store.Writes.Count);
    }

    [Fact]
    public async Task WarmupAndStoppedAndClosedAreTwoHundredWithAnExplicitStatus()
    {
        var harness = Build(StructureEngineMode.Shadow);
        var stopped = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.Equal(200, stopped.HttpStatus);
        Assert.Equal(StructureAnalysisStatus.Stopped, stopped.Response!.Status);
        Assert.NotNull(stopped.Response.Message);

        var generation = harness.Runtime.CommitStart();
        harness.Runtime.TryCommit(generation, s => s with { Market = new MarketSession(false, "휴장", null, null, null) });
        var closed = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.Equal(200, closed.HttpStatus);
        Assert.Equal(StructureAnalysisStatus.MarketClosed, closed.Response!.Status);

        harness.Runtime.TryCommit(generation, s => s with { Market = D3.Session });
        var warmup = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.Equal(200, warmup.HttpStatus);
        Assert.Equal(StructureAnalysisStatus.Warmup, warmup.Response!.Status);
        Assert.Null(warmup.Response.Analysis);
    }

    /// <summary>§12: `GET /api/state`의 structureSummary는 additive이며 기존 score/action(v4) 의미를 덮어쓰지 않는다.</summary>
    [Fact]
    public async Task StateSummaryIsAdditiveAndLabelsTheLegacyScoreEngineAsV4()
    {
        var harness = await Polled(StructureEngineMode.Shadow);
        var state = await new StateQueryService(harness.Store, harness.Poller, new QuietStream(), harness.Runtime,
            harness.Clock, harness.Structure).GetAsync();

        using var document = JsonDocument.Parse(Json(state));
        var root = document.RootElement;
        // 기존 필드가 그대로 남아 있다.
        foreach (var field in new[] { "running", "connection", "transport", "market", "updatedAt", "watchlist", "signals", "events" })
            Assert.True(root.TryGetProperty(field, out _), field);

        var summary = root.GetProperty("structureSummary");
        Assert.Equal("shadow", summary.GetProperty("mode").GetString());
        Assert.Equal("v4", summary.GetProperty("legacyScoreEngine").GetString());
        Assert.Equal("v4", summary.GetProperty("entryOwner").GetString());
        Assert.Equal(P.PolicyHash, summary.GetProperty("policyHash").GetString());
        var row = Assert.Single(summary.GetProperty("symbols").EnumerateArray().ToArray());
        Assert.Equal(D3.Symbol, row.GetProperty("symbol").GetString());
        Assert.Equal(StructureAnalysisStatus.Available, row.GetProperty("status").GetString());
        // 이슈 #26: 대표 후보 종류 필드는 additive이며 대표 후보가 없으면 null이다(0·기본값으로 위장하지 않음).
        Assert.True(row.TryGetProperty("preferredKind", out var preferredKind));
        if (row.GetProperty("preferredCandidateId").ValueKind == JsonValueKind.Null)
            Assert.Equal(JsonValueKind.Null, preferredKind.ValueKind);

        // v4 점수는 여전히 정수 score/action으로만 표현된다.
        var signal = Assert.Single(root.GetProperty("signals").EnumerateArray().ToArray());
        Assert.Equal(JsonValueKind.Number, signal.GetProperty("score").ValueKind);
        Assert.False(signal.TryGetProperty("entryQuality", out _));
    }

    [Fact]
    public async Task StateSummaryReportsDisabledPerSymbolWhenTheEngineIsOff()
    {
        var harness = await Polled(StructureEngineMode.Off);
        var state = await new StateQueryService(harness.Store, harness.Poller, new QuietStream(), harness.Runtime,
            harness.Clock, harness.Structure).GetAsync();

        using var document = JsonDocument.Parse(Json(state));
        var summary = document.RootElement.GetProperty("structureSummary");
        Assert.Equal("off", summary.GetProperty("mode").GetString());
        var row = Assert.Single(summary.GetProperty("symbols").EnumerateArray().ToArray());
        Assert.Equal(StructureAnalysisStatus.Disabled, row.GetProperty("status").GetString());
    }

    /// <summary>표시 가격(quote)과 분석 AsOf를 구분해 노출한다(§2, §13).</summary>
    [Fact]
    public async Task PublishedViewSeparatesQuoteTimeFromAnalysisAsOf()
    {
        var clock = new MovableClock(D3.At(NowMinute).AddSeconds(20));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars, quoteAt: clock.Now), default);

        Assert.True(service.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal(D3.At(NowMinute), view.AnalysisAsOf);
        Assert.Equal(clock.Now, view.QuoteAt);
        Assert.NotEqual(view.AnalysisAsOf, view.QuoteAt);
        Assert.Equal(100m, view.QuotePrice);
    }

    /// <summary>UI의 자동 분석 숫자와 저장 기록은 같은 version/policyHash 기준이어야 한다(§12).</summary>
    [Fact]
    public async Task ViewAndObservationShareTheSameVersionAndPolicyHash()
    {
        var harness = await Polled(StructureEngineMode.Shadow);
        Assert.True(harness.Structure.TryGetPublished(D3.Symbol, out var view));

        using var document = JsonDocument.Parse(harness.Observations.AllLines.First());
        var root = document.RootElement;
        Assert.Equal(view.PolicyHash, root.GetProperty("policyHash").GetString());
        Assert.Equal(view.EngineVersion, root.GetProperty("engineVersion").GetString());
        Assert.Equal(view.RecordVersion, root.GetProperty("recordVersion").GetString());
        Assert.Equal(P.PolicyHash, view.PolicyHash);
    }

    /// <summary>구조가 없으면 계획을 만들지 않는다. 공개 snapshot에 Stop/Target=0을 끼워 넣지 않는다(§11, §19-5).</summary>
    [Fact]
    public async Task PublishedCandidatesNeverCarryFabricatedStopsOrTargets()
    {
        var harness = await Polled(StructureEngineMode.Shadow, polls: 4);
        Assert.True(harness.Structure.TryGetPublished(D3.Symbol, out var view));

        foreach (var candidate in view.Candidates)
        {
            if (candidate.State == "READY") { Assert.NotNull(candidate.Plan); continue; }
            Assert.Null(candidate.Plan);
            Assert.Null(candidate.Stop);
            Assert.Null(candidate.Target);
            Assert.True(candidate.RejectionCodes.Length > 0 || candidate.Notes.Length > 0);
        }

        foreach (var zone in view.Zones)
        {
            if (zone.Eligible) Assert.Empty(zone.RejectReasons);
            else Assert.NotEmpty(zone.RejectReasons);
        }
    }

    [Fact]
    public async Task RepeatedRunsOnTheSameInputProduceTheSameObservation()
    {
        var a = await Polled(StructureEngineMode.Shadow, polls: 3);
        var b = await Polled(StructureEngineMode.Shadow, polls: 3);

        Assert.Equal(a.Observations.AllLines.Count(), b.Observations.AllLines.Count());
        Assert.Equal(a.Observations.AllLines.ToArray(), b.Observations.AllLines.ToArray());
    }
}
