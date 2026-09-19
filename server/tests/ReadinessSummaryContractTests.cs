using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class ReadinessSummaryContractTests
{
    public static IEnumerable<object[]> ReadinessStates() =>
    [
        ["disabled", false, null, "disabled"],
        ["stopped", false, null, "stopped"],
        ["marketClosed", false, null, "market_closed"],
        ["unavailable", true, null, "evaluation_failed"],
        ["unavailable", false, null, "input_unavailable"],
        ["warmup", false, null, "warmup"],
        ["available", false, "READY", "ready"],
        ["available", false, "ENTERED", "entered"],
        ["available", false, "WAIT", "candidate_inactive"],
        ["available", false, "REJECTED", "candidate_rejected"],
        ["available", false, "INVALIDATED", "candidate_invalidated"],
        ["available", false, "EXPIRED", "candidate_expired"],
        ["available", false, "OTHER", "evaluated_waiting"]
    ];

    [Theory]
    [MemberData(nameof(ReadinessStates))]
    public void PublicReadinessContractPreservesStateAndFailure(string status, bool failed,
        string? candidate, string expected)
    {
        Assert.Equal(expected, StructureAnalysisService.ReadinessReasonForContract(status, failed, candidate));
    }

    [Fact]
    public void OffSummaryExposesDisabledReadinessAndAdditiveFields()
    {
        var runtime = new MonitorRuntimeState();
        var service = D3.Service(StructureEngineMode.Off, new MemoryObservationStore(), runtime, new MovableClock(D3.At(45)));
        var root = JsonSerializer.SerializeToElement(service.Summary([D3.Symbol]));
        var row = root.GetProperty("symbols")[0];

        Assert.Equal("disabled", row.GetProperty("readinessReason").GetString());
        Assert.Equal("disabled", row.GetProperty("status").GetString());
        Assert.Equal(0, row.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0, row.GetProperty("readyCount").GetInt32());
        Assert.Equal(0, row.GetProperty("enteredCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, row.GetProperty("rejectionCodes").ValueKind);
    }

    [Fact]
    public void RejectionCodesContractIsDistinctAndOrdinalSorted()
    {
        var codes = new[] { "TREND", "COST", "TREND", "COST" }
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(["COST", "TREND"], codes);
    }
}
