using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain;
using Astra.Server.Domain.ScoreCore;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureScoreCoreAttachmentTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-10-02T14:30:00Z");

    static ScoreCoreShadowSnapshot Snapshot(string captureId, string status, DateTimeOffset asOf, string target = Fx.Symbol)
    {
        var score = new ScoreCoreSnapshot("score-1", target, ImpactTargetKind.Company, asOf,
            ScoreCoreAggregator.SchemaVersion, ScoreCorePolicy.ShadowV1.Version, "assessment-v1",
            ScoreCalibrationStatus.Uncalibrated, [], [], 0, 0, 0, status);
        return new(captureId, score, [new("assessed-news", "ready", 0, 0, null)],
            ImmutableArray<ScoreEvidenceExclusion>.Empty, status);
    }

    static EntryScoreCoreAttachment Attachment(bool enabled, AttachmentStore store)
        => new(new ScoreCoreOptions { Enabled = enabled }, store);

    [Fact]
    public async Task 비활성이면_disabled를_기록하고_저장소를_조회하지_않는다()
    {
        var store = new AttachmentStore { Latest = Snapshot("abc123abc123abc123abc123", "shadow", AsOf.AddMinutes(-5)) };

        var tags = await Attachment(false, store).ResolveAsync(Fx.Symbol, AsOf, default);

        Assert.Equal(EntryScoreCoreAttachment.StatusDisabled, tags.Status);
        Assert.Null(tags.CaptureId);
        Assert.Equal(ScoreCorePolicy.ShadowV1.Version, tags.PolicyVersion);
        Assert.Equal(0, store.FindLatestCalls);
    }

    [Fact]
    public async Task 활성이고_asOf_이전_snapshot이_있으면_captureId_상태_버전을_기록한다()
    {
        var snapshotAt = AsOf.AddMinutes(-5);
        var store = new AttachmentStore { Latest = Snapshot("abc123abc123abc123abc123", "shadow", snapshotAt) };

        var tags = await Attachment(true, store).ResolveAsync(Fx.Symbol.ToLowerInvariant(), AsOf, default);

        Assert.Equal("shadow", tags.Status);
        Assert.Equal("abc123abc123abc123abc123", tags.CaptureId);
        Assert.Equal(ScoreCorePolicy.ShadowV1.Version, tags.PolicyVersion);
        Assert.Equal(ScoreCoreAggregator.SchemaVersion, tags.SchemaVersion);
        Assert.Equal(snapshotAt, tags.SnapshotAsOf);
        Assert.Null(tags.Reason);
        Assert.Equal(1, store.FindLatestCalls);
        Assert.Equal((ImpactTargetKind.Company, Fx.Symbol), store.LastQuery);
    }

    [Fact]
    public async Task asOf_이후_snapshot만_있으면_not_found다()
    {
        var store = new AttachmentStore { Latest = Snapshot("abc123abc123abc123abc123", "shadow", AsOf.AddMinutes(1)) };

        var tags = await Attachment(true, store).ResolveAsync(Fx.Symbol, AsOf, default);

        Assert.Equal(EntryScoreCoreAttachment.StatusNotFound, tags.Status);
        Assert.Equal(EntryScoreCoreAttachment.ReasonSnapshotAfterAsOf, tags.Reason);
        Assert.Null(tags.CaptureId);
    }

    [Fact]
    public async Task snapshot이_없으면_not_found다()
    {
        var tags = await Attachment(true, new AttachmentStore()).ResolveAsync(Fx.Symbol, AsOf, default);

        Assert.Equal(EntryScoreCoreAttachment.StatusNotFound, tags.Status);
        Assert.Equal(EntryScoreCoreAttachment.ReasonNoSnapshot, tags.Reason);
    }

    [Fact]
    public async Task 저장소_예외는_unavailable로_기록되고_던지지_않는다()
    {
        var store = new AttachmentStore { Failure = new IOException("disk") };

        var tags = await Attachment(true, store).ResolveAsync(Fx.Symbol, AsOf, default);

        Assert.Equal(EntryScoreCoreAttachment.StatusUnavailable, tags.Status);
        Assert.Equal(nameof(IOException), tags.Reason);
    }

    [Fact]
    public async Task 취소는_호출자에게_전파된다()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new AttachmentStore { Latest = Snapshot("abc123abc123abc123abc123", "shadow", AsOf.AddMinutes(-5)) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Attachment(true, store).ResolveAsync(Fx.Symbol, AsOf, cancellation.Token));
    }

    [Fact]
    public void replay_참조는_unavailable과_과거_PIT_없음_사유를_담는다()
    {
        var tags = EntryScoreCoreAttachment.HistoricalReplay();

        Assert.Equal(EntryScoreCoreAttachment.StatusUnavailable, tags.Status);
        Assert.Equal(EntryScoreCoreAttachment.ReasonHistoricalReplay, tags.Reason);
        Assert.Equal(ScoreCorePolicy.ShadowV1.Version, tags.PolicyVersion);
        Assert.Null(tags.CaptureId);
    }

    static StructuralEntryRequest Request(string eventId, EntryScoreCoreTags? scoreCore)
    {
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), D6.WiringPolicy);
        Assert.True(evaluation.Viable);
        var context = StructuralSimulation.Freeze(evaluation.Plan!, eventId, "UP", 40, 60, Fx.At(40), Fx.At(40))
            with { ScoreCore = scoreCore };
        return new(Fx.Symbol, Fx.At(39), Fx.At(40), Fx.SessionEnd, context, BenchmarkReturnPercent: .1);
    }

    [Fact]
    public void scoreCore_필드가_없는_기존_simtrades_json은_null로_복원된다()
    {
        var tags = new EntryScoreCoreTags("shadow", "abc123abc123abc123abc123", ScoreCorePolicy.ShadowV1.Version,
            ScoreCoreAggregator.SchemaVersion, AsOf);
        var trade = StructuralSimulation.Enter([], Request("TEST|sc-legacy", tags)).Trade!;
        var node = JsonNode.Parse(JsonSerializer.Serialize(new List<SimTrade> { trade }, Json))!.AsArray();
        var structure = node[0]!["structure"]!.AsObject();
        Assert.True(structure.Remove("scoreCore"));

        var legacy = Assert.Single(JsonSerializer.Deserialize<List<SimTrade>>(node.ToJsonString(), Json)!);

        Assert.Null(legacy.Structure!.ScoreCore);
        Assert.Equal(trade.Structure!.EntryEventId, legacy.Structure.EntryEventId);
        Assert.Equal(trade.Structure.Benchmark, legacy.Structure.Benchmark);
        Assert.Equal(trade.Structure.PlanSnapshot.PolicyHash, legacy.Structure.PlanSnapshot.PolicyHash);
        Assert.Equal(trade.Stop, legacy.Stop);
        Assert.Equal(trade.Target, legacy.Target);
    }

    [Fact]
    public void scoreCore_필드는_simtrades_json_왕복에서_보존된다()
    {
        var tags = new EntryScoreCoreTags("not_found", null, ScoreCorePolicy.ShadowV1.Version, null, null,
            EntryScoreCoreAttachment.ReasonNoSnapshot);
        var trade = StructuralSimulation.Enter([], Request("TEST|sc-roundtrip", tags)).Trade!;

        var restored = Assert.Single(JsonSerializer.Deserialize<List<SimTrade>>(
            JsonSerializer.Serialize(new List<SimTrade> { trade }, Json), Json)!);

        Assert.Equal(tags, restored.Structure!.ScoreCore);
        Assert.Equal(trade.Structure!.Benchmark, restored.Structure.Benchmark);
    }

    [Fact]
    public void scoreCore_참조는_거래_숫자와_청산_정책에_영향을_주지_않는다()
    {
        var tagged = StructuralSimulation.Enter([], Request("TEST|sc-same",
            new EntryScoreCoreTags("shadow", "abc123abc123abc123abc123", ScoreCorePolicy.ShadowV1.Version,
                ScoreCoreAggregator.SchemaVersion, AsOf))).Trade!;
        var untagged = StructuralSimulation.Enter([], Request("TEST|sc-same", null)).Trade!;

        Assert.Equal(untagged.EntryPrice, tagged.EntryPrice);
        Assert.Equal(untagged.Stop, tagged.Stop);
        Assert.Equal(untagged.Target, tagged.Target);
        Assert.Equal(untagged.Structure!.StructuralExitPolicyVersion, tagged.Structure!.StructuralExitPolicyVersion);
        Assert.Equal(untagged.Structure.PlanSnapshot.PolicyHash, tagged.Structure.PlanSnapshot.PolicyHash);
        Assert.Null(untagged.Structure.ScoreCore);
        Assert.Equal("shadow", tagged.Structure.ScoreCore!.Status);
    }

    [Fact]
    public void 관측_note는_scoreCore_상태를_담고_미수집이면_생략한다()
    {
        var tagged = StructuralSimulation.Enter([], Request("TEST|sc-note", EntryScoreCoreAttachment.Disabled())).Trade!;
        var untagged = StructuralSimulation.Enter([], Request("TEST|sc-note", null)).Trade!;

        Assert.Contains(StructureAnalysisService.NoteEntryScoreCorePrefix + EntryScoreCoreAttachment.StatusDisabled,
            StructureAnalysisService.EntryObservabilityNotes(tagged));
        Assert.DoesNotContain(StructureAnalysisService.EntryObservabilityNotes(untagged),
            x => x.StartsWith(StructureAnalysisService.NoteEntryScoreCorePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task replay_거래는_모두_unavailable_참조를_달고_진입_수는_체결_진단과_일치한다()
    {
        var run = await new HistoricalStructureTradeReplay(new RandomWalkBars(6),
                StructurePolicy.Default with
                {
                    RequireCompleteLiquidityCost = false,
                    ReboundMaxTrendAlignment = null,
                    WindowBlockStartMinutesFromOpen = null,
                    WindowBlockEndMinutesFromOpen = null
                })
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 9), ["TSLA"], default);

        var trades = run.Trades["TSLA"];
        Assert.NotEmpty(trades);
        Assert.Equal(run.Candidates.Count(x => x.Filled), trades.Length);
        Assert.All(trades, trade =>
        {
            var tags = Assert.IsType<EntryScoreCoreTags>(trade.Structure!.ScoreCore);
            Assert.Equal(EntryScoreCoreAttachment.StatusUnavailable, tags.Status);
            Assert.Equal(EntryScoreCoreAttachment.ReasonHistoricalReplay, tags.Reason);
            Assert.Null(tags.CaptureId);
        });
    }

    public static TheoryData<string> Ports => new() { "none", "disabled", "enabled" };

    [Theory]
    [MemberData(nameof(Ports))]
    public async Task 배선_정책에서_포트_유무와_활성_여부에_관계없이_진입은_한_건이고_숫자가_같다(string port)
    {
        var baseline = await RunLive(D6.WiringPolicy, null);
        var harness = await RunLive(D6.WiringPolicy, Port(port));

        var expected = Assert.Single(baseline.Store.Trades);
        var trade = Assert.Single(harness.Store.Trades);
        Assert.Equal(expected.EntryPrice, trade.EntryPrice);
        Assert.Equal(expected.Stop, trade.Stop);
        Assert.Equal(expected.Target, trade.Target);
        Assert.Equal(expected.Structure!.EntryEventId, trade.Structure!.EntryEventId);
        Assert.Equal(expected.Structure.PlanSnapshot.PolicyHash, trade.Structure.PlanSnapshot.PolicyHash);
        Assert.Equal(1, harness.Entries.Calls);
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        switch (port)
        {
            case "none":
                Assert.Null(trade.Structure.ScoreCore);
                Assert.DoesNotContain(view.Notes, x => x.StartsWith(StructureAnalysisService.NoteEntryScoreCorePrefix, StringComparison.Ordinal));
                break;
            case "disabled":
                Assert.Equal(EntryScoreCoreAttachment.StatusDisabled, trade.Structure.ScoreCore!.Status);
                Assert.Contains(StructureAnalysisService.NoteEntryScoreCorePrefix + EntryScoreCoreAttachment.StatusDisabled, view.Notes);
                break;
            default:
                Assert.Equal("shadow", trade.Structure.ScoreCore!.Status);
                Assert.Equal("abc123abc123abc123abc123", trade.Structure.ScoreCore.CaptureId);
                Assert.Contains(StructureAnalysisService.NoteEntryScoreCorePrefix + "shadow", view.Notes);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Ports))]
    public async Task Cycle45_기본_정책에서는_포트와_무관하게_같은_fixture가_같은_판정을_낸다(string port)
    {
        var baseline = await RunLive(StructurePolicy.Default, null);
        var harness = await RunLive(StructurePolicy.Default, Port(port));

        Assert.True(baseline.Structure.TryGetPublished(Fx.Symbol, out var expected));
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        Assert.Equal(baseline.Store.Trades.Count, harness.Store.Trades.Count);
        Assert.Equal(expected.CandidateSummary, view.CandidateSummary);
        Assert.Equal(expected.Candidates.Select(x => (x.Kind, x.State, string.Join(",", x.RejectionCodes))),
            view.Candidates.Select(x => (x.Kind, x.State, string.Join(",", x.RejectionCodes))));
        Assert.Equal(StructurePolicy.Default.PolicyHash, view.PolicyHash);
        Assert.Equal(baseline.Entries.Calls, harness.Entries.Calls);
    }

    static IEntryScoreCoreAttachment? Port(string port) => port switch
    {
        "none" => null,
        "disabled" => Attachment(false, new AttachmentStore()),
        _ => Attachment(true, new AttachmentStore
        {
            Latest = Snapshot("abc123abc123abc123abc123", "shadow", Fx.At(60))
        })
    };

    sealed record LiveHarness(RecordingStore Store, StructureAnalysisService Structure, CountingEntryPort Entries);

    static async Task<LiveHarness> RunLive(StructurePolicy policy, IEntryScoreCoreAttachment? scoreCore)
    {
        var defaultFixture = ReferenceEquals(policy, StructurePolicy.Default);
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var entries = new CountingEntryPort(new StructuralTradeEntryService(store));
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(new MemoryObservationStore(), policy),
            runtime, clock, diagnostics, new StructureEngineOptions(StructureEngineMode.Active), policy, entries,
            scoreCore: scoreCore);
        var gateway = new ScriptedGateway(D6.Session,
            () => defaultFixture ? D6.CompletedBars(clock.Now).Select(Compress).ToArray() : D6.CompletedBars(clock.Now),
            () => (defaultFixture ? 99.90 + (D6.QuotePrice(clock.Now) - 99.90) * 0.35 : D6.QuotePrice(clock.Now), clock.Now),
            () => defaultFixture ? D6.Daily().Select(Compress).ToArray() : D6.Daily());
        var poller = new MonitorPollingService(store, gateway, new QuietStream(), runtime, clock, diagnostics, structure);
        runtime.CommitStart();
        foreach (var minute in new[] { 64, 65 })
        {
            clock.Now = Fx.At(minute);
            await poller.PollAsync(default);
        }
        return new(store, structure, entries);
    }

    static Candle Compress(Candle c) => c with
    {
        Open = 99.90 + (c.Open - 99.90) * 0.35,
        High = 99.90 + (c.High - 99.90) * 0.35,
        Low = 99.90 + (c.Low - 99.90) * 0.35,
        Close = 99.90 + (c.Close - 99.90) * 0.35,
    };

    sealed class AttachmentStore : IScoreCoreSnapshotStore
    {
        public ScoreCoreShadowSnapshot? Latest { get; init; }
        public Exception? Failure { get; init; }
        public int FindLatestCalls { get; private set; }
        public (ImpactTargetKind Kind, string TargetId)? LastQuery { get; private set; }

        public Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot, CancellationToken ct)
            => Task.FromResult(ScoreSnapshotAppendResult.Appended);

        public Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct)
            => Task.FromResult(Latest?.CaptureId == captureId ? Latest : null);

        public Task<ScoreCoreShadowSnapshot?> FindLatestAsync(ImpactTargetKind kind, string targetId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            FindLatestCalls++;
            LastQuery = (kind, targetId);
            if (Failure is not null) throw Failure;
            return Task.FromResult(Latest);
        }

        public Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct) => Task.FromResult(0);
    }

    sealed class RandomWalkBars : IBarStore
    {
        readonly Dictionary<string, IReadOnlyList<string>> _days = new(StringComparer.Ordinal);

        public RandomWalkBars(int seed)
        {
            var random = new Random(seed);
            foreach (var day in new[] { "2026-09-08", "2026-09-09" })
            {
                var start = DateTimeOffset.Parse(day + "T13:30:00Z");
                var lines = new List<string>();
                var price = 100d;
                for (var i = 0; i < 390; i++)
                {
                    var open = price;
                    price *= 1 + (random.NextDouble() - .5) * .006;
                    lines.Add(JsonSerializer.Serialize(new
                    {
                        t = start.AddMinutes(i).UtcDateTime,
                        o = Math.Round(open, 2),
                        h = Math.Round(Math.Max(open, price) + random.NextDouble() * .15, 2),
                        l = Math.Round(Math.Min(open, price) - random.NextDouble() * .15, 2),
                        c = Math.Round(price, 2),
                        v = Math.Round(1000 + random.NextDouble() * 4000)
                    }));
                }
                _days[day] = lines;
            }
        }

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(_days.GetValueOrDefault(day)?.LastOrDefault());
        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_days.Keys.Order(StringComparer.Ordinal).ToArray());
        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["TSLA"]);
        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(symbol == "TSLA" ? _days[day].Count : 0);
        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(symbol == "TSLA" ? _days[day] : []);
        public Task DeleteDayAsync(string day, CancellationToken ct) => Task.CompletedTask;
    }
}
