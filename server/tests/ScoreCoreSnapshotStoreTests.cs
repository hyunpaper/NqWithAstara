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

    [Fact]
    public async Task AppendSearchesOnlyTheSnapshotDateFile()
    {
        Directory.CreateDirectory(_root);
        var other = Path.Combine(_root, "2026-09-26.jsonl");
        await File.WriteAllTextAsync(other, "{\"captureId\":\"capture-1\"}\n");
        using var locked = new FileStream(other, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = new JsonlScoreCoreSnapshotStore(_root);

        var result = await store.AppendAsync(Snapshot("capture-1", "shadow"), default);

        Assert.Equal(ScoreSnapshotAppendResult.Appended, result);
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "2026-09-27.jsonl")));
    }

    [Fact]
    public async Task RestartedStoreRejectsDuplicateAndConflictingAppend()
    {
        await new JsonlScoreCoreSnapshotStore(_root).AppendAsync(Snapshot("capture-1", "shadow"), default);
        var restarted = new JsonlScoreCoreSnapshotStore(_root);

        var duplicate = await restarted.AppendAsync(Snapshot("capture-1", "shadow"), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.AppendAsync(Snapshot("capture-1", "unavailable"), default));

        Assert.Equal(ScoreSnapshotAppendResult.AlreadyExists, duplicate);
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "2026-09-27.jsonl")));
    }

    [Fact]
    public async Task IndexSeesAppendsFromAnotherStoreInstance()
    {
        var first = new JsonlScoreCoreSnapshotStore(_root);
        var second = new JsonlScoreCoreSnapshotStore(_root);
        await first.AppendAsync(Snapshot("capture-1", "shadow"), default);
        await second.AppendAsync(Snapshot("capture-2", "shadow"), default);

        var duplicate = await first.AppendAsync(Snapshot("capture-2", "shadow"), default);

        Assert.Equal(ScoreSnapshotAppendResult.AlreadyExists, duplicate);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_root, "2026-09-27.jsonl")).Length);
    }

    [Fact]
    public async Task FindLatestReturnsNewestSnapshotForTargetAcrossDates()
    {
        var store = new JsonlScoreCoreSnapshotStore(_root);
        var day = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        await store.AppendAsync(Snapshot("old", "shadow", day.AddDays(-1)), default);
        await store.AppendAsync(Snapshot("new", "shadow", day), default);
        await store.AppendAsync(Snapshot("earlier", "shadow", day.AddHours(-1)), default);
        await store.AppendAsync(Snapshot("other", "shadow", day.AddHours(1), "AMD"), default);

        var latest = await new JsonlScoreCoreSnapshotStore(_root)
            .FindLatestAsync(ImpactTargetKind.Company, "nvda", default);
        var previous = await store.FindAsync("old", default);

        Assert.Equal("new", latest?.CaptureId);
        Assert.Equal("old", previous?.CaptureId);
        Assert.Null(await store.FindLatestAsync(ImpactTargetKind.Market, "NVDA", default));
    }

    [Fact]
    public async Task PruneDeletesOnlyExpiredDatedFilesInRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        var sibling = _root + "-sibling";
        Directory.CreateDirectory(sibling);
        try
        {
            var expired = Path.Combine(_root, "2026-08-01.jsonl");
            var kept = Path.Combine(_root, "2026-09-20.jsonl");
            var other = new[]
            {
                Path.Combine(_root, "notes.txt"), Path.Combine(_root, "2026-08-01.jsonl.bak"),
                Path.Combine(_root, "backup-2026-08-01.jsonl"), Path.Combine(_root, "nested", "2026-08-01.jsonl"),
                Path.Combine(sibling, "2026-08-01.jsonl"),
            };
            foreach (var path in other.Append(expired).Append(kept)) await File.WriteAllTextAsync(path, "x\n");

            var deleted = await new JsonlScoreCoreSnapshotStore(_root)
                .PruneAsync(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), 30, default);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(kept));
            Assert.All(other, path => Assert.True(File.Exists(path), path));
        }
        finally { Directory.Delete(sibling, true); }
    }

    [Fact]
    public async Task TruncatedTailRecoveryReportsRemovedBytes()
    {
        var diagnostics = new NewsDiagnostics();
        await new JsonlScoreCoreSnapshotStore(_root).AppendAsync(Snapshot("capture-1", "shadow"), default);
        await File.AppendAllTextAsync(Path.Combine(_root, "2026-09-27.jsonl"), "{\"broken");
        var store = new JsonlScoreCoreSnapshotStore(_root, diagnostics);

        await store.AppendAsync(Snapshot("capture-2", "shadow"), default);

        Assert.Equal(8, store.RecoveredTailBytes);
        var failure = Assert.Single(diagnostics.Failures);
        Assert.Equal("score-core-store", failure.Scope);
        Assert.Contains("8바이트", failure.Exception.Message);
    }

    static ScoreCoreShadowSnapshot Snapshot(string id, string status, DateTimeOffset? at = null,
        string target = "NVDA")
    {
        var asOf = at ?? new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var score = new ScoreCoreSnapshot("score-1", target, ImpactTargetKind.Company, asOf,
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
