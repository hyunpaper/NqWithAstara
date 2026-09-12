using System.Text.Json;
using Astra.Server.Application;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RealVsV5QueryServiceTests
{
    static RealVsV5QueryService Query(MemoryObservationStore observations, RealFillsService fills) =>
        new(new RecordingStore(), observations, fills, new MovableClock(Rf.FilledAt));

    [Fact]
    public async Task ReportJoinsStoredFillsWithTheDayObservations()
    {
        var gateway = new FakeOrderGateway([new TossOrderPage([Rf.Order("o1")], null, false)]);
        var fills = Rf.Service(gateway, new MemoryRealFillStore());
        await fills.RefreshAsync(Rf.TradingDate, CancellationToken.None);
        var observations = new MemoryObservationStore();
        await observations.AppendAsync(StructureObservationWriter.FileName(Rf.TradingDate), Rf.Observation(),
            CancellationToken.None);

        var (status, response) = await Query(observations, fills).GetAsync("2026-09-11", CancellationToken.None);

        Assert.Equal(200, status);
        Assert.NotNull(response);
        Assert.True(response.FillsCollected);
        Assert.Equal(1, response.Report.Matched);
        Assert.Empty(response.Limitations);
        Assert.Equal(1.25, response.Report.Rows.Single().DeviationAtr);
    }

    [Fact]
    public async Task MissingFillFileIsReportedAsALimitationNotAsZeroPerformance()
    {
        var fills = Rf.Service(new FakeOrderGateway([]), new MemoryRealFillStore());

        var (status, response) = await Query(new MemoryObservationStore(), fills)
            .GetAsync("2026-09-11", CancellationToken.None);

        Assert.Equal(200, status);
        Assert.NotNull(response);
        Assert.False(response.FillsCollected);
        Assert.Null(response.Report.MatchRatePercent);
        Assert.Contains(RealVsV5QueryService.NoFillsCollected, response.Limitations);
        Assert.Contains(RealVsV5QueryService.NoObservations, response.Limitations);
    }

    [Fact]
    public async Task InvalidDateIsRejected()
    {
        var fills = Rf.Service(new FakeOrderGateway([]), new MemoryRealFillStore());

        var (status, response) = await Query(new MemoryObservationStore(), fills)
            .GetAsync("어제", CancellationToken.None);

        Assert.Equal(400, status);
        Assert.Null(response);
    }
}

public sealed class RealVsV5EndpointTests(AstraHostFixture host) : IClassFixture<AstraHostFixture>
{
    [Fact]
    public async Task RealVsV5EndpointReturnsACamelCaseReportWithoutCollectedFills()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/validation/real-vs-v5?date=2026-09-11");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        foreach (var name in new[] { "tradingDate", "fillsCollected", "collectedAt", "observationLines",
            "observationFileFound", "report", "limitations" })
            Assert.True(root.TryGetProperty(name, out _), $"missing property: {name}");
        Assert.False(root.GetProperty("fillsCollected").GetBoolean());

        var report = root.GetProperty("report");
        Assert.Equal(0, report.GetProperty("realBuys").GetInt32());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("matchRatePercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("deviationMedianAtr").ValueKind);
        Assert.Empty(report.GetProperty("rows").EnumerateArray());
        Assert.Contains(root.GetProperty("limitations").EnumerateArray(),
            x => x.GetString() == RealVsV5QueryService.NoFillsCollected);
    }

    [Fact]
    public async Task RealVsV5EndpointRejectsAnInvalidDate()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/validation/real-vs-v5?date=2026-13-99");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
