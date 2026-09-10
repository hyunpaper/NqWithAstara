using Astra.Server.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>
/// 이슈 #6 / D3 미검증 #2 — 실제 파일 어댑터 <see cref="StructureObservationStore"/> 검증.
/// 임시 디렉터리만 사용한다: 디렉터리 생성, `.tmp` + File.Move 원자 쓰기, LF 고정, 재시작 복원.
/// </summary>
public sealed class StructureObservationStoreFileTests : IDisposable
{
    readonly string _contentRoot = Directory.CreateTempSubdirectory("astra-obs-store-").FullName;
    string StructureRoot => Path.Combine(_contentRoot, "App_Data", "structure");
    StructureObservationStore NewStore() => new(new FakeEnv(_contentRoot));

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* 임시 디렉터리 정리 실패는 무시 */ }
    }

    [Fact]
    public async Task AppendCreatesDirectoryUnderAppDataStructure()
    {
        var store = NewStore();
        Assert.False(Directory.Exists(StructureRoot));

        await store.AppendAsync("obs-2026-09-10.jsonl", "{\"a\":1}", CancellationToken.None);

        Assert.True(Directory.Exists(StructureRoot));
        Assert.True(File.Exists(Path.Combine(StructureRoot, "obs-2026-09-10.jsonl")));
    }

    [Fact]
    public async Task AppendWritesLfOnlyRegardlessOfPlatform()
    {
        var store = NewStore();
        await store.AppendAsync("obs.jsonl", "{\"a\":1}", CancellationToken.None);
        await store.AppendAsync("obs.jsonl", "{\"b\":2}", CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(StructureRoot, "obs.jsonl"));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\r", text);
        Assert.Equal("{\"a\":1}\n{\"b\":2}\n", text);
    }

    [Fact]
    public async Task SizeAndReadLinesReflectAppends()
    {
        var store = NewStore();
        Assert.Equal(0, await store.SizeAsync("missing.jsonl", CancellationToken.None));
        Assert.Empty(await store.ReadLinesAsync("missing.jsonl", CancellationToken.None));

        await store.AppendAsync("obs.jsonl", "line-1", CancellationToken.None);
        await store.AppendAsync("obs.jsonl", "line-2", CancellationToken.None);

        Assert.Equal("line-1\nline-2\n".Length, await store.SizeAsync("obs.jsonl", CancellationToken.None));
        Assert.Equal(new[] { "line-1", "line-2" }, await store.ReadLinesAsync("obs.jsonl", CancellationToken.None));
    }

    [Fact]
    public async Task WriteTextIsAtomicAndLeavesNoTmpFile()
    {
        var store = NewStore();
        await store.WriteTextAsync("latch.json", "{\"v\":1}", CancellationToken.None);
        await store.WriteTextAsync("latch.json", "{\"v\":2}", CancellationToken.None);

        Assert.Equal("{\"v\":2}", await store.ReadTextAsync("latch.json", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(StructureRoot, "*.tmp"));
    }

    [Fact]
    public async Task ReadTextReturnsNullWhenFileMissing()
    {
        Assert.Null(await NewStore().ReadTextAsync("missing.json", CancellationToken.None));
    }

    [Fact]
    public async Task RestartWithNewStoreInstanceReadsBackPersistedState()
    {
        await NewStore().WriteTextAsync("latch.json", "{\"restored\":true}", CancellationToken.None);
        await NewStore().AppendAsync("obs.jsonl", "kept", CancellationToken.None);

        // 재시작 시나리오: 같은 콘텐츠 루트 위에 새 인스턴스를 만든다.
        var restarted = NewStore();
        Assert.Equal("{\"restored\":true}", await restarted.ReadTextAsync("latch.json", CancellationToken.None));
        Assert.Equal(new[] { "kept" }, await restarted.ReadLinesAsync("obs.jsonl", CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentAppendsAreSerializedWithoutLosingLines()
    {
        var store = NewStore();
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => store.AppendAsync("obs.jsonl", $"line-{i}", CancellationToken.None)));

        var lines = await store.ReadLinesAsync("obs.jsonl", CancellationToken.None);
        Assert.Equal(50, lines.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"line-{i}").Order(), lines.Order());
    }

    /// <summary>ContentRootPath만 의미 있는 최소 가짜 환경. 실제 호스트를 띄우지 않는다.</summary>
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
