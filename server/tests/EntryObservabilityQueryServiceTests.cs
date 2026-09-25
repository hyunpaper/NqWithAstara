using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class EntryObservabilityQueryServiceTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyTradeHistoryStillReportsTheFirstGateAndDataDelay()
    {
        var service = Structure();
        service.PublishForContractTest(View([], [SetupDetector.BlockerStaleQuote], quoteAt: Now.AddSeconds(-95)));

        var report = new EntryObservabilityQueryService(service, new MovableClock(Now)).Get();

        var row = Assert.Single(report.Symbols);
        Assert.Equal(0, report.CandidateCount);
        Assert.Equal("NO_CANDIDATE", row.FinalDisposition);
        Assert.Equal(SetupDetector.BlockerStaleQuote, row.FirstGateReason);
        Assert.Equal(95, row.DataDelaySeconds);
    }

    [Fact]
    public void RejectedCandidateSeparatesDuplicateReasonsFromTheFinalRejectionSet()
    {
        var service = Structure();
        var candidate = new StructureCandidateDto("event-1", "BREAKOUT", "zone-1", Now.AddMinutes(-2),
            Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(3), "WAIT", 62, 100m, null, null, null,
            null, null, [], [StructuralLifecycle.CodeDuplicateGuard, SetupDetector.BlockerStaleQuote], [], false, false);
        service.PublishForContractTest(View([candidate], []));

        var row = Assert.Single(new EntryObservabilityQueryService(service, new MovableClock(Now)).Get().Symbols);

        Assert.Equal("REJECTED", row.FinalDisposition);
        Assert.Equal(SetupDetector.BlockerStaleQuote, row.FirstGateReason);
        Assert.Equal([StructuralLifecycle.CodeDuplicateGuard], row.DuplicateReasons);
        Assert.Contains(StructuralLifecycle.CodeDuplicateGuard, row.RejectionReasons);
    }

    static StructureAnalysisService Structure()
    {
        var store = new RecordingStore();
        var runtime = new MonitorRuntimeState();
        return new StructureAnalysisService(store,
            new StructureObservationWriter(new MemoryObservationStore(), StructurePolicy.Default), runtime,
            new MovableClock(Now), new SilentDiagnostics(), new StructureEngineOptions(StructureEngineMode.Active),
            StructurePolicy.Default);
    }

    static StructureAnalysisView View(StructureCandidateDto[] candidates, string[] readyBlockers,
        DateTimeOffset? quoteAt = null)
    {
        var quality = new StructureQualityDto([], [], [], [], [], readyBlockers);
        return new("SOXL", "active", "READY", StructureAnalysisService.EntryOwnerV5,
            StructurePolicy.Default.Version, StructurePolicy.Default.Version, StructurePolicy.Default.PolicyHash,
            Now.AddHours(-1), Now.AddHours(5), Now.AddMinutes(-1), Now.AddMinutes(-2), quoteAt ?? Now, 100m,
            Now, 1, candidates.Length == 0 ? "WAIT" : "REJECTED", candidates.FirstOrDefault()?.EventId,
            null, [], candidates, quality, [], []);
    }
}
