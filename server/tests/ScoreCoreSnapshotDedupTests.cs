using System.Collections.Immutable;
using Astra.Server.Application;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.News;
using Astra.Server.Domain.ScoreCore;
using Astra.Server.Infrastructure.ScoreCore;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Server.Tests;

public sealed class ScoreCoreSnapshotDedupTests(ITestOutputHelper output) : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    readonly string _root = Path.Combine(Path.GetTempPath(), "astra-score-core-dedup", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SameContentInTwoCyclesAppendsOneLine()
    {
        var news = ScoreCoreNewsEvidenceTests.Store(ScoreCoreNewsEvidenceTests.Record("n1", Now.AddHours(-1)));
        var service = Service(news, new JsonlScoreCoreSnapshotStore(_root));

        var first = await service.CaptureWithResultAsync(new("NVDA", ImpactTargetKind.Company, Now));
        var second = await service.CaptureWithResultAsync(new("NVDA", ImpactTargetKind.Company, Now.AddMinutes(5)));

        Assert.Equal(ScoreSnapshotAppendResult.Appended, first.Append);
        Assert.Equal(ScoreSnapshotAppendResult.Unchanged, second.Append);
        Assert.Equal(first.Snapshot.ContentKey, second.Snapshot.ContentKey);
        Assert.NotEqual(first.Snapshot.CaptureId, second.Snapshot.CaptureId);
        Assert.Single(Lines("2026-10-02"));
    }

    [Fact]
    public async Task ChangedContentIsAppended()
    {
        var news = ScoreCoreNewsEvidenceTests.Store(ScoreCoreNewsEvidenceTests.Record("n1", Now.AddHours(-1)));
        var service = Service(news, new JsonlScoreCoreSnapshotStore(_root));

        await service.CaptureWithResultAsync(new("NVDA", ImpactTargetKind.Company, Now));
        Add(news, ScoreCoreNewsEvidenceTests.Record("n2", Now.AddMinutes(2)));
        var changed = await service.CaptureWithResultAsync(new("NVDA", ImpactTargetKind.Company, Now.AddMinutes(5)));

        Assert.Equal(ScoreSnapshotAppendResult.Appended, changed.Append);
        Assert.Equal(2, Lines("2026-10-02").Length);
    }

    [Fact]
    public async Task FirstCycleOfNewDateAppendsEvenWhenContentIsSame()
    {
        var store = new JsonlScoreCoreSnapshotStore(_root);
        var service = new ScoreCoreSnapshotService(new AsOfEvidenceAssembler([new MacroCalendarEvidenceSource()]), store);
        var late = new DateTimeOffset(2026, 10, 2, 23, 58, 0, TimeSpan.Zero);

        var before = await service.CaptureWithResultAsync(new("MARKET", ImpactTargetKind.Market, late));
        var after = await service.CaptureWithResultAsync(new("MARKET", ImpactTargetKind.Market, late.AddMinutes(5)));
        var repeat = await service.CaptureWithResultAsync(new("MARKET", ImpactTargetKind.Market, late.AddMinutes(10)));

        Assert.Equal(ScoreSnapshotAppendResult.Appended, before.Append);
        Assert.Equal(ScoreSnapshotAppendResult.Appended, after.Append);
        Assert.Equal(ScoreSnapshotAppendResult.Unchanged, repeat.Append);
        Assert.Equal(before.Snapshot.ContentKey, after.Snapshot.ContentKey);
        Assert.Single(Lines("2026-10-02"));
        Assert.Single(Lines("2026-10-03"));
    }

    [Fact]
    public async Task RestartedStoreKeepsUnchangedDecision()
    {
        var assembler = new AsOfEvidenceAssembler([new MacroCalendarEvidenceSource()]);
        await new ScoreCoreSnapshotService(assembler, new JsonlScoreCoreSnapshotStore(_root))
            .CaptureWithResultAsync(new("MARKET", ImpactTargetKind.Market, Now));

        var restarted = await new ScoreCoreSnapshotService(assembler, new JsonlScoreCoreSnapshotStore(_root))
            .CaptureWithResultAsync(new("MARKET", ImpactTargetKind.Market, Now.AddMinutes(5)));

        Assert.Equal(ScoreSnapshotAppendResult.Unchanged, restarted.Append);
        Assert.Single(Lines("2026-10-02"));
    }

    [Fact]
    public async Task FailingTargetDoesNotStopOtherTargets()
    {
        var store = new FailingStore(new JsonlScoreCoreSnapshotStore(_root), "OIL");
        var options = new ScoreCoreOptions { Enabled = true };
        var state = new ScoreCoreRuntimeState(options);
        var diagnostics = new NewsDiagnostics();
        var reader = new NewsScoreRecordReader(new MemoryNewsStore());
        var service = new ScoreCoreShadowCaptureService(options,
            new ScoreCoreTargetSelector(new NewsLocalStore(), reader, options),
            new ScoreCoreShadowCaptureOrchestrator(new ScoreCoreSnapshotService(
                new AsOfEvidenceAssembler([new NewsScoreEvidenceSource(reader, options)]), store)),
            store, state, diagnostics, new NewsClock(Now));

        await service.RunOnceAsync(default);
        var health = state.Health();

        Assert.Equal("partial", health.Status);
        Assert.Equal(["asset:OIL"], health.FailedTargets);
        Assert.Equal(2, health.SnapshotCount);
        Assert.Equal(2, health.AppendedCount);
        Assert.Equal(nameof(IOException), health.LastError);
        Assert.Contains(diagnostics.Failures, x => x.Scope.Contains("OIL", StringComparison.Ordinal));
        Assert.Equal(2, Lines("2026-10-02").Length);
        Assert.Equal(Now, state.LastConfirmedAt(ImpactTargetKind.Market, "MARKET"));
        Assert.Null(state.LastConfirmedAt(ImpactTargetKind.Asset, "OIL"));
    }

    [Fact]
    public async Task FiftyTargetsThreeCyclesStorageMeasurement()
    {
        var tickers = Enumerable.Range(0, 47).Select(i => $"T{i:D2}").ToArray();
        var news = ScoreCoreNewsEvidenceTests.Store(tickers.SelectMany(t => Enumerable.Range(0, 3).Select(i =>
            ScoreCoreNewsEvidenceTests.Record($"{t}-{i}", Now.AddHours(-1 - i), target: t))).ToArray());
        var options = new ScoreCoreOptions { Enabled = true };
        var state = new ScoreCoreRuntimeState(options);
        var clock = new NewsClock(Now);
        var store = new JsonlScoreCoreSnapshotStore(_root);
        var reader = new NewsScoreRecordReader(news);
        var service = new ScoreCoreShadowCaptureService(options,
            new ScoreCoreTargetSelector(new NewsLocalStore(), reader, options),
            new ScoreCoreShadowCaptureOrchestrator(new ScoreCoreSnapshotService(new AsOfEvidenceAssembler(
                [new NewsScoreEvidenceSource(reader, options), new MacroCalendarEvidenceSource()]), store)),
            store, state, new NewsDiagnostics(), clock);
        var path = Path.Combine(_root, "2026-10-02.jsonl");

        await service.RunOnceAsync(default);
        var firstLines = File.ReadAllLines(path).Length;
        var firstBytes = new FileInfo(path).Length;
        foreach (var ticker in tickers) Add(news, ScoreCoreNewsEvidenceTests.Record($"{ticker}-new", Now.AddMinutes(2), target: ticker));
        clock.Now = Now.AddMinutes(5);
        await service.RunOnceAsync(default);
        var changed = state.Health();
        clock.Now = Now.AddMinutes(10);
        await service.RunOnceAsync(default);
        var same = state.Health();
        var lines = File.ReadAllLines(path).Length;
        var bytes = new FileInfo(path).Length;
        output.WriteLine($"cycle1 lines={firstLines} bytes={firstBytes} avg={firstBytes / Math.Max(1, firstLines)}");
        output.WriteLine($"cycle2 appended={changed.AppendedCount} unchanged={changed.UnchangedCount}");
        output.WriteLine($"cycle3 appended={same.AppendedCount} unchanged={same.UnchangedCount}");
        output.WriteLine($"total lines={lines} bytes={bytes} avg={bytes / Math.Max(1, lines)}");

        Assert.Equal(50, firstLines);
        Assert.Equal(47, changed.AppendedCount);
        Assert.Equal(3, changed.UnchangedCount);
        Assert.Equal(0, same.AppendedCount);
        Assert.Equal(50, same.UnchangedCount);
        Assert.Equal(97, lines);
    }

    static ScoreCoreSnapshotService Service(MemoryNewsStore news, IScoreCoreSnapshotStore store)
    {
        var options = new ScoreCoreOptions();
        return new ScoreCoreSnapshotService(new AsOfEvidenceAssembler(
        [
            new NewsScoreEvidenceSource(new NewsScoreRecordReader(news), options),
            new MacroCalendarEvidenceSource(),
        ]), store);
    }

    static void Add(MemoryNewsStore news, NewsRecord record)
    {
        var added = ScoreCoreNewsEvidenceTests.Store(record);
        foreach (var (file, lines) in added.Files)
        {
            if (!news.Files.TryGetValue(file, out var existing)) news.Files[file] = existing = [];
            existing.AddRange(lines);
        }
    }

    string[] Lines(string date)
    {
        var path = Path.Combine(_root, date + ".jsonl");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    sealed class FailingStore(IScoreCoreSnapshotStore inner, string failingTarget) : IScoreCoreSnapshotStore
    {
        public Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot, CancellationToken ct)
            => snapshot.Score.TargetId == failingTarget
                ? throw new IOException("disk")
                : inner.AppendAsync(snapshot, ct);

        public Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct)
            => inner.FindAsync(captureId, ct);

        public Task<ScoreCoreShadowSnapshot?> FindLatestAsync(ImpactTargetKind kind, string targetId,
            CancellationToken ct) => inner.FindLatestAsync(kind, targetId, ct);

        public Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct)
            => inner.PruneAsync(now, retentionDays, ct);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
