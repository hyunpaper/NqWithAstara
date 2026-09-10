using System.Text;
using Astra.Server;
using Xunit;

public sealed class TossStreamTests
{
    [Fact]
    public void ParsesExactUsTradeSchema()
    {
        var json = """{"type":"message","topic":"trade:us:AAPL","data":{"price":"243.26","volume":"8","timestamp":"2026-06-18T23:30:00+09:00","currency":"USD"}}""";
        Assert.True(TossStreamService.TryParseTrade(Encoding.UTF8.GetBytes(json), out var trade));
        Assert.Equal("AAPL", trade.Symbol);
        Assert.Equal(243.26m, trade.Price);
        Assert.Equal(8m, trade.Volume);
        Assert.Equal("USD", trade.Currency);
        Assert.Equal(TimeSpan.FromHours(9), trade.Timestamp.Offset);
    }

    [Theory]
    [InlineData("{\"type\":\"pong\"}")]
    [InlineData("{\"type\":\"message\",\"topic\":\"orderbook:us:AAPL\",\"data\":{}}")]
    [InlineData("{\"type\":\"message\",\"topic\":\"trade:us:AAPL\",\"data\":{\"price\":243.26,\"volume\":\"8\",\"timestamp\":\"2026-06-18T23:30:00+09:00\",\"currency\":\"USD\"}}")]
    [InlineData("{\"type\":{},\"topic\":\"trade:us:AAPL\",\"data\":{}}")]
    [InlineData("{\"type\":\"message\",\"topic\":\"trade:us:AAPL\",\"data\":{\"price\":\"1\",\"volume\":\"1\",\"timestamp\":42,\"currency\":\"USD\"}}")]
    [InlineData("not json")]
    public void IgnoresNonTradeOrMalformedFrames(string frame)
    {
        Assert.False(TossStreamService.TryParseTrade(Encoding.UTF8.GetBytes(frame), out _));
    }

    [Fact]
    public async Task RestartKeepsTicksForStillSubscribedSymbolsOnly()
    {
        var service = new TossStreamService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TossStreamService>.Instance);
        var now = DateTimeOffset.UtcNow;
        service.Latest["AAPL"] = new TossTrade("AAPL", 100m, 1m, now.AddSeconds(-10), "USD");
        service.Latest["MSFT"] = new TossTrade("MSFT", 200m, 1m, now, "USD");
        await service.StartAsync(["AAPL", "NVDA"], _ => Task.FromException<string>(new InvalidOperationException("no token in tests")), true);
        Assert.True(service.TryGetLatest("AAPL", out var kept));
        Assert.Equal(100m, kept.Price);
        Assert.False(service.TryGetLatest("MSFT", out _));
        Assert.Equal(now.AddSeconds(-10), service.LastTickAt);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task StopClearsAllTicks()
    {
        var service = new TossStreamService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TossStreamService>.Instance);
        service.Latest["AAPL"] = new TossTrade("AAPL", 100m, 1m, DateTimeOffset.UtcNow, "USD");
        await service.StopAsync();
        Assert.False(service.TryGetLatest("AAPL", out _));
        Assert.Null(service.LastTickAt);
        Assert.Equal("idle", service.Status);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task DisposeIsIdempotentAcrossDuplicateDiRegistrations()
    {
        // Program.cs는 구체 타입과 IRealtimeMarketStream 두 등록으로 같은 인스턴스를 노출한다.
        // 호스트 종료 시 컨테이너가 두 번 dispose하므로 두 번째 호출이 예외 없이 끝나야 한다.
        var service = new TossStreamService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TossStreamService>.Instance);
        await service.DisposeAsync();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task RefusesConnectionUntilAllowedIpIsConfirmed()
    {
        var service = new TossStreamService(Microsoft.Extensions.Logging.Abstractions.NullLogger<TossStreamService>.Instance);
        var tokenCalled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(["AAPL"], _ =>
        {
            tokenCalled = true;
            return Task.FromResult("unused");
        }, false));
        Assert.False(tokenCalled);
        await service.DisposeAsync();
    }
}
