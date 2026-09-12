using System.Text.Json;
using Astra.Server;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RealFillsServiceTests
{
    [Fact]
    public async Task CursorPaginationWalksEveryPage()
    {
        var gateway = new FakeOrderGateway([
            new TossOrderPage([Rf.Order("o1"), Rf.Order("o2", minute: 1)], "c1", true),
            new TossOrderPage([Rf.Order("o3", minute: 2)], "c2", true),
            new TossOrderPage([Rf.Order("o4", minute: 3)], null, false)
        ]);
        var store = new MemoryRealFillStore();

        var result = await Rf.Service(gateway, store).RefreshAsync(Rf.TradingDate, CancellationToken.None);

        Assert.True(result.Collected);
        Assert.Equal(3, result.Pages);
        Assert.Equal(4, result.Fills);
        Assert.Equal([null, "c1", "c2"], gateway.Cursors);
        Assert.All(gateway.Limits, x => Assert.Equal(100, x));
    }

    [Fact]
    public async Task PaginationStopsWhenThePageHasNoNextCursor()
    {
        var gateway = new FakeOrderGateway([
            new TossOrderPage([Rf.Order("o1")], null, true),
            new TossOrderPage([Rf.Order("o2", minute: 1)], null, false)
        ]);

        var result = await Rf.Service(gateway, new MemoryRealFillStore())
            .RefreshAsync(Rf.TradingDate, CancellationToken.None);

        Assert.Equal(1, result.Pages);
        Assert.Equal(1, result.Fills);
    }

    [Fact]
    public async Task OrdersAreQueriedOverTwoKstDaysAndFilteredByTheNewYorkTradingDate()
    {
        var beforeMidnightKst = new DateTimeOffset(2026, 9, 11, 23, 30, 0, Rf.Kst);
        var afterMidnightKst = new DateTimeOffset(2026, 9, 12, 1, 4, 0, Rf.Kst);
        var nextSessionKst = new DateTimeOffset(2026, 9, 12, 23, 0, 0, Rf.Kst);
        var gateway = new FakeOrderGateway([
            new TossOrderPage([
                Rf.Order("o1", filledAt: beforeMidnightKst),
                Rf.Order("o2", filledAt: afterMidnightKst),
                Rf.Order("o3", filledAt: nextSessionKst)
            ], null, false)
        ]);
        var store = new MemoryRealFillStore();

        var result = await Rf.Service(gateway, store).RefreshAsync(Rf.TradingDate, CancellationToken.None);

        Assert.Equal([(Rf.TradingDate, Rf.TradingDate.AddDays(1))], gateway.Ranges);
        Assert.Equal(2, result.Fills);
        using var json = JsonDocument.Parse(store.Files["2026-09-11.json"]);
        Assert.Equal([beforeMidnightKst, afterMidnightKst],
            json.RootElement.GetProperty("fills").EnumerateArray()
                .Select(x => x.GetProperty("filledAt").GetDateTimeOffset()).ToArray());
    }

    [Fact]
    public async Task OnlyFilledOrdersWithAnExecutionAreStored()
    {
        var gateway = new FakeOrderGateway([
            new TossOrderPage([
                Rf.Order("o1"), Rf.Order("o2", status: "CANCELLED", minute: 1),
                Rf.Order("o3", executed: false, minute: 2)
            ], null, false)
        ]);

        var result = await Rf.Service(gateway, new MemoryRealFillStore())
            .RefreshAsync(Rf.TradingDate, CancellationToken.None);

        Assert.Equal(1, result.Fills);
    }

    [Fact]
    public async Task StoredFileKeepsOnlyTheComparisonFieldsAndNoAccountOrOrderIdentity()
    {
        var gateway = new FakeOrderGateway([new TossOrderPage([Rf.Order("order-1")], null, false)]);
        var store = new MemoryRealFillStore();

        await Rf.Service(gateway, store).RefreshAsync(Rf.TradingDate, CancellationToken.None);

        var content = store.Files["2026-09-11.json"];
        foreach (var forbidden in new[] { "order-1", "orderId", "accountSeq", "filledAmount", "1002.5", "balance" })
            Assert.DoesNotContain(forbidden, content, StringComparison.OrdinalIgnoreCase);

        using var json = JsonDocument.Parse(content);
        var fill = json.RootElement.GetProperty("fills").EnumerateArray().Single();
        Assert.Equal(["symbol", "side", "filledAt", "averageFilledPrice", "filledQuantity", "commission", "orderType"],
            fill.EnumerateObject().Select(x => x.Name).ToArray());
    }

    [Fact]
    public async Task SessionEndCollectionRunsOnceForTheSameTradingDate()
    {
        var gateway = new FakeOrderGateway([new TossOrderPage([Rf.Order("o1")], null, false)]);
        var service = Rf.Service(gateway, new MemoryRealFillStore());

        Assert.NotNull(await service.CollectOnSessionEndAsync(Rf.TradingDate, CancellationToken.None));
        Assert.Null(await service.CollectOnSessionEndAsync(Rf.TradingDate, CancellationToken.None));
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task GatewayFailureIsHarmlessAndOnlyLogsANote()
    {
        var gateway = new FakeOrderGateway([]) { Failure = new HttpRequestException("boom") };
        var store = new MemoryRealFillStore();
        var diagnostics = new FakeRealFillDiagnostics();

        var result = await Rf.Service(gateway, store, diagnostics).RefreshAsync(Rf.TradingDate, CancellationToken.None);

        Assert.False(result.Collected);
        Assert.Equal("REAL_FILLS_FAILED", result.Note);
        Assert.Empty(store.Files);
        Assert.Equal(1, diagnostics.Notes);
    }

    [Fact]
    public async Task MissingBrokerageAccountStopsCollectionWithoutAnOrderCall()
    {
        var gateway = new FakeOrderGateway([]) { AccountList = [new TossAccount(1, "SAVINGS")] };
        var store = new MemoryRealFillStore();

        var result = await Rf.Service(gateway, store).RefreshAsync(Rf.TradingDate, CancellationToken.None);

        Assert.False(result.Collected);
        Assert.Equal("REAL_FILLS_NO_ACCOUNT", result.Note);
        Assert.Equal(0, gateway.Calls);
        Assert.Empty(store.Files);
    }
}
