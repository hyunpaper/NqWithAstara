using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class ReadinessSummaryContractTests
{
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
}
