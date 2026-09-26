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
