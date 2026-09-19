using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

namespace Astra.Server.Tests;

public sealed class FeeRateCheckServiceTests
{
    static readonly DateTimeOffset Today = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MismatchBetweenAccountRateAndPolicyRaisesWarning()
    {
        var gateway = new FakeGateway([new(1, "BROKERAGE")], [new("US", .0015m, null, new(2027, 1, 1))]);
        var service = new FeeRateCheckService(gateway, StructurePolicy.Default, new FixedTimeProvider(Today), new FakeDiagnostics());

        await service.RunAsync(CancellationToken.None);

        Assert.Contains(service.Warnings, x => x.StartsWith("V5_FEE_RATE_MISMATCH:"));
    }

    [Fact]
    public async Task MatchingAccountRateRaisesNoWarning()
    {
        var gateway = new FakeGateway([new(1, "BROKERAGE")], [new("US", .001m, null, new(2027, 1, 1))]);
        var service = new FeeRateCheckService(gateway, StructurePolicy.Default, new FixedTimeProvider(Today), new FakeDiagnostics());

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(service.Warnings);
    }

    [Fact]
    public async Task ExpiringRateDoesNotRaiseWarning()
    {
        var gateway = new FakeGateway([new(1, "BROKERAGE")], [new("US", .001m, null, DateOnly.FromDateTime(Today.UtcDateTime).AddDays(2))]);
        var service = new FeeRateCheckService(gateway, StructurePolicy.Default, new FixedTimeProvider(Today), new FakeDiagnostics());

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(service.Warnings);
    }

    [Fact]
    public async Task MissingActiveRateDoesNotRaiseExpiryWarningAndLogsDiagnostic()
    {
        var gateway = new FakeGateway([new(1, "BROKERAGE")], [new("US", .001m, new(2026, 1, 1), new(2026, 9, 11))]);
        var diagnostics = new FakeDiagnostics();
        var service = new FeeRateCheckService(gateway, StructurePolicy.Default, new FixedTimeProvider(Today), diagnostics);

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(service.Warnings);
        Assert.Equal(1, diagnostics.MarketDataFailedCalls);
    }

    [Fact]
    public async Task AccountLookupFailureIsHarmlessAndOnlyLogsANote()
    {
        var gateway = new FakeGateway(null, []);
        var diagnostics = new FakeDiagnostics();
        var service = new FeeRateCheckService(gateway, StructurePolicy.Default, new FixedTimeProvider(Today), diagnostics);

        await service.RunAsync(CancellationToken.None);

        Assert.Empty(service.Warnings);
        Assert.Equal(1, diagnostics.MarketDataFailedCalls);
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    sealed class FakeDiagnostics : IMonitorDiagnostics
    {
        public int MarketDataFailedCalls;
        public void PollFailed(string scope, Exception exception) { }
        public void MarketDataFailed(string symbol, string operation, Exception exception) => MarketDataFailedCalls++;
    }

    sealed class FakeGateway(IReadOnlyList<TossAccount>? accounts, IReadOnlyList<TossCommission> commissions) : IMarketDataGateway
    {
        public Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<WatchItem>>([]);
        public Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
        public Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<Candle>>([]);
        public Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) => Task.FromResult((1d, DateTimeOffset.UtcNow));
        public Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) => Task.FromResult(new MarketSession(false, "closed", null, null, null));
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token");
        public Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossTrade>>([]);
        public Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>([]);
        public Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct) =>
            accounts is null ? throw new HttpRequestException("boom") : Task.FromResult(accounts);
        public Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct) => Task.FromResult(commissions);
        public Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct) => Task.FromResult(new TossOrderPage([], null, false));
        public Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct) => Task.FromResult<IReadOnlyList<TossHolding>>([]);
    }
}
