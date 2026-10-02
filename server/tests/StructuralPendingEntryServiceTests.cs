using System.Text.Json;
using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructuralPendingEntryServiceTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-20T13:30:00Z");

    static StoredPendingStructuralEntry Entry(string id = "event-1") => new(
        new PendingEntry(id, "SOXL", TradeSide.Long, Start, Start.AddMinutes(1), Start.AddMinutes(5), 90, 110, 100, "plan", "policy"),
        null!, Start);

    [Fact]
    public async Task QueueSurvivesNewServiceInstanceAndClaimIsSingleUse()
    {
        var store = new MemoryStore();
        await new StructuralPendingEntryService(store).QueueAsync(Entry());
        var restarted = new StructuralPendingEntryService(store);
        Assert.NotNull(await restarted.GetAsync("SOXL"));
        Assert.NotNull(await restarted.ClaimAsync("SOXL", Start.AddMinutes(1)));
        Assert.Null(await restarted.ClaimAsync("SOXL", Start.AddMinutes(1)));
    }

    [Fact]
    public async Task ExpiredClaimRemovesPendingWithoutReturningIt()
    {
        var store = new MemoryStore();
        var service = new StructuralPendingEntryService(store);
        await service.QueueAsync(Entry());
        Assert.Null(await service.ClaimAsync("SOXL", Start.AddMinutes(6)));
        Assert.Null(await service.GetAsync("SOXL"));
    }

    [Fact]
    public async Task PersistedConfirmationCanBeReclaimedAfterPendingExpiry()
    {
        var store = new MemoryStore();
        var service = new StructuralPendingEntryService(store);
        var entry = Entry("crash-retry");
        await service.QueueAsync(entry);
        var confirmation = new EntryConfirmation(entry.Pending, PendingEntryDecision.Confirmed,
            Start.AddMinutes(1), 100.5, .01, "LIVE_CONFIRMATION_BAR_CLOSE", "OBSERVED");
        await service.SaveConfirmationAsync("SOXL", confirmation, Start.AddMinutes(1));

        var recovered = await service.ClaimAsync("SOXL", Start.AddMinutes(7));
        Assert.NotNull(recovered);
        Assert.Equal(100.5, recovered!.Confirmation!.FillPrice);
    }

    [Fact]
    public async Task ClaimAndPersistConfirmationCommitsEvidenceAndLeaseTogether()
    {
        var store = new MemoryStore();
        var service = new StructuralPendingEntryService(store);
        var entry = Entry("atomic");
        await service.QueueAsync(entry);
        var confirmation = new EntryConfirmation(entry.Pending, PendingEntryDecision.Confirmed,
            Start.AddMinutes(1), 100.5, .01, "LIVE_CONFIRMATION_BAR_CLOSE", "OBSERVED");

        var claimed = await service.ClaimAndPersistConfirmationAsync("SOXL", confirmation, Start.AddMinutes(1));

        Assert.NotNull(claimed);
        Assert.Equal(100.5, claimed!.Confirmation!.FillPrice);
        Assert.NotNull(claimed.ProcessingUntil);
        Assert.Null(await service.ClaimAndPersistConfirmationAsync("SOXL", confirmation, Start.AddMinutes(1)));
    }

    [Fact]
    public void LivePendingStateIsIndependentOfPreferredCandidate()
    {
        var pending = Entry().Pending;
        Assert.Equal(StructureAnalysisService.LivePendingDecision.Wait,
            StructureAnalysisService.ResolvePendingState(pending, [new Candle(Start, 100, 101, 99, 100, 1)]).Decision);
        Assert.Equal(StructureAnalysisService.LivePendingDecision.Confirm,
            StructureAnalysisService.ResolvePendingState(pending, [new Candle(Start.AddMinutes(1), 100, 101, 99, 100, 1)]).Decision);
        Assert.Equal(StructureAnalysisService.LivePendingDecision.Missed,
            StructureAnalysisService.ResolvePendingState(pending, [new Candle(Start.AddMinutes(2), 100, 101, 99, 100, 1)]).Decision);
    }

    [Fact]
    public async Task ProductionPendingPathConfirmsWithoutPreferredCandidateAndIsIdempotent()
    {
        var store = new MemoryStore();
        var pending = new StructuralPendingEntryService(store);
        var context = StructuralSimulation.Freeze(
            StructuralPlanner.Evaluate(D2.ExampleA(), D6.WiringPolicy).Plan!,
            "pending-production", "UP", 12, 55, Start, Start);
        await pending.QueueAsync(new StoredPendingStructuralEntry(
            new PendingEntry("pending-production", "SOXL", TradeSide.Long, Start,
                Start.AddMinutes(1), Start.AddMinutes(5), (double)context.PlanSnapshot.Stop,
                (double)context.PlanSnapshot.Target, (double)context.PlanSnapshot.EntryReference,
                context.PlanSnapshot.PlanId, context.PlanSnapshot.PolicyHash), context, Start));

        var entries = new CountingEntryPort(new StructuralTradeEntryService(store, D6.WiringPolicy));
        var service = BuildPendingService(store, pending, entries);
        var snapshot = new StructureSnapshot("SOXL", Start, Start.AddHours(6), Start.AddMinutes(1),
            100, Start.AddMinutes(1), ImmutableArray<Candle>.Empty, ImmutableArray<Candle>.Empty,
            null, null, 1);
        var request = new StructureObservationRequest("SOXL", 1, D6.Session, [], [], 100, Start.AddMinutes(1));

        var result = await service.TryEnterPreferredAsync(request, [], null, snapshot, null,
            Start.AddMinutes(2), [new Candle(Start.AddMinutes(1), 100, 101, 99, 100.5, 1)], default);

        Assert.True(result!.Entered, result.Note);
        Assert.Equal(1, entries.Calls);
        var trade = Assert.Single(await store.Read("simtrades.json", new List<SimTrade>()));
        Assert.Equal("OBSERVED_CONFIRMATION_BAR", trade.Execution!.EntryMinuteCoverage);
        Assert.Equal("pending-production", trade.Structure!.EntryEventId);

        var retry = await service.TryEnterPreferredAsync(request, [], null, snapshot, null,
            Start.AddMinutes(2), [new Candle(Start.AddMinutes(1), 100, 101, 99, 100.5, 1)], default);
        Assert.Null(retry);
        Assert.Equal(1, entries.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionPendingPathRecordsBenchmarkAvailabilityOnTheCommittedTrade(bool benchmarkPresent)
    {
        var store = new MemoryStore();
        var pending = new StructuralPendingEntryService(store);
        var policy = D6.WiringPolicy with
        {
            EnableTwoRFeeBreakEvenStop = true,
            CapStructuralTargetAtTwoR = true,
            EnableHalfRFeeBreakEvenStopForPositiveBenchmark = true
        };
        var context = StructuralSimulation.Freeze(
            StructuralPlanner.Evaluate(D2.ExampleA(), D6.WiringPolicy).Plan!,
            "pending-benchmark-observability", "UP", 12, 55, Start, Start, policy);
        await pending.QueueAsync(new StoredPendingStructuralEntry(
            new PendingEntry("pending-benchmark-observability", "SOXL", TradeSide.Long, Start,
                Start.AddMinutes(1), Start.AddMinutes(5), (double)context.PlanSnapshot.Stop,
                (double)context.PlanSnapshot.Target, (double)context.PlanSnapshot.EntryReference,
                context.PlanSnapshot.PlanId, context.PlanSnapshot.PolicyHash), context, Start));
        var entries = new CountingEntryPort(new StructuralTradeEntryService(store, policy));
        var benchmark = benchmarkPresent ? new FakeBenchmark(BenchmarkBars(100, 100.5)) : null;
        var service = BuildPendingService(store, pending, entries, policy, benchmark);
        var snapshot = new StructureSnapshot("SOXL", Start, Start.AddHours(6), Start.AddMinutes(1),
            100, Start.AddMinutes(1), ImmutableArray<Candle>.Empty, ImmutableArray<Candle>.Empty,
            null, null, 1);
        var request = new StructureObservationRequest("SOXL", 1, D6.Session, [], [], 100, Start.AddMinutes(1));

        var result = await service.TryEnterPreferredAsync(request, [], null, snapshot, null,
            Start.AddMinutes(2), [new Candle(Start.AddMinutes(1), 100, 101, 99, 100.5, 1)], default);

        Assert.True(result!.Entered, result.Note);
        var trade = Assert.Single(await store.Read("simtrades.json", new List<SimTrade>()));
        Assert.Equal(trade.Id, result.Trade!.Id);
        var tags = Assert.IsType<EntryBenchmarkTags>(trade.Structure!.Benchmark);
        var notes = StructureAnalysisService.EntryObservabilityNotes(result.Trade).ToArray();
        if (benchmarkPresent)
        {
            Assert.Equal(StructuralSimulation.BenchmarkAvailable, tags.Status);
            Assert.Equal(.5, tags.ReturnPercent!.Value, 6);
            Assert.Equal(StructuralSimulation.HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion,
                trade.Structure.StructuralExitPolicyVersion);
            Assert.Contains(StructureAnalysisService.NoteEntryBenchmarkAvailable, notes);
        }
        else
        {
            Assert.Equal(StructuralSimulation.BenchmarkUnavailable, tags.Status);
            Assert.Null(tags.ReturnPercent);
            Assert.Equal(context.StructuralExitPolicyVersion, trade.Structure.StructuralExitPolicyVersion);
            Assert.Contains(StructureAnalysisService.NoteEntryBenchmarkUnavailable, notes);
        }
        Assert.Contains(StructureAnalysisService.NoteExitPolicyPrefix + trade.Structure.StructuralExitPolicyVersion, notes);
    }

    [Fact]
    public async Task ProductionPendingPathBlocksNegativeBenchmarkOnlyForLongRebound()
    {
        var store = new MemoryStore();
        var pending = new StructuralPendingEntryService(store);
        var context = StructuralSimulation.Freeze(
            StructuralPlanner.Evaluate(D2.ExampleA() with { Kind = "REBOUND" }, D6.WiringPolicy).Plan!,
            "pending-benchmark", "UP", 12, 55, Start, Start);
        await pending.QueueAsync(new StoredPendingStructuralEntry(
            new PendingEntry("pending-benchmark", "SOXL", TradeSide.Long, Start,
                Start.AddMinutes(1), Start.AddMinutes(5), (double)context.PlanSnapshot.Stop,
                (double)context.PlanSnapshot.Target, (double)context.PlanSnapshot.EntryReference,
                context.PlanSnapshot.PlanId, context.PlanSnapshot.PolicyHash), context, Start));
        var entries = new CountingEntryPort(new StructuralTradeEntryService(store, D6.WiringPolicy));
        var policy = D6.WiringPolicy with { RequirePositiveBenchmarkForRebound = true };
        var benchmark = new FakeBenchmark(BenchmarkBars(100, 99));
        var service = BuildPendingService(store, pending, entries, policy, benchmark);
        var snapshot = new StructureSnapshot("SOXL", Start, Start.AddHours(6), Start.AddMinutes(1),
            100, Start.AddMinutes(1), ImmutableArray<Candle>.Empty, ImmutableArray<Candle>.Empty,
            null, null, 1);
        var request = new StructureObservationRequest("SOXL", 1, D6.Session, [], [], 100, Start.AddMinutes(1));

        var result = await service.TryEnterPreferredAsync(request, [], null, snapshot, null,
            Start.AddMinutes(2), [new Candle(Start.AddMinutes(1), 100, 101, 99, 100.5, 1)], default);

        Assert.False(result!.Entered);
        Assert.Equal(BenchmarkEntryGate.Negative, result.Note);
        Assert.Equal(0, entries.Calls);
        Assert.Null(await pending.GetAsync("SOXL"));
    }

    [Fact]
    public async Task ProductionPendingPathWaitsBeforeConfirmationAndMissesAfterIt()
    {
        var store = new MemoryStore();
        var pending = new StructuralPendingEntryService(store);
        await pending.QueueAsync(Entry("pending-state") with { Pending = Entry("pending-state").Pending with { Symbol = "SOXL" } });
        var entries = new CountingEntryPort(new StructuralTradeEntryService(store, D6.WiringPolicy));
        var service = BuildPendingService(store, pending, entries);
        var snapshot = new StructureSnapshot("SOXL", Start, Start.AddHours(6), Start, 100, Start,
            ImmutableArray<Candle>.Empty, ImmutableArray<Candle>.Empty, null, null, 1);
        var request = new StructureObservationRequest("SOXL", 1, D6.Session, [], [], 100, Start);

        var waiting = await service.TryEnterPreferredAsync(request, [], null, snapshot, null, Start,
            [new Candle(Start, 100, 101, 99, 100, 1)], default);
        Assert.False(waiting!.Entered);
        Assert.Contains("WAITING", waiting.Note);
        Assert.Equal(0, entries.Calls);

        var missed = await service.TryEnterPreferredAsync(request, [], null, snapshot, null, Start.AddMinutes(2),
            [new Candle(Start.AddMinutes(2), 100, 101, 99, 100, 1)], default);
        Assert.False(missed!.Entered);
        Assert.Contains("MISSED", missed.Note);
        Assert.Equal(0, entries.Calls);
        Assert.Null(await pending.GetAsync("SOXL"));
    }

    [Fact]
    public async Task 돌파_논거가_확인봉에서_무효화되면_대기를_즉시_제거한다()
    {
        var store = new MemoryStore();
        var pending = new StructuralPendingEntryService(store);
        var rejected = Entry("invalidated-breakout") with
        {
            Pending = Entry("invalidated-breakout").Pending with
            {
                SetupKind = "BREAKOUT",
                ConfirmationBoundary = 100,
                ConfirmationPolicyVersion = "hold-breakout-boundary.1"
            }
        };
        await pending.QueueAsync(rejected);
        var entries = new CountingEntryPort(new StructuralTradeEntryService(store, D6.WiringPolicy));
        var service = BuildPendingService(store, pending, entries);
        var snapshot = new StructureSnapshot("SOXL", Start, Start.AddHours(6), Start.AddMinutes(1),
            100, Start.AddMinutes(1), ImmutableArray<Candle>.Empty, ImmutableArray<Candle>.Empty,
            null, null, 1);
        var request = new StructureObservationRequest("SOXL", 1, D6.Session, [], [], 100, Start.AddMinutes(1));

        var result = await service.TryEnterPreferredAsync(request, [], null, snapshot, null,
            Start.AddMinutes(2), [new Candle(Start.AddMinutes(1), 101, 102, 99, 100, 1)], default);

        Assert.False(result!.Entered);
        Assert.Contains("REJECTED", result.Note);
        Assert.Equal(0, entries.Calls);
        Assert.Null(await pending.GetAsync("SOXL"));
    }

    static StructureAnalysisService BuildPendingService(MemoryStore store,
        StructuralPendingEntryService pending, CountingEntryPort entries, StructurePolicy? policy = null,
        IBenchmarkBarSource? benchmark = null)
    {
        var selected = policy ?? D6.WiringPolicy;
        return new(store, new StructureObservationWriter(new MemoryObservationStore(), selected),
            new MonitorRuntimeState(), TimeProvider.System, new SilentDiagnostics(),
            new StructureEngineOptions(StructureEngineMode.Active), selected, entries,
            pendingEntries: pending, benchmark: benchmark);
    }

    static Candle[] BenchmarkBars(double firstClose, double lastClose) => Enumerable.Range(0, 16)
        .Select(i =>
        {
            var close = i == 0 ? firstClose : i == 15 ? lastClose : firstClose;
            return new Candle(Start.AddMinutes(-14 + i), close, close, close, close, 1);
        }).ToArray();

    sealed class FakeBenchmark(IReadOnlyList<Candle> bars) : IBenchmarkBarSource
    {
        public string Symbol => "QQQ";
        public IReadOnlyList<Candle> Bars => bars;
    }

    sealed class CountingEntryPort(IStructuralTradeEntries inner) : IStructuralTradeEntries
    {
        public int Calls { get; private set; }
        public async Task<StructuralEntryResult> TryEnterAsync(StructuralEntryRequest request, CancellationToken ct)
        {
            Calls++;
            return await inner.TryEnterAsync(request, ct);
        }
    }

    sealed class MemoryStore : ILocalStore
    {
        readonly Dictionary<string, string> data = [];
        public Task<T> Read<T>(string file, T fallback) => Task.FromResult(data.TryGetValue(file, out var json)
            ? JsonSerializer.Deserialize<T>(json)! : fallback);
        public Task Write<T>(string file, T value) { data[file] = JsonSerializer.Serialize(value); return Task.CompletedTask; }
        public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
        {
            var current = await Read(file, fallback); var changed = change(current);
            await Write(file, changed.Data); return changed.Result;
        }
    }
}
