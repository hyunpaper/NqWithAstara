using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

static class Fidelity
{
    public static readonly decimal[] Buckets =
        [100.0m, 100.6m, 101.4m, 101.0m, 100.8m, 101.6m, 102.4m, 102.0m, 101.8m, 102.6m, 103.4m];

    public const int TriggerMinute = 80;
    public const int AnalysisMinute = 81;
    public const double TriggerPrice = 103.12;
    public const double BreakingPrice = 102.50;

    static Candle C(int minute, decimal open, decimal high, decimal low, decimal close, double volume = 1000) =>
        new(D3.At(minute), (double)open, (double)high, (double)low, (double)close, volume);

    public static Candle[] Bars()
    {
        var bars = new List<Candle>();
        for (var bucket = 0; bucket < Buckets.Length; bucket++)
            for (var i = 0; i < 5; i++)
                bars.Add(C(bucket * 5 + i, Buckets[bucket], Buckets[bucket] + .10m, Buckets[bucket] - .10m,
                    Buckets[bucket]));
        bars.Add(C(55, 103.40m, 104.00m, 103.35m, 103.60m));
        bars.Add(C(56, 103.60m, 103.65m, 103.30m, 103.35m));
        bars.Add(C(57, 103.35m, 103.40m, 103.10m, 103.15m));
        bars.Add(C(58, 103.15m, 103.20m, 102.98m, 103.00m));
        bars.Add(C(59, 103.00m, 103.04m, 102.95m, 102.97m));
        bars.Add(C(60, 102.97m, 103.02m, 102.96m, 103.00m));
        bars.Add(C(61, 103.00m, 103.04m, 102.98m, 103.02m));
        bars.Add(C(62, 103.02m, 103.05m, 103.00m, 103.03m));
        bars.Add(C(63, 103.03m, 103.06m, 103.01m, 103.04m));
        bars.Add(C(64, 103.04m, 103.07m, 103.02m, 103.05m));
        bars.Add(C(65, 103.05m, 103.08m, 103.03m, 103.06m));
        bars.Add(C(66, 103.06m, 103.08m, 103.04m, 103.06m));
        bars.Add(C(67, 103.06m, 103.09m, 103.04m, 103.07m));
        bars.Add(C(68, 103.07m, 103.09m, 103.05m, 103.07m));
        bars.Add(C(69, 103.07m, 103.10m, 103.05m, 103.08m));
        bars.Add(C(70, 103.08m, 103.10m, 103.04m, 103.06m));
        bars.Add(C(71, 103.06m, 103.08m, 103.00m, 103.02m));
        bars.Add(C(72, 103.02m, 103.04m, 102.98m, 103.00m));
        bars.Add(C(73, 103.00m, 103.02m, 102.96m, 102.98m));
        bars.Add(C(74, 102.98m, 103.04m, 102.97m, 103.02m));
        bars.Add(C(75, 103.02m, 103.06m, 103.00m, 103.04m));
        bars.Add(C(76, 103.04m, 103.06m, 103.02m, 103.05m));
        bars.Add(C(77, 103.05m, 103.08m, 103.03m, 103.06m));
        bars.Add(C(78, 103.06m, 103.08m, 103.04m, 103.07m));
        bars.Add(C(79, 103.07m, 103.09m, 103.05m, 103.08m));
        bars.Add(C(TriggerMinute, 103.08m, 103.18m, 103.06m, (decimal)TriggerPrice, 2000));
        return [.. bars];
    }

    public static Candle[] Daily() => [new(D3.SessionStart.AddDays(-1), 103.0, 104.00, 102.95, 102.95, 1_000_000)];

    public static ImmutableArray<StructureDailyBar> DailyBars() =>
        [new(MarketRules.TradingDate(D3.SessionStart.AddDays(-1)), 103.0m, 104.00m, 102.95m, 102.95m, 1_000_000)];

    public static StructureObservationRequest Request(long generation, int upToMinute, double price) =>
        new(D3.Symbol, generation, D3.Session, Bars().Where(x => x.Timestamp < D3.At(upToMinute)).ToArray(),
            Daily(), price, D3.At(upToMinute),
            new StructureLiquidity((decimal)price - .01m, (decimal)price + .01m, D3.At(upToMinute), 500, 500));

    public static (ImmutableArray<PriceZone> Zones, ImmutableArray<TouchEpisode> Episodes,
        ImmutableArray<StructureBar> Bars1m, double? Atr) Structure(StructurePolicy policy)
    {
        var analysisAsOf = D3.At(AnalysisMinute);
        var cutoff = D3.At(TriggerMinute);
        var normalized = BarAggregator.Normalize(Bars(), D3.SessionStart, D3.SessionEnd, analysisAsOf);
        var five = BarAggregator.Aggregate(normalized.Bars, D3.SessionStart, cutoff, policy);
        var built = ZoneBuilder.Build(new ZoneBuildRequest(D3.Symbol, D3.SessionStart, D3.SessionEnd, cutoff,
            normalized.Bars, five, DailyBars(), [], []), policy);
        var evaluated = ZoneEvaluator.Evaluate(built.Zones, new ZoneEvaluationRequest(D3.SessionStart, cutoff,
            normalized.Bars, [], built.RetiredZoneIds), policy);
        return (evaluated.Zones, evaluated.Episodes, normalized.Bars,
            SessionAtr.At(normalized.Bars, SessionAtr.Series(normalized.Bars, policy), cutoff));
    }

    public static TrendAssessment RealTrend(StructurePolicy policy)
    {
        var analysisAsOf = D3.At(AnalysisMinute);
        var normalized = BarAggregator.Normalize(Bars(), D3.SessionStart, D3.SessionEnd, analysisAsOf);
        return TrendEvaluator.Evaluate(TrendRequest.Create(D3.Symbol, D3.SessionStart, analysisAsOf, normalized.Bars,
            BarAggregator.Aggregate(normalized.Bars, D3.SessionStart, analysisAsOf, policy)), policy);
    }

    public static SetupDetectionResult Detect(TrendAssessment trend, StructurePolicy policy)
    {
        var (zones, episodes, bars, atr) = Structure(policy);
        var analysisAsOf = D3.At(AnalysisMinute);
        return SetupDetector.Detect(SetupDetectionRequest.Create(D3.Symbol, D3.SessionStart, D3.SessionEnd,
            analysisAsOf, analysisAsOf, bars, zones, episodes, trend, atr, (decimal)TriggerPrice, analysisAsOf,
            new StructureLiquidity((decimal)TriggerPrice - .01m, (decimal)TriggerPrice + .01m,
                D3.At(AnalysisMinute), 500, 500)), policy);
    }
}

public sealed class StructureObservationTransitionTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    sealed record Harness(StructureAnalysisService Service, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, MovableClock Clock, RecordingStore Store, long Generation);

    static Harness Build()
    {
        var clock = new MovableClock(D3.At(Fidelity.TriggerMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(D3.Symbol, "테스트"));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store);
        return new Harness(service, observations, runtime, clock, store, D3.StartedRuntime(runtime));
    }

    static async Task<Harness> Ready()
    {
        var harness = Build();
        await harness.Service.ObserveAsync(Fidelity.Request(harness.Generation, Fidelity.TriggerMinute, 103.08),
            default);
        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute);
        await harness.Service.ObserveAsync(
            Fidelity.Request(harness.Generation, Fidelity.AnalysisMinute, Fidelity.TriggerPrice), default);
        return harness;
    }

    static StructureObservationRequest Breaking(long generation) =>
        Fidelity.Request(generation, Fidelity.AnalysisMinute, Fidelity.TriggerPrice) with
        {
            QuotePrice = Fidelity.BreakingPrice
        };

    static JsonElement[] Records(MemoryObservationStore observations) =>
        observations.AllLines.Select(x => JsonDocument.Parse(x).RootElement).ToArray();

    [Fact]
    public async Task TheFixtureReachesReadyOnTheRealApplicationPath()
    {
        var harness = await Ready();

        Assert.True(harness.Service.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal("READY", view.CandidateSummary);
        var candidate = Assert.Single(view.Candidates);
        Assert.Equal("PULLBACK", candidate.Kind);
        Assert.Equal("READY", candidate.State);
        Assert.NotNull(candidate.Plan);
        Assert.Equal(2, harness.Observations.Appends);
    }

    /// <summary>#146: components는 full 관측에만 실리고, 같은 봉 안 전이 관측은 추가 부담 없이 이전과 동일하다.</summary>
    [Fact]
    public async Task TheFullReadyObservationCarriesTrendComponentsButTheTransitionDoesNot()
    {
        var harness = await Ready();
        var full = Records(harness.Observations)[^1];
        Assert.Equal("full", full.GetProperty("detail").GetString());
        var trend = full.GetProperty("trend");
        Assert.True(trend.TryGetProperty("components", out var components));
        Assert.NotEqual(JsonValueKind.Null, components.ValueKind);
        Assert.True(components.GetProperty("efficiency").TryGetProperty("value", out _));

        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(30);
        await harness.Service.ObserveAsync(Breaking(harness.Generation), default);

        var transition = Records(harness.Observations)[^1];
        Assert.Equal(StructureAnalysisService.DetailTransition, transition.GetProperty("detail").GetString());
        var transitionTrend = transition.GetProperty("trend");
        Assert.True(transitionTrend.TryGetProperty("components", out var transitionComponents));
        Assert.Equal(JsonValueKind.Null, transitionComponents.ValueKind);
    }

    [Fact]
    public async Task AReadyToInvalidatedTransitionInsideTheSameBarIsWrittenToObservations()
    {
        var harness = await Ready();
        var beforeAppends = harness.Observations.Appends;

        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(30);
        await harness.Service.ObserveAsync(Breaking(harness.Generation), default);

        Assert.Equal(beforeAppends + 1, harness.Observations.Appends);
        Assert.True(harness.Service.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal("INVALIDATED", view.CandidateSummary);
        Assert.Contains(StructuralLifecycle.NoteLiveInvalidated, Assert.Single(view.Candidates).Notes);

        var records = Records(harness.Observations);
        var last = records[^1];
        Assert.Equal(StructureAnalysisService.DetailTransition, last.GetProperty("detail").GetString());
        Assert.Equal(D3.At(Fidelity.TriggerMinute), last.GetProperty("lastCompletedBarStart").GetDateTimeOffset());
        Assert.Equal("INVALIDATED", last.GetProperty("candidateSummary").GetString());
        Assert.Equal("INVALIDATED",
            last.GetProperty("candidates")[0].GetProperty("state").GetString());
        Assert.Equal("READY", records[^2].GetProperty("candidateSummary").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("zones").ValueKind);
    }

    [Fact]
    public async Task RepeatedPollsWithoutAStateChangeNeverAppendAgain()
    {
        var harness = await Ready();

        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(30);
        await harness.Service.ObserveAsync(Breaking(harness.Generation), default);
        var afterTransition = harness.Observations.Appends;
        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(45);
        await harness.Service.ObserveAsync(Breaking(harness.Generation), default);
        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(59);
        await harness.Service.ObserveAsync(Breaking(harness.Generation), default);

        Assert.Equal(afterTransition, harness.Observations.Appends);
    }

    [Fact]
    public async Task TheTransitionCommitsTheTerminalStateToThePersistedLatch()
    {
        var harness = await Ready();
        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(30);
        await harness.Service.ObserveAsync(Breaking(harness.Generation), default);

        var latch = Assert.Single(StructureLatchStorage.Parse(
            harness.Observations.Texts[StructureAnalysisService.LatchFile]));
        var tombstone = Assert.Single(latch.Tombstones);
        Assert.Equal(CandidateDisposition.Invalidated, tombstone.Value);
        Assert.NotNull(latch.LastObservationId);
        Assert.Equal(Records(harness.Observations)[^1].GetProperty("observationId").GetString(),
            latch.LastObservationId);
    }

    [Fact]
    public async Task AGenerationBumpKeepsTheReadyCandidateAndAddsNoObservation()
    {
        var harness = await Ready();
        var appends = harness.Observations.Appends;
        Assert.True(harness.Service.TryGetPublished(D3.Symbol, out var before));

        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(20);
        var bumped = D3.StartedRuntime(harness.Runtime);
        Assert.NotEqual(harness.Generation, bumped);
        await harness.Service.ObserveAsync(Fidelity.Request(bumped, Fidelity.AnalysisMinute, Fidelity.TriggerPrice),
            default);

        Assert.True(harness.Service.TryGetPublished(D3.Symbol, out var after));
        Assert.Equal("READY", after.CandidateSummary);
        Assert.Equal("READY", Assert.Single(after.Candidates).State);
        Assert.NotNull(Assert.Single(after.Candidates).Plan);
        Assert.Equal(before.PreferredCandidateId, after.PreferredCandidateId);
        Assert.DoesNotContain(StructuralLifecycle.NoteBarAlreadyEvaluated, after.Notes);
        Assert.DoesNotContain(StructuralLifecycle.CodeNewTriggerSuppressed, Assert.Single(after.Candidates).Notes);
        Assert.Equal(bumped, after.Generation);
        Assert.Equal(appends, harness.Observations.Appends);
    }

    [Fact]
    public async Task AGenerationBumpStillLetsALaterTransitionBeRecorded()
    {
        var harness = await Ready();
        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(20);
        var bumped = D3.StartedRuntime(harness.Runtime);
        await harness.Service.ObserveAsync(Fidelity.Request(bumped, Fidelity.AnalysisMinute, Fidelity.TriggerPrice),
            default);
        var appends = harness.Observations.Appends;

        harness.Clock.Now = D3.At(Fidelity.AnalysisMinute).AddSeconds(30);
        await harness.Service.ObserveAsync(Breaking(bumped), default);

        Assert.Equal(appends + 1, harness.Observations.Appends);
        Assert.True(harness.Service.TryGetPublished(D3.Symbol, out var view));
        Assert.Equal("INVALIDATED", view.CandidateSummary);
    }

    [Fact]
    public void ObservationIdSeparatesEventSignaturesInsideTheSameBar()
    {
        var bar = D3.At(Fidelity.TriggerMinute);
        var ready = "evt=Ready;preferred=evt";
        var invalidated = "evt=Invalidated;preferred=null";

        var first = StructuralLifecycle.ObservationId(D3.Symbol, bar, P.PolicyHash, ready);
        Assert.Equal(first, StructuralLifecycle.ObservationId(D3.Symbol, bar, P.PolicyHash, ready));
        Assert.NotEqual(first, StructuralLifecycle.ObservationId(D3.Symbol, bar, P.PolicyHash, invalidated));
        Assert.NotEqual(first, StructuralLifecycle.ObservationId(D3.Symbol, D3.At(Fidelity.TriggerMinute + 1),
            P.PolicyHash, ready));
        Assert.Equal(StructuralLifecycle.ObservationId(D3.Symbol, bar, P.PolicyHash),
            StructuralLifecycle.ObservationId(D3.Symbol, bar, P.PolicyHash, null));
    }

    [Fact]
    public async Task ATransitionObservationIsCoreAndSurvivesAnExhaustedRoutineBudget()
    {
        var policy = P with { ObservationDailyByteLimit = 4096, ObservationCoreReserveRatio = .5 };
        var store = new MemoryObservationStore { ForcedSize = 2200 };
        var writer = new StructureObservationWriter(store, policy);
        var transition = Record(StructureAnalysisService.DetailTransition, "INVALIDATED");
        var routine = Record("summary", "WAIT");

        Assert.True(StructureObservationWriter.IsCore(transition));
        Assert.True(StructureObservationWriter.IsRoutine(routine));

        var droppedRoutine = await writer.AppendAsync(routine, default);
        var writtenTransition = await writer.AppendAsync(transition, default);

        Assert.False(droppedRoutine.Written);
        Assert.True(writtenTransition.Written);
        Assert.True(writtenTransition.Core);
        Assert.Contains(StructureObservationWriter.LimitWarning,
            StructureObservationWriter.StorageWarnings(writtenTransition));
    }

    static StructureObservationRecord Record(string detail, string summary) =>
        new(StructuralLifecycle.ObservationId(D3.Symbol, D3.At(Fidelity.TriggerMinute), P.PolicyHash, summary),
            P.Version + "-shadow", D3.Symbol, D3.At(Fidelity.AnalysisMinute), D3.SessionStart,
            D3.At(Fidelity.AnalysisMinute), D3.At(Fidelity.AnalysisMinute), D3.At(Fidelity.TriggerMinute),
            P.PolicyHash, P.Version, "shadow", "v4", detail, StructureAnalysisStatus.Available, summary, null, null,
            null, null, [], [], []);
}

public sealed class StructureUnavailableStatusTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static StructureSnapshotBuild Build(Candle[] bars) =>
        StructureSnapshotFactory.Create(D3.Symbol, D3.Session, bars, D3.Daily(), 100.0, D3.At(45), D3.At(45), 7, P);

    [Fact]
    public void AHealthySymbolStaysAvailable()
    {
        var build = Build(D3.Candles(46));

        Assert.Equal(StructureAnalysisStatus.Available, build.Status);
        Assert.Empty(build.Quality.BlockersForCandidate);
    }

    [Fact]
    public void AMinuteBarGapMakesTheAnalysisStatusUnavailable()
    {
        var gapped = D3.Candles(46).Where(x => x.Timestamp != D3.At(20)).ToArray();

        var build = Build(gapped);

        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
        Assert.Equal(StructureAnalysisStatus.Unavailable, build.Status);
        Assert.NotNull(build.Snapshot);
    }

    [Fact]
    public void ConflictingBarsAlsoMakeTheAnalysisStatusUnavailable()
    {
        var conflicting = D3.Candles(46).ToList();
        conflicting.Add(new Candle(D3.At(20), 1, 2, .5, 1.5, 10));

        var build = Build([.. conflicting]);

        Assert.Contains(StructureSnapshotFactory.BlockerBarConflict, build.Quality.BlockersForCandidate);
        Assert.Equal(StructureAnalysisStatus.Unavailable, build.Status);
    }

    [Fact]
    public void TooFewBarsAreStillWarmupNotUnavailable()
    {
        var build = StructureSnapshotFactory.Create(D3.Symbol, D3.Session, D3.Candles(5), D3.Daily(), 100.0,
            D3.At(4), D3.At(4), 7, P);

        Assert.Equal(StructureAnalysisStatus.Warmup, build.Status);
    }

    [Fact]
    public async Task AGappedSymbolIsReportedAsUnavailableThroughTheApi()
    {
        var clock = new MovableClock(D3.At(45));
        var runtime = new MonitorRuntimeState();
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(D3.Symbol, "테스트"));
        var service = D3.Service(StructureEngineMode.Shadow, new MemoryObservationStore(), runtime, clock, store);
        var generation = D3.StartedRuntime(runtime);
        var gapped = D3.Candles(46).Where(x => x.Timestamp != D3.At(20)).ToArray();

        await service.ObserveAsync(new StructureObservationRequest(D3.Symbol, generation, D3.Session, gapped,
            D3.Daily(), 100.0, D3.At(45)), default);

        var (status, response) = await service.GetAsync(D3.Symbol, default);
        Assert.Equal(200, status);
        Assert.Equal(StructureAnalysisStatus.Unavailable, response!.Status);
        Assert.NotNull(response.Analysis);
    }

    [Fact]
    public async Task AFailedEvaluationIsUnavailableAndDistinctFromWarmup()
    {
        var clock = new MovableClock(D3.At(45));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(D3.Symbol, "테스트"));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store);
        var generation = D3.StartedRuntime(runtime);

        var warmup = await service.GetAsync(D3.Symbol, default);
        Assert.Equal(StructureAnalysisStatus.Warmup, warmup.Response!.Status);

        observations.AppendFailure = new InvalidOperationException("disk");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ObserveAsync(D3.Request(generation, 46), default));

        var failed = await service.GetAsync(D3.Symbol, default);
        Assert.Equal(StructureAnalysisStatus.Unavailable, failed.Response!.Status);
        Assert.NotNull(failed.Response.Message);
        Assert.NotEqual(warmup.Response.Message, failed.Response.Message);

        var summary = JsonDocument.Parse(JsonSerializer.Serialize(service.Summary([D3.Symbol]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.GetProperty("symbols")[0];
        Assert.Equal(StructureAnalysisStatus.Unavailable, summary.GetProperty("status").GetString());
        Assert.Contains(StructureAnalysisService.WarningEvaluationFailed,
            summary.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()));

        observations.AppendFailure = null;
        await service.ObserveAsync(D3.Request(generation, 46), default);

        var recovered = await service.GetAsync(D3.Symbol, default);
        Assert.Equal(StructureAnalysisStatus.Available, recovered.Response!.Status);
    }
}

public sealed class StructurePullbackTrendStateTraceTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    [Fact]
    public void TheRealTrendPathProducesThePullbackWithoutTheTrendStateNote()
    {
        var trend = Fidelity.RealTrend(P);
        Assert.True(trend.State is TrendState.Up or TrendState.Transition);

        var result = Fidelity.Detect(trend, P);

        Assert.Equal(SetupKind.Pullback, Assert.Single(result.Candidates).Kind);
        Assert.DoesNotContain(SetupDetector.NotePullbackTrendState, result.Warnings);
    }

    [Theory]
    [InlineData(TrendState.Range)]
    [InlineData(TrendState.Down)]
    [InlineData(TrendState.Unknown)]
    public void ATrendStateRejectionLeavesTheReasonInTheObservationWarnings(TrendState state)
    {
        var real = Fidelity.RealTrend(P);
        var result = Fidelity.Detect(real with { State = state }, P);

        Assert.Contains(SetupDetector.NotePullbackTrendState, result.Warnings);
        Assert.DoesNotContain(result.Candidates, x => x.Kind == SetupKind.Pullback);
    }

    [Fact]
    public void AZoneThatNeverQualifiedAsAPullbackLeavesNoTrendStateReason()
    {
        var real = Fidelity.RealTrend(P);
        var (zones, _, bars, atr) = Fidelity.Structure(P);
        var analysisAsOf = D3.At(Fidelity.AnalysisMinute);

        var result = SetupDetector.Detect(SetupDetectionRequest.Create(D3.Symbol, D3.SessionStart, D3.SessionEnd,
            analysisAsOf, analysisAsOf, bars, zones, ImmutableArray<TouchEpisode>.Empty,
            real with { State = TrendState.Down }, atr, (decimal)Fidelity.TriggerPrice, analysisAsOf), P);

        Assert.DoesNotContain(SetupDetector.NotePullbackTrendState, result.Warnings);
    }

    [Fact]
    public async Task TheTrendStateReasonReachesThePublishedObservationNotes()
    {
        var clock = new MovableClock(D3.At(Fidelity.AnalysisMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(Fidelity.Request(generation, Fidelity.AnalysisMinute, Fidelity.TriggerPrice),
            default);

        Assert.True(service.TryGetPublished(D3.Symbol, out var view));
        Assert.NotNull(view.Trend);
        var notes = JsonDocument.Parse(observations.AllLines.Last()).RootElement.GetProperty("notes")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(view.Notes, notes);
    }
}
