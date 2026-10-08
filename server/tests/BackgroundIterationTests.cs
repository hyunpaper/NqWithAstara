using Astra.Server.Application;
using Xunit;

namespace Astra.Server.Tests;

public sealed class BackgroundIterationTests
{
    [Fact]
    public async Task 본문_예외는_로그하고_삼켜_호출자를_살려_둔다()
    {
        var diagnostics = new NewsDiagnostics();

        await BackgroundIteration.GuardAsync(() => throw new UnauthorizedAccessException(), diagnostics,
            "store-loop", CancellationToken.None);

        Assert.Single(diagnostics.Failures);
        Assert.Equal("store-loop", diagnostics.Failures[0].Scope);
    }

    [Fact]
    public async Task 저장_실패가_반복돼도_루프는_모든_반복을_돈다()
    {
        var diagnostics = new NewsDiagnostics();
        var iterations = 0;

        for (var i = 0; i < 3; i++)
            await BackgroundIteration.GuardAsync(() =>
            {
                iterations++;
                throw new IOException("sharing", unchecked((int)0x80070020));
            }, diagnostics, "obs-loop", CancellationToken.None);

        Assert.Equal(3, iterations);
        Assert.Equal(3, diagnostics.Failures.Count);
    }

    [Fact]
    public async Task 취소된_토큰의_취소예외는_전파한다()
    {
        var diagnostics = new NewsDiagnostics();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BackgroundIteration.GuardAsync(() => throw new OperationCanceledException(cts.Token), diagnostics,
                "loop", cts.Token));

        Assert.Empty(diagnostics.Failures);
    }
}
