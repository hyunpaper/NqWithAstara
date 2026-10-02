using Astra.Server.Application;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.News;
using Astra.Server.Domain.ScoreCore;
using Astra.Server.Infrastructure.ScoreCore;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ScoreCoreShadowCaptureServiceTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledServiceDoesNotCaptureOrPrune()
    {
        var fixture = new Fixture(enabled: false);

        await fixture.Service.RunOnceAsync(default);

        Assert.Empty(fixture.Store.Values);
        Assert.Equal(0, fixture.Store.PruneCalls);
        Assert.Equal("disabled", fixture.State.Health().Status);
        Assert.False(fixture.State.Health().Enabled);
    }

    [Fact]
    public async Task SelectionIsDeterministicAndCapped()
    {
        var news = ScoreCoreNewsEvidenceTests.Store(
            ScoreCoreNewsEvidenceTests.Record("a1", Now.AddHours(-1), target: "AMD"),
            ScoreCoreNewsEvidenceTests.Record("a2", Now.AddHours(-2), target: "AMD"),
            ScoreCoreNewsEvidenceTests.Record("a3", Now.AddHours(-3), target: "AMD") with { ClassifiedFrom = "a2" },
            ScoreCoreNewsEvidenceTests.Record("i1", Now.AddHours(-1), target: "INTC"),
            ScoreCoreNewsEvidenceTests.Record("b1", Now.AddHours(-1), target: "AVGO"),
            ScoreCoreNewsEvidenceTests.Record("old", Now.AddHours(-30), target: "MU"));
        var local = new NewsLocalStore(new WatchItem("tsla", "테슬라"), new WatchItem("NVDA", "엔비디아"),
            new WatchItem("AMD", "AMD"));
        var reader = new NewsScoreRecordReader(news);

        var all = await new ScoreCoreTargetSelector(local, reader, new ScoreCoreOptions())
            .SelectAsync(Now, default);
        var capped = await new ScoreCoreTargetSelector(local, reader, new ScoreCoreOptions { MaxTargets = 7 })
            .SelectAsync(Now, default);

        Assert.Equal(
        [
            ("MARKET", ImpactTargetKind.Market), ("OIL", ImpactTargetKind.Asset), ("TREASURY", ImpactTargetKind.Asset),
            ("AMD", ImpactTargetKind.Company), ("NVDA", ImpactTargetKind.Company), ("TSLA", ImpactTargetKind.Company),
            ("AVGO", ImpactTargetKind.Company), ("INTC", ImpactTargetKind.Company),
        ], all);
        Assert.Equal(all.Take(7), capped);
    }

    [Fact]
    public async Task FailedCycleIsReportedAndNextCycleContinues()
    {
        var fixture = new Fixture(enabled: true);
        fixture.Local.FailNextRead = true;

        await fixture.Service.RunOnceAsync(default);
        var failed = fixture.State.Health();
        fixture.Clock.Now = Now.AddMinutes(5);
        await fixture.Service.RunOnceAsync(default);
        var recovered = fixture.State.Health();

        Assert.Equal("failed", failed.Status);
        Assert.Equal(nameof(IOException), failed.LastError);
        Assert.Contains(fixture.Diagnostics.Failures, x => x.Scope == "score-core-capture");
        Assert.Equal("ok", recovered.Status);
        Assert.Null(recovered.LastError);
        Assert.Empty(recovered.FailedTargets);
        Assert.Equal(3, recovered.TargetCount);
        Assert.Equal(3, recovered.SnapshotCount);
        Assert.Equal(3, recovered.AppendedCount);
        Assert.Equal(3, recovered.InsufficientCount);
        Assert.Equal(0, recovered.UnavailableCount);
        Assert.Equal(Now.AddMinutes(5), recovered.LastSuccessAt);
    }

    [Fact]
    public async Task RetentionRunsAtStartupAndOncePerDay()
    {
        var fixture = new Fixture(enabled: true);

        await fixture.Service.RunOnceAsync(default);
        fixture.Clock.Now = Now.AddHours(1);
        await fixture.Service.RunOnceAsync(default);
        fixture.Clock.Now = Now.AddDays(1);
        await fixture.Service.RunOnceAsync(default);

        Assert.Equal(2, fixture.Store.PruneCalls);
        Assert.Equal(30, fixture.Store.LastRetentionDays);
        Assert.Equal(Now.AddDays(1), fixture.State.Health().LastPrunedAt);
    }

    [Fact]
    public async Task HealthReportsCapturedSnapshotCounts()
    {
        var fixture = new Fixture(enabled: true,
            ScoreCoreNewsEvidenceTests.Record("n1", Now.AddHours(-1), target: "NVDA"));

        await fixture.Service.RunOnceAsync(default);
        var health = fixture.State.Health();

        Assert.True(health.Enabled);
        Assert.Equal("ok", health.Status);
        Assert.Equal(4, health.TargetCount);
        Assert.Equal(4, health.SnapshotCount);
        Assert.Equal(Now, health.LastRunAt);
        Assert.Equal(ScoreCorePolicy.ShadowV1.Version, health.PolicyVersion);
        Assert.Contains(fixture.Store.Values.Values, x => x.Score.TargetId == "NVDA" && x.EvidenceCount == 1);
    }

    sealed class Fixture
    {
        public NewsClock Clock { get; } = new(Now);
        public RecordingStore Store { get; } = new();
        public NewsDiagnostics Diagnostics { get; } = new();
        public FlakyLocalStore Local { get; } = new();
        public ScoreCoreRuntimeState State { get; }
        public ScoreCoreShadowCaptureService Service { get; }

        public Fixture(bool enabled, params NewsRecord[] news)
        {
            var options = new ScoreCoreOptions { Enabled = enabled };
            var reader = new NewsScoreRecordReader(ScoreCoreNewsEvidenceTests.Store(news));
            var assembler = new AsOfEvidenceAssembler(
                [new NewsScoreEvidenceSource(reader, options), new MacroCalendarEvidenceSource()]);
            State = new ScoreCoreRuntimeState(options);
            Service = new ScoreCoreShadowCaptureService(options,
                new ScoreCoreTargetSelector(Local, reader, options),
                new ScoreCoreShadowCaptureOrchestrator(new ScoreCoreSnapshotService(assembler, Store)),
                Store, State, Diagnostics, Clock);
        }
    }

    sealed class FlakyLocalStore : ILocalStore
    {
        readonly NewsLocalStore _inner = new();
        public bool FailNextRead { get; set; }

        public Task<T> Read<T>(string file, T fallback)
        {
            if (!FailNextRead) return _inner.Read(file, fallback);
            FailNextRead = false;
            throw new IOException("watchlist");
        }

        public Task Write<T>(string file, T data) => _inner.Write(file, data);

        public Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
            => _inner.Update(file, fallback, change);
    }

    sealed class RecordingStore : IScoreCoreSnapshotStore
    {
        public Dictionary<string, ScoreCoreShadowSnapshot> Values { get; } = [];
        public int PruneCalls { get; private set; }
        public int LastRetentionDays { get; private set; }

        public Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot, CancellationToken ct)
            => Task.FromResult(Values.TryAdd(snapshot.CaptureId, snapshot)
                ? ScoreSnapshotAppendResult.Appended
                : ScoreSnapshotAppendResult.AlreadyExists);

        public Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct)
            => Task.FromResult(Values.GetValueOrDefault(captureId));

        public Task<ScoreCoreShadowSnapshot?> FindLatestAsync(ImpactTargetKind kind, string targetId,
            CancellationToken ct) => Task.FromResult<ScoreCoreShadowSnapshot?>(null);

        public Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct)
        {
            PruneCalls++;
            LastRetentionDays = retentionDays;
            return Task.FromResult(0);
        }
    }
}
