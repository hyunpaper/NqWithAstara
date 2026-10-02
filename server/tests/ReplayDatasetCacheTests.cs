using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Server.Application.Backtest;
using Astra.Server.Domain;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ReplayDatasetCacheTests
{
    static readonly DateOnly Day = new(2026, 9, 8);

    [Fact]
    public void RequestKeyNormalizesSymbolOrderAndSeparatesSourceContracts()
    {
        var first = ReplayDatasetCache.RequestKey("Toss", true, Day, Day, "qqq", ["tsla", "QQQ"]);
        var reordered = ReplayDatasetCache.RequestKey("Toss", true, Day, Day, "QQQ", ["QQQ", "TSLA", "tsla"]);
        var unadjusted = ReplayDatasetCache.RequestKey("Toss", false, Day, Day, "QQQ", ["TSLA", "QQQ"]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, unadjusted);
    }

    [Fact]
    public async Task IdenticalRequestReusesImmutableDatasetWithoutCallingTheSource()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-reuse-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);

            var first = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);
            var calls = source.Calls.Count;
            var second = await cache.AcquireAsync(["tsla", "QQQ"], "qqq", Day, Day, false,
                CancellationToken.None);

            Assert.Equal(first.DatasetId, second.DatasetId);
            Assert.True(second.Reused);
            Assert.Equal(calls, source.Calls.Count);
            Assert.Equal("available", second.Manifest.Status);
            Assert.True(File.Exists(Path.Combine(second.Root, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(second.Root, "import-report.json")));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(second.Root, "raw"), "page-*.json",
                SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(second.Root, "bars", "2026-09-08", "TSLA.jsonl")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task SymbolOrderIsCanonicalAcrossDatasetCreationAndReuse()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-symbol-order-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);

            var first = await cache.AcquireAsync(["TSLA", "AAPL"], "QQQ", Day, Day, false,
                CancellationToken.None);
            var calls = source.Calls.Count;
            var reused = await cache.AcquireAsync(["AAPL", "TSLA"], "QQQ", Day, Day, false,
                CancellationToken.None);

            Assert.Equal(new[] { "AAPL", "TSLA" }, first.Import.Watchlist.ToArray());
            Assert.Equal(first.DatasetId, reused.DatasetId);
            Assert.True(reused.Reused);
            Assert.Equal(calls, source.Calls.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task RefreshExplicitlyFetchesAgainWithoutDeletingThePreviousDataset()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-refresh-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var first = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);
            var calls = source.Calls.Count;
            source.PriceVersion = 10;

            var refreshed = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, true, CancellationToken.None);

            Assert.True(source.Calls.Count > calls);
            Assert.False(refreshed.Reused);
            Assert.NotEqual(first.DatasetId, refreshed.DatasetId);
            Assert.True(Directory.Exists(first.Root));
            Assert.True(Directory.Exists(refreshed.Root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task FailedRefreshDoesNotDisplaceTheSealedDatasetForDefaultReplay()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-refresh-fallback-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var sealedDataset = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false,
                CancellationToken.None);
            source.FailOnceAfterFirstPage = true;

            await Assert.ThrowsAsync<HttpRequestException>(() => cache.AcquireAsync(["TSLA"], "QQQ", Day, Day,
                true, CancellationToken.None));
            var callsAfterFailure = source.Calls.Count;
            var reused = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);

            Assert.Equal(sealedDataset.DatasetId, reused.DatasetId);
            Assert.True(reused.Reused);
            Assert.Equal(callsAfterFailure, source.Calls.Count);

            var refreshed = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, true,
                CancellationToken.None);

            Assert.NotEqual(sealedDataset.DatasetId, refreshed.DatasetId);
            Assert.Equal(2, source.Calls.Count(x => x.Symbol == "QQQ" && x.Before is null));
            Assert.Equal(2, source.Calls.Count(x => x.Symbol == "QQQ" && x.Before == "older"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task CanceledRefreshDoesNotDisplaceTheSealedDatasetForDefaultReplay()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-refresh-cancel-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var sealedDataset = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false,
                CancellationToken.None);
            source.BlockOnceAfterFirstPage = true;
            using var cancellation = new CancellationTokenSource();
            var refresh = cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, true, cancellation.Token);
            await source.FirstPageYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            var callsAfterCancel = source.Calls.Count;
            var reused = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);

            Assert.Equal(sealedDataset.DatasetId, reused.DatasetId);
            Assert.True(reused.Reused);
            Assert.Equal(callsAfterCancel, source.Calls.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task TerminalPartialDatasetIsImmutableAndReused()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-partial-").FullName;
        try
        {
            var source = new PagedSource { Partial = true };
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var first = await cache.AcquireAsync(["TSLA"], "QQQ", Day.AddDays(-7), Day, false,
                CancellationToken.None);
            var calls = source.Calls.Count;
            var second = await cache.AcquireAsync(["TSLA"], "QQQ", Day.AddDays(-7), Day, false,
                CancellationToken.None);

            Assert.Equal("partial", first.Manifest.Status);
            Assert.Equal(first.DatasetId, second.DatasetId);
            Assert.True(second.Reused);
            Assert.Equal(calls, source.Calls.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task FailedAcquisitionResumesAtThePersistedNextCursor()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-resume-").FullName;
        try
        {
            var source = new PagedSource { FailOnceAfterFirstPage = true };
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);

            await Assert.ThrowsAsync<HttpRequestException>(() => cache.AcquireAsync(["TSLA"], "QQQ", Day, Day,
                false, CancellationToken.None));
            var acquiringManifest = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, "replay-datasets", ".acquiring"), "manifest.json", SearchOption.AllDirectories));
            var acquiring = JsonSerializer.Deserialize<ReplayDatasetManifest>(
                await File.ReadAllTextAsync(acquiringManifest), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("acquiring", acquiring!.Status);
            Assert.NotNull(acquiring.FailureReason);

            var completed = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);

            Assert.Equal("available", completed.Manifest.Status);
            Assert.Equal(1, source.Calls.Count(x => x.Symbol == "TSLA" && x.Before is null));
            Assert.Equal(1, source.Calls.Count(x => x.Symbol == "TSLA" && x.Before == "older"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task CanceledAcquisitionStaysAcquiringAndResumesWithoutRefetchingThePage()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-cancel-").FullName;
        try
        {
            var source = new PagedSource { BlockOnceAfterFirstPage = true };
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            using var cancellation = new CancellationTokenSource();
            var acquiring = cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, cancellation.Token);
            await source.FirstPageYielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
            var manifestPath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, "replay-datasets", ".acquiring"), "manifest.json", SearchOption.AllDirectories));
            var manifest = JsonSerializer.Deserialize<ReplayDatasetManifest>(await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("acquiring", manifest!.Status);
            Assert.Equal("canceled", manifest.FailureReason);

            var completed = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);

            Assert.Equal("available", completed.Manifest.Status);
            Assert.Equal(1, source.Calls.Count(x => x.Symbol == "TSLA" && x.Before is null));
            Assert.Equal(1, source.Calls.Count(x => x.Symbol == "TSLA" && x.Before == "older"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsAreSerializedAcrossTheRequestFileLock()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-lock-").FullName;
        try
        {
            var source = new PagedSource { Delay = TimeSpan.FromMilliseconds(30) };
            var firstCache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var secondCache = new ReplayDatasetCache(root, source, TimeProvider.System);

            var leases = await Task.WhenAll(
                firstCache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None),
                secondCache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None));

            Assert.Equal(leases[0].DatasetId, leases[1].DatasetId);
            Assert.Equal(4, source.Calls.Count);
            Assert.Single(leases, x => x.Reused);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task SealedDatasetIsRecoveredWhenTheProcessStoppedBeforeUpdatingTheIndex()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-index-recovery-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var first = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);
            var calls = source.Calls.Count;
            var requestKey = ReplayDatasetCache.RequestKey(source.Name, source.Adjusted, Day, Day, "QQQ",
                ["QQQ", "TSLA"]);
            var indexPath = Path.Combine(root, "replay-datasets", "index", requestKey + ".json");
            await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(new
            {
                requestKey,
                datasetId = first.DatasetId,
                acquisitionId = "moved-before-index-update"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var recovered = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);

            Assert.Equal(first.DatasetId, recovered.DatasetId);
            Assert.True(recovered.Reused);
            Assert.Equal(calls, source.Calls.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ResumeCycleIsSealedWithoutCallingOrPersistingTheRepeatedPage()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-resume-cycle-").FullName;
        try
        {
            var source = new ResumeCycleSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);

            await Assert.ThrowsAsync<HttpRequestException>(() => cache.AcquireAsync(["TSLA"], "QQQ", Day, Day,
                false, CancellationToken.None));
            var callsAfterFailure = source.Calls;
            var completed = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false,
                CancellationToken.None);

            Assert.Equal("partial", completed.Manifest.Status);
            Assert.Equal(callsAfterFailure, source.Calls);
            var pages = Directory.EnumerateFiles(Path.Combine(completed.Root, "raw", "TSLA"), "page-*.json")
                .Order(StringComparer.Ordinal).Select(path => JsonSerializer.Deserialize<HistoricalBarPage>(
                    File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
            Assert.Equal(2, pages.Length);
            Assert.Equal(2, pages.Select(x => x.RequestCursor).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("순환", pages[^1].StopReason);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task InvalidIndexIdsCannotEscapeTheDatasetRoot()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-invalid-index-").FullName;
        try
        {
            var source = new PagedSource();
            var requestKey = ReplayDatasetCache.RequestKey(source.Name, source.Adjusted, Day, Day, "QQQ",
                ["QQQ", "TSLA"]);
            var indexPath = Path.Combine(root, "replay-datasets", "index", requestKey + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(new
            {
                requestKey,
                datasetId = "../outside",
                acquisitionId = "../../outside"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var dataset = await new ReplayDatasetCache(root, source, TimeProvider.System)
                .AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);

            Assert.Equal("available", dataset.Manifest.Status);
            Assert.StartsWith(Path.Combine(root, "replay-datasets"), dataset.Root, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "outside")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task TamperedManifestCannotBeReusedOrTriggerAnImplicitRefetch()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-invalid-manifest-").FullName;
        try
        {
            var source = new PagedSource();
            var cache = new ReplayDatasetCache(root, source, TimeProvider.System);
            var dataset = await cache.AcquireAsync(["TSLA"], "QQQ", Day, Day, false,
                CancellationToken.None);
            var calls = source.Calls.Count;
            var manifestPath = Path.Combine(dataset.Root, "manifest.json");
            var manifest = JsonSerializer.Deserialize<ReplayDatasetManifest>(await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with
            {
                DatasetId = new string('0', 64)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayDatasetCache(root, source,
                TimeProvider.System).AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None));
            Assert.Equal(calls, source.Calls.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task TamperedManifestProgressIsRejectedByTheDatasetContentHash()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-progress-tamper-").FullName;
        try
        {
            var source = new PagedSource();
            var dataset = await new ReplayDatasetCache(root, source, TimeProvider.System)
                .AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None);
            var calls = source.Calls.Count;
            var manifestPath = Path.Combine(dataset.Root, "manifest.json");
            var manifest = JsonSerializer.Deserialize<ReplayDatasetManifest>(await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var progress = manifest.Progress.SetItem(0, manifest.Progress[0] with
            {
                StopReason = "변조된 종료 사유"
            });
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with
            {
                Progress = progress
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayDatasetCache(root, source,
                TimeProvider.System).AcquireAsync(["TSLA"], "QQQ", Day, Day, false, CancellationToken.None));
            Assert.Equal(calls, source.Calls.Count);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    sealed class PagedSource : IHistoricalBarPageSource
    {
        int _fail = 1;
        int _block = 1;
        public string Name => "Toss";
        public bool Adjusted => true;
        public bool Partial { get; init; }
        public bool FailOnceAfterFirstPage { get; set; }
        public bool BlockOnceAfterFirstPage { get; set; }
        public int PriceVersion { get; set; }
        public TimeSpan Delay { get; init; }
        public ConcurrentBag<(string Symbol, string? Before)> Calls { get; } = [];
        public TaskCompletionSource FirstPageYielded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct) => throw new InvalidOperationException("page source를 사용해야 합니다.");

        public async IAsyncEnumerable<HistoricalBarPage> ReadPagesAsync(string symbol, DateTimeOffset from,
            DateTimeOffset to, string? before, int completedPages,
            IReadOnlyCollection<string> visitedCursors,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Partial)
            {
                Calls.Add((symbol, before));
                var bar = Bar("2026-09-08T13:30:00Z", (symbol == "QQQ" ? 500 : 100) + PriceVersion);
                yield return new(to.ToString("O"), null, [bar], 1, false, bar.Timestamp, "provider-history-end");
                yield break;
            }
            if (before is null)
            {
                Calls.Add((symbol, null));
                var bar = Bar("2026-09-08T13:30:00Z", (symbol == "QQQ" ? 500 : 100) + PriceVersion);
                yield return new(to.ToString("O"), "older", [bar], 1, false, bar.Timestamp, null);
                if (FailOnceAfterFirstPage && Interlocked.Exchange(ref _fail, 0) == 1)
                    throw new HttpRequestException("fixture network failure");
                if (BlockOnceAfterFirstPage && Interlocked.Exchange(ref _block, 0) == 1)
                {
                    FirstPageYielded.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                Calls.Add((symbol, "older"));
                var oldest = new Candle(from, 1, 1, 1, 1, 0);
                yield return new("older", null, [oldest], 1, true, oldest.Timestamp, null);
            }
            else
            {
                Calls.Add((symbol, before));
                var oldest = new Candle(from, 1, 1, 1, 1, 0);
                yield return new(before, null, [oldest], 1, true, oldest.Timestamp, null);
            }
        }
    }

    sealed class ResumeCycleSource : IHistoricalBarPageSource
    {
        int _fail = 1;
        public string Name => "Toss";
        public bool Adjusted => true;
        public int Calls { get; private set; }
        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct) => throw new InvalidOperationException("page source를 사용해야 합니다.");

        public async IAsyncEnumerable<HistoricalBarPage> ReadPagesAsync(string symbol, DateTimeOffset from,
            DateTimeOffset to, string? before, int completedPages, IReadOnlyCollection<string> visitedCursors,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            await Task.Yield();
            if (symbol == "QQQ")
            {
                var oldest = new Candle(from, 1, 1, 1, 1, 0);
                var regular = Bar("2026-09-08T13:30:00Z", 500);
                yield return new(to.ToString("O"), null, [regular, oldest], 2, true, oldest.Timestamp, null);
                yield break;
            }
            var bar = Bar("2026-09-08T13:30:00Z", 100);
            yield return new("A", "B", [bar], 1, false, bar.Timestamp, null);
            yield return new("B", "A", [bar], 1, false, bar.Timestamp, null);
            if (Interlocked.Exchange(ref _fail, 0) == 1) throw new HttpRequestException("cycle fixture stop");
        }
    }

    static Candle Bar(string timestamp, double close) => new(DateTimeOffset.Parse(timestamp), close, close + 1,
        close - 1, close, 1000);
}
