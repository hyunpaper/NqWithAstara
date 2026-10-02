using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class ReadinessSummaryContractTests
{
    public static IEnumerable<object?[]> ReadinessStates() =>
    [
        ["disabled", false, (string?)null, "disabled"],
        ["stopped", false, (string?)null, "stopped"],
        ["marketClosed", false, (string?)null, "market_closed"],
        ["unavailable", true, (string?)null, "evaluation_failed"],
        ["unavailable", false, (string?)null, "input_unavailable"],
        ["warmup", false, (string?)null, "warmup"],
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
    public void SummaryUsesStoppedRuntimeStateBeforeStaleFailure()
    {
        var runtime = new MonitorRuntimeState();
        var service = D3.Service(StructureEngineMode.Active, new MemoryObservationStore(), runtime, new MovableClock(D3.At(45)));
        service.MarkFailedForContractTest(D3.Symbol);
        var row = JsonSerializer.SerializeToElement(service.Summary([D3.Symbol])).GetProperty("symbols")[0];
        Assert.Equal("stopped", row.GetProperty("status").GetString());
        Assert.Equal("stopped", row.GetProperty("readinessReason").GetString());
    }

    [Fact]
    public void SummaryUsesMarketClosedRuntimeState()
    {
        var runtime = new MonitorRuntimeState();
        var generation = runtime.CommitStart();
        runtime.TryCommit(generation, s => s with { Market = new MarketSession(false, "휴장", null, D3.At(0), D3.At(10)) });
        var service = D3.Service(StructureEngineMode.Active, new MemoryObservationStore(), runtime, new MovableClock(D3.At(45)));
        service.MarkFailedForContractTest(D3.Symbol);
        var row = JsonSerializer.SerializeToElement(service.Summary([D3.Symbol])).GetProperty("symbols")[0];
        Assert.Equal("marketClosed", row.GetProperty("status").GetString());
        Assert.Equal("market_closed", row.GetProperty("readinessReason").GetString());
    }

    [Fact]
    public async Task SummarySerializesInjectedCandidateStateAndSortedRejections()
    {
        var runtime = new MonitorRuntimeState();
        var generation = D3.StartedRuntime(runtime);
        var service = D3.Service(StructureEngineMode.Active, new MemoryObservationStore(), runtime, new MovableClock(D3.At(45)));
        await service.ObserveAsync(D3.Request(generation, 64), CancellationToken.None);
        Assert.True(service.TryGetPublished(D3.Symbol, out var view));
        var candidate = new StructureCandidateDto(EventId: "contract", Kind: "REBOUND", ZoneId: "zone",
            TriggerBarStart: D3.At(40), TriggerConfirmedAt: D3.At(41), StructureCutoff: D3.At(50), ExpiresAt: D3.At(70),
            State: "REJECTED", EntryQuality: null, EntryReference: 99m, InvalidationAnchor: null, Stop: null,
            Target: null, NetR: null, Plan: null, Components: [], RejectionCodes: ["TREND", "COST", "TREND"],
            Notes: [], CounterTrend: false, RetestConfirmed: false);
        service.PublishForContractTest(view with { CandidateSummary = "REJECTED", Candidates = [candidate] });
        var row = JsonSerializer.SerializeToElement(service.Summary([D3.Symbol])).GetProperty("symbols")[0];
        Assert.Equal("candidate_rejected", row.GetProperty("readinessReason").GetString());
        Assert.Equal(["COST", "TREND"], row.GetProperty("rejectionCodes").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }
    [Fact]
    public async Task SummarySeparatesShortCandidateCodesWhenPolicyCannotEnterShort()
    {
        var runtime = new MonitorRuntimeState();
        var generation = D3.StartedRuntime(runtime);
        var service = D3.Service(StructureEngineMode.Active, new MemoryObservationStore(), runtime, new MovableClock(D3.At(45)));
        await service.ObserveAsync(D3.Request(generation, 64), CancellationToken.None);
        Assert.True(service.TryGetPublished(D3.Symbol, out var view));
        StructureCandidateDto Candidate(string id, string kind, string[] codes, string state = "REJECTED") => new(EventId: id, Kind: kind, ZoneId: "zone",
            TriggerBarStart: D3.At(40), TriggerConfirmedAt: D3.At(41), StructureCutoff: D3.At(50), ExpiresAt: D3.At(70),
            State: state, EntryQuality: null, EntryReference: 99m, InvalidationAnchor: null, Stop: null,
            Target: null, NetR: null, Plan: null, Components: [], RejectionCodes: codes,
            Notes: [], CounterTrend: false, RetestConfirmed: false);
        service.PublishForContractTest(view with
        {
            CandidateSummary = "REJECTED",
            Candidates =
            [
                Candidate("long", "PULLBACK", ["NO_TARGET_STRUCTURE"]),
                Candidate("short-ready", "PULLBACK_SHORT", [], "READY"),
                Candidate("short-entered", "REBOUND_SHORT", [], "ENTERED")
            ]
        });
        var row = JsonSerializer.SerializeToElement(service.Summary([D3.Symbol])).GetProperty("symbols")[0];

        Assert.Equal(1, row.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0, row.GetProperty("readyCount").GetInt32());
        Assert.Equal(0, row.GetProperty("enteredCount").GetInt32());
        Assert.Equal(["NO_TARGET_STRUCTURE"], row.GetProperty("rejectionCodes").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(2, row.GetProperty("nonEntrySideCandidateCount").GetInt32());
        Assert.Equal(1, row.GetProperty("nonEntrySideReadyCount").GetInt32());
        Assert.Equal(1, row.GetProperty("nonEntrySideEnteredCount").GetInt32());
        Assert.Equal([], row.GetProperty("nonEntrySideRejectionCodes").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Theory]
    [InlineData("PULLBACK", null, true)]
    [InlineData("REBOUND", null, true)]
    [InlineData("BREAKOUT_SHORT", null, false)]
    [InlineData("PULLBACK_SHORT", 0.02, true)]
    [InlineData("PULLBACK_SHORT", 0d, true)]
    [InlineData("PULLBACK_SHORT", -0.02, true)]
    [InlineData("REBOUND_SHORT", 0.02, true)]
    [InlineData("PULLBACK_SHORT_EXTRA", 0.02, false)]
    [InlineData("UNKNOWN", null, false)]
    [InlineData("", null, false)]
    [InlineData(null, null, false)]
    public void CanEnterFollowsShortBorrowPolicy(string? kind, double? borrowPercent, bool expected)
    {
        var policy = StructurePolicy.Default with { ShortBorrowCostPercent = borrowPercent };
        Assert.Equal(expected, SetupKinds.CanEnter(kind, policy));
    }
}
