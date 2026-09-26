using Astra.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Astra.Server.Tests;

public sealed class NewsStoreFileTests : IDisposable
{
    readonly string _contentRoot = Directory.CreateTempSubdirectory("astra-news-store-").FullName;

    [Fact]
    public async Task 정리중_동시에_추가된_SBH_기사를_보존한다()
    {
        var store = new NewsStore(new FakeEnv(_contentRoot));
        const string file = "2026-09-27.jsonl";
        await store.AppendAsync(file, "legacy", CancellationToken.None);
        await store.AppendAsync(file, "sbh-existing", CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var filter = Task.Run(() => store.FilterLinesAsync(file, line =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return line.StartsWith("sbh-", StringComparison.Ordinal);
        }, CancellationToken.None));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var append = store.AppendAsync(file, "sbh-arrived-during-migration", CancellationToken.None);
        release.TrySetResult();

        Assert.Equal(1, await filter);
        await append;

        Assert.Equal(["sbh-existing", "sbh-arrived-during-migration"],
            await store.ReadLinesAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task 여러_파일_정리에서_후속_교체가_실패하면_앞선_파일도_복구한다()
    {
        var store = new NewsStore(new FakeEnv(_contentRoot));
        const string first = "2026-09-25.jsonl";
        const string second = "2026-09-26.jsonl";
        await store.AppendAsync(first, "legacy-first", CancellationToken.None);
        await store.AppendAsync(first, "sbh-first", CancellationToken.None);
        await store.AppendAsync(second, "legacy-second", CancellationToken.None);
        var secondPath = Path.Combine(_contentRoot, "App_Data", "news", second);
        File.SetAttributes(secondPath, File.GetAttributes(secondPath) | FileAttributes.ReadOnly);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.FilterFilesAsync([first, second],
                line => !line.StartsWith("legacy-", StringComparison.Ordinal), CancellationToken.None));
            Assert.Equal(["legacy-first", "sbh-first"], await store.ReadLinesAsync(first, CancellationToken.None));
            Assert.Equal(["legacy-second"], await store.ReadLinesAsync(second, CancellationToken.None));
        }
        finally
        {
            File.SetAttributes(secondPath, FileAttributes.Normal);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Astra.Server.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
    }
}
