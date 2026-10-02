using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Astra.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class SimulationSummaryCumulativeTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 30, 0, TimeSpan.Zero);
    static readonly DateOnly Today = MarketRules.TradingDate(Now);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string _contentRoot = Directory.CreateTempSubdirectory("astra-sim-cumulative-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    static FrozenStructureContext Context(string policyHash) =>
        new("TEST|e", new FrozenPlanSnapshot("plan", "PULLBACK", 100m, 99.6m, 99.4m, 101.5m, "z1", 99.4m, 99.7m, "z2",
            101.5m, 101.9m, .05m, "atr", .02m, 1.3m, .8m, 1.6m, .6, .1m, .02m, .04m, false, "cost.1", "fill.1", Now, Now.AddMinutes(5),
            "v5-structure.1", policyHash, [], "계획"), "UP", 40, 60, Now, Now, StructuralSimulation.ExitPolicyVersion);

    static SimTrade Trade(string id, double pnl, FrozenStructureContext? context, DateTimeOffset? enteredAt = null) =>
        new(id, "TEST", "PULLBACK", enteredAt ?? Now, 100, 101.5, 99.4, null, null, "TARGET", 101.5, Now.AddMinutes(30), pnl, 101.5,
            Logic: "v5-structure.1", Structure: context);

    [Fact]
    public void PolicySegmentSeparatesCurrentOtherAndUnknownHashes()
    {
        var trades = new[]
        {
            Trade("a", 1.0, Context("hash-now"), Now.AddHours(-2)),
            Trade("b", -0.5, Context("hash-now"), Now.AddHours(-1)),
            Trade("c", 2.0, Context("hash-old")),
            Trade("d", 3.0, null),
        };

        var segment = SimulationReportQueryService.PolicySegment(trades, "hash-now");

        Assert.Equal("hash-now", segment.PolicyHash);
        Assert.Equal(2, segment.Stats.Total);
        Assert.Equal(50, segment.Stats.WinRate);
        Assert.Equal(1, segment.OtherPolicyCount);
        Assert.Equal(1, segment.UnknownPolicyCount);
        Assert.Equal(Now.AddHours(-2), segment.FirstEnteredAt);
    }

    [Fact]
    public void PolicySegmentWithoutMatchingTradesReportsNullRatesNotZero()
    {
        var segment = SimulationReportQueryService.PolicySegment([Trade("d", 3.0, null)], "hash-now");

        Assert.Equal(0, segment.Stats.Total);
        Assert.Null(segment.Stats.WinRate);
        Assert.Equal(1, segment.UnknownPolicyCount);
        Assert.Null(segment.FirstEnteredAt);
    }

    [Fact]
    public async Task ReportCarriesCurrentPolicyAndHistoryAsAdditiveFields()
    {
        var store = new RecordingStore();
        store.Seed(Trade("a", 1.0, Context(StructurePolicy.Default.PolicyHash)));
        var history = new EntryObservationHistoryQueryService(new MemoryObservationStore(), new MovableClock(Now), StructurePolicy.Default);

        var report = await new SimulationReportQueryService(store, new MovableClock(Now), null, StructurePolicy.Default, history).GetAsync();

        Assert.Equal(1, report.CurrentPolicy!.Stats.Total);
        Assert.Equal(StructurePolicy.Default.PolicyHash, report.EntryObservationHistory!.PolicyHash);
        Assert.Equal(0, report.EntryObservationHistory.Today.Observations);
    }

    static string Observation(string symbol, DateTimeOffset observedAt, string policyHash, string state, string eventId,
        string[]? rejectionCodes = null, string[]? blockersForReady = null, string bar = "2026-10-02T14:29:00+00:00",
        string kind = "BREAKOUT") =>
        JsonSerializer.Serialize(new
        {
            observationId = Guid.NewGuid().ToString("N"), symbol, observedAt, policyHash, lastCompletedBarStart = bar,
            candidates = eventId.Length == 0 ? Array.Empty<object>() : new object[]
            {
                new { eventId, kind, state, triggerBarStart = bar, rejectionCodes = rejectionCodes ?? [] }
            },
            quality = new { blockersForCandidate = Array.Empty<string>(), blockersForReady = blockersForReady ?? [] }
        }, Json);

    static EntryObservationHistoryQueryService History(MemoryObservationStore store) =>
        new(store, new MovableClock(Now), StructurePolicy.Default);

    [Fact]
    public async Task EmptyObservationFileYieldsZeroCountsAndNoWarning()
    {
        var report = await History(new MemoryObservationStore()).GetAsync(CancellationToken.None);

        Assert.Equal(0, report.Today.Observations);
        Assert.Equal(0, report.Today.Candidates);
        Assert.Empty(report.RecentCandidates);
        Assert.Null(report.Warning);
        Assert.Equal(Today, report.Today.From);
    }

    [Fact]
    public async Task AggregatesDistinctCandidatesStatesAndTopReasons()
    {
        var store = new MemoryObservationStore();
        var hash = StructurePolicy.Default.PolicyHash;
        var file = StructureObservationWriter.FileName(Today);
        store.Files[file] =
        [
            Observation("AAA", Now.AddMinutes(-30), hash, "WAIT", "e1", blockersForReady: ["STALE_QUOTE"]),
            Observation("AAA", Now.AddMinutes(-20), hash, "READY", "e1"),
            Observation("AAA", Now.AddMinutes(-10), hash, "ENTERED", "e1"),
            Observation("BBB", Now.AddMinutes(-9), hash, "REJECTED", "e2", ["V5_ENTRY_NET_R", "STALE_QUOTE"]),
            Observation("BBB", Now.AddMinutes(-8), hash, "REJECTED", "e2", ["V5_ENTRY_NET_R", "STALE_QUOTE"]),
            Observation("CCC", Now.AddMinutes(-7), hash, "WAIT", ""),
        ];

        var report = await History(store).GetAsync(CancellationToken.None);

        Assert.Equal(6, report.Today.Observations);
        Assert.Equal(3, report.Today.Symbols);
        Assert.Equal(2, report.Today.Candidates);
        Assert.Equal(1, report.Today.Ready);
        Assert.Equal(1, report.Today.Entered);
        Assert.Equal(1, report.Today.Rejected);
        Assert.Equal("STALE_QUOTE", report.Today.TopReasons[0].Code);
        Assert.Equal(2, report.Today.TopReasons[0].Count);
        Assert.Equal(1, Assert.Single(report.Today.TopReasons, x => x.Code == "V5_ENTRY_NET_R").Count);
        var rejected = Assert.Single(report.RecentCandidates, x => x.EventId == "e2");
        Assert.Equal("REJECTED", rejected.State);
        Assert.Equal(["STALE_QUOTE", "V5_ENTRY_NET_R"], rejected.RejectionCodes);
        Assert.Equal("ENTERED", Assert.Single(report.RecentCandidates, x => x.EventId == "e1").State);
    }

    [Fact]
    public async Task ShortCandidatesAreReportedSeparatelyWhenPolicyCannotEnterShort()
    {
        var store = new MemoryObservationStore();
        var hash = StructurePolicy.Default.PolicyHash;
        store.Files[StructureObservationWriter.FileName(Today)] =
        [
            Observation("AAA", Now.AddMinutes(-9), hash, "REJECTED", "long", ["NO_TARGET_STRUCTURE"]),
            Observation("AAA", Now.AddMinutes(-8), hash, "REJECTED", "short", ["SHORT_BORROW_COST_MISSING", "NO_TARGET_STRUCTURE"],
                kind: "PULLBACK_SHORT"),
        ];

        var report = await History(store).GetAsync(CancellationToken.None);

        Assert.Equal(1, report.Today.Candidates);
        Assert.Equal(1, report.Today.Rejected);
        Assert.DoesNotContain(report.Today.TopReasons, x => x.Code == "SHORT_BORROW_COST_MISSING");
        Assert.Equal(1, Assert.Single(report.Today.TopReasons, x => x.Code == "NO_TARGET_STRUCTURE").Count);
        Assert.Equal(1, report.Today.NonEntrySideCandidates);
        Assert.Equal(1, Assert.Single(report.Today.NonEntrySideTopReasons!, x => x.Code == "SHORT_BORROW_COST_MISSING").Count);
        Assert.Contains(report.RecentCandidates, x => x.EventId == "short");
    }

    [Fact]
    public void LegacyHistoryWindowDefaultsNewSideFieldsToEmptyValues()
    {
        var window = new EntryObservationHistoryWindow("오늘", Today, Today, 1, 1, 1, 1, 0, 0, 1, [], 0);
        var json = JsonSerializer.SerializeToElement(window, Json);

        Assert.Equal(0, json.GetProperty("nonEntrySideCandidates").GetInt32());
        Assert.Equal(JsonValueKind.Array, json.GetProperty("nonEntrySideTopReasons").ValueKind);
        Assert.Empty(json.GetProperty("nonEntrySideTopReasons").EnumerateArray());
    }

    [Fact]
    public async Task CorruptLinesAreCountedAndSkippedWithoutFailingTheQuery()
    {
        var store = new MemoryObservationStore();
        var file = StructureObservationWriter.FileName(Today);
        store.Files[file] = ["{not json", "", "[1,2]", Observation("AAA", Now, StructurePolicy.Default.PolicyHash, "WAIT", "e1")];

        var report = await History(store).GetAsync(CancellationToken.None);

        Assert.Equal(2, report.Today.CorruptLines);
        Assert.Equal(1, report.Today.Observations);
        Assert.Null(report.Warning);
    }

    [Fact]
    public async Task CurrentPolicyWindowSpansPreviousDaysAndExcludesOtherHashes()
    {
        var store = new MemoryObservationStore();
        var hash = StructurePolicy.Default.PolicyHash;
        store.Files[StructureObservationWriter.FileName(Today.AddDays(-1))] =
        [
            Observation("AAA", Now.AddDays(-1), hash, "REJECTED", "y1", ["V5_ENTRY_NET_R"]),
            Observation("AAA", Now.AddDays(-1).AddMinutes(1), "hash-old", "REJECTED", "y2", ["V5_ENTRY_NET_R"]),
        ];
        store.Files[StructureObservationWriter.FileName(Today.AddDays(-EntryObservationHistoryQueryService.HistoryDays))] =
            [Observation("AAA", Now.AddDays(-8), hash, "REJECTED", "old", ["V5_ENTRY_NET_R"])];
        store.Files[StructureObservationWriter.FileName(Today)] = [Observation("BBB", Now, hash, "WAIT", "t1")];

        var report = await History(store).GetAsync(CancellationToken.None);

        Assert.Equal(1, report.Today.Candidates);
        Assert.Equal(2, report.CurrentPolicy.Candidates);
        Assert.Equal(1, report.CurrentPolicy.Rejected);
        Assert.Equal(2, report.CurrentPolicy.Observations);
        Assert.Equal(Today.AddDays(-(EntryObservationHistoryQueryService.HistoryDays - 1)), report.CurrentPolicy.From);
        Assert.DoesNotContain(report.RecentCandidates, x => x.EventId == "old");
        Assert.Contains(report.RecentCandidates, x => x.EventId == "y2");
    }

    [Fact]
    public async Task AppendedLinesAreReadIncrementallyAndUnchangedFilesAreNotReread()
    {
        var store = new MemoryObservationStore();
        var hash = StructurePolicy.Default.PolicyHash;
        var file = StructureObservationWriter.FileName(Today);
        store.Files[file] = [Observation("AAA", Now.AddMinutes(-5), hash, "WAIT", "e1")];
        var service = History(store);

        var first = await service.GetAsync(CancellationToken.None);
        var afterFirst = store.Interactions;
        var second = await service.GetAsync(CancellationToken.None);
        var afterSecond = store.Interactions;
        store.Files[file].Add(Observation("AAA", Now, hash, "REJECTED", "e1", ["V5_ENTRY_NET_R"]));
        var third = await service.GetAsync(CancellationToken.None);

        Assert.Equal(1, first.Today.Observations);
        Assert.Equal(1, second.Today.Observations);
        Assert.Equal(1, afterSecond - afterFirst);
        Assert.Equal(2, third.Today.Observations);
        Assert.Equal("REJECTED", Assert.Single(third.RecentCandidates).State);
    }

    [Fact]
    public async Task ReadFailureKeepsLastAggregateAndSurfacesWarning()
    {
        var inner = new MemoryObservationStore();
        var file = StructureObservationWriter.FileName(Today);
        inner.Files[file] = [Observation("AAA", Now.AddMinutes(-5), StructurePolicy.Default.PolicyHash, "WAIT", "e1")];
        var failing = new FailingObservationStore(inner);
        var service = new EntryObservationHistoryQueryService(failing, new MovableClock(Now), StructurePolicy.Default);

        var first = await service.GetAsync(CancellationToken.None);
        failing.Fail = true;
        inner.Files[file].Add(Observation("AAA", Now, StructurePolicy.Default.PolicyHash, "READY", "e1"));
        var second = await service.GetAsync(CancellationToken.None);

        Assert.Null(first.Warning);
        Assert.Equal(1, second.Today.Observations);
        Assert.Contains(file, second.Warning);
    }

    static SimTrade ClosedTrade(string id) =>
        new(id, "AAPL", "SETUP", Now, 100, 102, 99, null, null, "TARGET", 102, Now.AddHours(1), 2.0, 101);

    [Fact]
    public async Task ResetAppendsAuditEntryWithRequesterAndBackup()
    {
        var env = new FakeEnv(_contentRoot);
        Directory.CreateDirectory(Path.Combine(_contentRoot, "App_Data"));
        await File.WriteAllTextAsync(Path.Combine(_contentRoot, "App_Data", "simtrades.json"),
            JsonSerializer.Serialize(new List<SimTrade> { ClosedTrade("a"), ClosedTrade("b") }, Json));
        var audit = new SimulationResetAuditLog(env, NullLogger<SimulationResetAuditLog>.Instance);
        var service = new SimulationResetService(new LocalStore(env), new MovableClock(Now), audit);

        var result = await service.ResetAsync(false, new SimulationResetRequester("127.0.0.1", "curl/8.0"));

        var lines = await File.ReadAllLinesAsync(Path.Combine(_contentRoot, "App_Data", SimulationResetAuditLog.FileName));
        var entry = JsonSerializer.Deserialize<SimulationResetAuditEntry>(Assert.Single(lines), Json)!;
        Assert.Equal(2, entry.Removed);
        Assert.Equal(result.Backup, entry.Backup);
        Assert.Equal("127.0.0.1", entry.RemoteAddress);
        Assert.Equal("curl/8.0", entry.UserAgent);
        Assert.False(entry.IncludeOpen);
        Assert.Equal(Now, entry.At);
    }

    [Fact]
    public async Task ResetOnEmptyHistoryStillAppendsAuditEntry()
    {
        var env = new FakeEnv(_contentRoot);
        var audit = new SimulationResetAuditLog(env, NullLogger<SimulationResetAuditLog>.Instance);
        var service = new SimulationResetService(new LocalStore(env), new MovableClock(Now), audit);

        await service.ResetAsync(true, new SimulationResetRequester(null, null));

        var lines = await File.ReadAllLinesAsync(Path.Combine(_contentRoot, "App_Data", SimulationResetAuditLog.FileName));
        var entry = JsonSerializer.Deserialize<SimulationResetAuditEntry>(Assert.Single(lines), Json)!;
        Assert.Equal(0, entry.Removed);
        Assert.Null(entry.Backup);
        Assert.True(entry.IncludeOpen);
    }

    [Fact]
    public async Task AuditFailureDoesNotChangeResetResult()
    {
        var store = new RecordingStore();
        store.Seed(ClosedTrade("a"));
        var diagnostics = new SilentDiagnostics();
        var service = new SimulationResetService(store, new MovableClock(Now), new ThrowingAuditLog(), diagnostics);

        var result = await service.ResetAsync(false, new SimulationResetRequester("::1", "ua"));

        Assert.Equal(1, result.Removed);
        Assert.Empty(store.Trades);
        Assert.Equal("sim-reset-audit", Assert.Single(diagnostics.Failures).Scope);
    }

    sealed class ThrowingAuditLog : ISimulationResetAuditLog
    {
        public Task AppendAsync(SimulationResetAuditEntry entry) => throw new IOException("disk full");
    }

    sealed class FailingObservationStore(IStructureObservationStore inner) : IStructureObservationStore
    {
        public bool Fail { get; set; }
        public Task<long> SizeAsync(string file, CancellationToken ct) => Fail ? throw new IOException("locked") : inner.SizeAsync(file, ct);
        public Task<IReadOnlyList<string>> ReadLinesAsync(string file, CancellationToken ct) => Fail ? throw new IOException("locked") : inner.ReadLinesAsync(file, ct);
        public Task AppendAsync(string file, string line, CancellationToken ct) => inner.AppendAsync(file, line, ct);
        public Task<string?> ReadTextAsync(string file, CancellationToken ct) => inner.ReadTextAsync(file, ct);
        public Task WriteTextAsync(string file, string content, CancellationToken ct) => inner.WriteTextAsync(file, content, ct);
    }

    sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Astra.Server.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
