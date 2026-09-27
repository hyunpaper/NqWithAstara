using System.Collections.Immutable;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.ScoreCore;
using Astra.Server.Infrastructure.ScoreCore;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ScoreCoreSnapshotStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "astra-score-core-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AppendThenFindPreservesImmutableSnapshot()
    {
        var store = new JsonlScoreCoreSnapshotStore(_root);
        var snapshot = Snapshot("capture-1", "shadow");

        var appended = await store.AppendAsync(snapshot, default);
        var duplicate = await store.AppendAsync(snapshot, default);
        var loaded = await store.FindAsync(snapshot.CaptureId, default);

        Assert.Equal(ScoreSnapshotAppendResult.Appended, appended);
        Assert.Equal(ScoreSnapshotAppendResult.AlreadyExists, duplicate);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.CaptureId, loaded.CaptureId);
        Assert.Equal(snapshot.Score.SnapshotId, loaded.Score.SnapshotId);
        Assert.Equal(snapshot.Score.TargetId, loaded.Score.TargetId);
        Assert.Equal(snapshot.Score.AsOf, loaded.Score.AsOf);
        Assert.Equal(snapshot.Score.CalibrationStatus, loaded.Score.CalibrationStatus);
        Assert.Equal(snapshot.Coverage.ToArray(), loaded.Coverage.ToArray());
        Assert.Equal(snapshot.AssemblyExclusions.ToArray(), loaded.AssemblyExclusions.ToArray());
        Assert.Equal(snapshot.Status, loaded.Status);
        Assert.Single(File.ReadAllLines(Directory.GetFiles(_root, "*.jsonl").Single()));
    }

    [Fact]
    public async Task SameCaptureIdWithDifferentPayloadIsRejected()
    {
        var store = new JsonlScoreCoreSnapshotStore(_root);
        await store.AppendAsync(Snapshot("capture-1", "shadow"), default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(Snapshot("capture-1", "unavailable"), default));

        Assert.Contains("다른 snapshot", error.Message);
    }

    [Fact]
    public async Task ParallelDuplicateAppendCreatesOneLine()
    {
        var store = new JsonlScoreCoreSnapshotStore(_root);
        var snapshot = Snapshot("capture-1", "shadow");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.AppendAsync(snapshot, default)));

        Assert.Single(File.ReadAllLines(Directory.GetFiles(_root, "*.jsonl").Single()));
    }

    [Fact]
    public async Task SeparateStoreInstancesSerializeConcurrentAppends()
    {
        var first = new JsonlScoreCoreSnapshotStore(_root);
        var second = new JsonlScoreCoreSnapshotStore(_root);

        await Task.WhenAll(
            first.AppendAsync(Snapshot("capture-1", "shadow"), default),
            second.AppendAsync(Snapshot("capture-2", "shadow"), default));

        var lines = File.ReadAllLines(Directory.GetFiles(_root, "*.jsonl").Single());
        Assert.Equal(2, lines.Length);
        Assert.NotNull(await first.FindAsync("capture-1", default));
        Assert.NotNull(await second.FindAsync("capture-2", default));
    }

    [Fact]
    public async Task TruncatedTailIsRemovedBeforeNextAppend()
    {
        var store = new JsonlScoreCoreSnapshotStore(_root);
        await store.AppendAsync(Snapshot("capture-1", "shadow"), default);
        var path = Directory.GetFiles(_root, "*.jsonl").Single();
        await File.AppendAllTextAsync(path, "{\"captureId\":\"broken");

        await new JsonlScoreCoreSnapshotStore(_root)
            .AppendAsync(Snapshot("capture-2", "shadow"), default);

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain(lines, x => x.Contains("broken", StringComparison.Ordinal));
        Assert.NotNull(await store.FindAsync("capture-2", default));
    }

    [Fact]
    public async Task MalformedInteriorRowDoesNotHideLaterSnapshots()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "2026-09-27.jsonl");
        await File.WriteAllTextAsync(path, "{invalid}\n");
        var store = new JsonlScoreCoreSnapshotStore(_root);

        await store.AppendAsync(Snapshot("capture-2", "shadow"), default);

        Assert.NotNull(await store.FindAsync("capture-2", default));
    }

    static ScoreCoreShadowSnapshot Snapshot(string id, string status)
    {
        var asOf = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var score = new ScoreCoreSnapshot("score-1", "NVDA", ImpactTargetKind.Company, asOf,
            ScoreCoreAggregator.SchemaVersion, ScoreCorePolicy.ShadowV1.Version, "assessment-v1",
            ScoreCalibrationStatus.Uncalibrated, [], [], 0, 0, 0, "insufficient_data");
        return new(id, score,
            [new("assessed-news", "ready", 0, 0, "news_assessment_missing")],
            ImmutableArray<ScoreEvidenceExclusion>.Empty, status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
