using Astra.Server.Application;
using Xunit;

namespace Astra.Server.Tests;

public sealed class AtomicFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("astra-atomic-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static Task NoDelay(int attempt, CancellationToken ct) => Task.CompletedTask;

    static IOException SharingViolation() => new("sharing", unchecked((int)0x80070020));

    [Fact]
    public async Task RetryAsync_AccessDenied_뒤_재시도로_성공한다()
    {
        var attempts = 0;
        await AtomicFile.RetryAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw new UnauthorizedAccessException();
            return Task.CompletedTask;
        }, CancellationToken.None, maxAttempts: 5, delay: NoDelay);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryAsync_공유위반_IOException도_재시도한다()
    {
        var attempts = 0;
        await AtomicFile.RetryAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw SharingViolation();
            return Task.CompletedTask;
        }, CancellationToken.None, maxAttempts: 5, delay: NoDelay);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryAsync_상한까지_실패하면_원래_예외를_올린다()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AtomicFile.RetryAsync(() =>
        {
            attempts++;
            throw new UnauthorizedAccessException();
        }, CancellationToken.None, maxAttempts: 3, delay: NoDelay));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryAsync_일시적이지_않은_예외는_재시도하지_않는다()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AtomicFile.RetryAsync(() =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, CancellationToken.None, maxAttempts: 5, delay: NoDelay));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryAsync_재시도_사이에만_지연을_호출한다()
    {
        var delays = 0;
        var attempts = 0;
        await AtomicFile.RetryAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw new UnauthorizedAccessException();
            return Task.CompletedTask;
        }, CancellationToken.None, maxAttempts: 5, delay: (_, _) => { delays++; return Task.CompletedTask; });

        Assert.Equal(3, attempts);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task WriteReplaceAsync_성공하면_내용을_쓰고_tmp를_남기지_않는다()
    {
        var path = Path.Combine(_dir, "data.json");
        await AtomicFile.WriteReplaceAsync(path, "{\"v\":1}", CancellationToken.None, delay: NoDelay);
        await AtomicFile.WriteReplaceAsync(path, "{\"v\":2}", CancellationToken.None, delay: NoDelay);

        Assert.Equal("{\"v\":2}", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task WriteReplaceAsync_교체가_실패하면_tmp를_정리하고_예외를_올린다()
    {
        var path = Path.Combine(_dir, "blocked.json");
        Directory.CreateDirectory(path);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            AtomicFile.WriteReplaceAsync(path, "대체", CancellationToken.None, maxAttempts: 3, delay: NoDelay));

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void IsTransient_접근거부와_공유위반만_참이다()
    {
        Assert.True(AtomicFile.IsTransient(new UnauthorizedAccessException()));
        Assert.True(AtomicFile.IsTransient(SharingViolation()));
        Assert.False(AtomicFile.IsTransient(new IOException("디스크 가득", unchecked((int)0x80070070))));
        Assert.False(AtomicFile.IsTransient(new InvalidOperationException()));
    }
}
