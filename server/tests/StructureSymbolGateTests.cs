using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureSymbolGateTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static StockInfo Info(string securityType, bool common = true, decimal? leverage = null) =>
        new(D3.Symbol, "테스트", securityType, "NASDAQ", common, "NORMAL", leverage, 1_000_000m);

    // ── 게이트 판정 (§16A) ──

    [Fact]
    public void StockAndDepositaryReceiptAreAllowedWithoutANote()
    {
        Assert.Null(SymbolEligibility.Note(Info("STOCK"), P));
        Assert.Null(SymbolEligibility.Note(Info("DEPOSITARY_RECEIPT"), P));
        Assert.Null(SymbolEligibility.Note(Info("STOCK", leverage: 1m), P));
        Assert.Null(SymbolEligibility.Note(Info("STOCK", leverage: -1m), P));
    }

    [Fact]
    public void EtfIsBlocked()
        => Assert.Equal(SymbolEligibility.CodeTypeUnsupported, SymbolEligibility.Note(Info("ETF"), P));

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(3)]
    public void LeveragedSymbolsAreBlocked(int leverage)
        => Assert.Equal(SymbolEligibility.CodeTypeUnsupported,
            SymbolEligibility.Note(Info("STOCK", leverage: leverage), P));

    [Fact]
    public void NonCommonSharesAreBlocked()
        => Assert.Equal(SymbolEligibility.CodeTypeUnsupported,
            SymbolEligibility.Note(Info("STOCK", common: false), P));

    [Fact]
    public void MissingMetadataIsANoteAndNotABlock()
    {
        Assert.Equal(SymbolEligibility.NoteMetaUnknown, SymbolEligibility.Note(null, P));
        Assert.NotEqual(SymbolEligibility.CodeTypeUnsupported, SymbolEligibility.Note(null, P));
    }

    [Fact]
    public void PolicyOwnsTheAllowedSecurityTypes()
    {
        Assert.Equal(["STOCK", "DEPOSITARY_RECEIPT"], P.AllowedSecurityTypes.ToArray());
        Assert.Contains("\"AllowedSecurityTypes\":[\"STOCK\",\"DEPOSITARY_RECEIPT\"]", P.CanonicalJson);
        Assert.NotEqual(P.PolicyHash, (P with { AllowedSecurityTypes = ["STOCK"] }).PolicyHash);
        Assert.Null(SymbolEligibility.Note(Info("ETF"), P with { AllowedSecurityTypes = ["STOCK", "ETF"] }));
    }

    // ── 후보 차단 (§8) ──

    [Fact]
    public void BlockedSymbolTurnsAReadyCandidateIntoARejection()
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 25; m++) bars.Add(new(Fx.At(m), Fx.At(m + 1), 100m, 100.10m, 99.90m, 100m, 1000));
        bars.Add(new(Fx.At(25), Fx.At(26), 99.85m, 99.90m, 99.30m, 99.35m, 1000));
        bars.Add(new(Fx.At(26), Fx.At(27), 99.35m, 99.45m, 99.15m, 99.25m, 1000));
        bars.Add(new(Fx.At(27), Fx.At(28), 99.30m, 99.55m, 99.35m, 99.50m, 1000));
        bars.Add(new(Fx.At(28), Fx.At(29), 99.50m, 99.70m, 99.50m, 99.60m, 1000));
        bars.Add(new(Fx.At(29), Fx.At(30), 99.60m, 99.65m, 99.55m, 99.60m, 1000));
        bars.Add(new(Fx.At(30), Fx.At(31), 99.60m, 99.85m, 99.58m, 99.80m, 2000));
        ImmutableArray<PriceZone> zones = [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)];
        ImmutableArray<TouchEpisode> episodes = [D2.Episode("support-zone", 25, 28)];

        SetupDetectionRequest Request(ImmutableArray<string> blockers) =>
            SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, Fx.At(31), Fx.At(31),
                bars.ToImmutable(), zones, episodes, D2.Trend(), .20, 100.00m, Fx.At(31),
                D2.Quote(99.99m, 100.01m, 31), blockers);

        var allowed = SetupDetector.Detect(Request([]), P);
        Assert.Equal(CandidateDisposition.Ready, allowed.Candidates.Single(x => x.Kind == SetupKind.Pullback).Disposition);

        var blocked = SetupDetector.Detect(Request([SymbolEligibility.CodeTypeUnsupported]), P);
        var candidate = blocked.Candidates.Single(x => x.Kind == SetupKind.Pullback);
        Assert.NotEqual(CandidateDisposition.Ready, candidate.Disposition);
        Assert.Contains(SymbolEligibility.CodeTypeUnsupported, candidate.RejectionCodes);
        Assert.Contains(SymbolEligibility.CodeTypeUnsupported, blocked.ReadyBlockers);
    }

    // ── 메타 캐시 ──

    static (SymbolMetadataService Metadata, ScriptedGateway Gateway) Meta(Func<string, StockInfo[]>? source = null)
    {
        var gateway = new ScriptedGateway(D3.Session, () => [], () => (100.0, D3.At(0))) { StockInfoSource = source };
        return (new SymbolMetadataService(gateway, new SilentDiagnostics()), gateway);
    }

    [Fact]
    public async Task MetadataIsFetchedOnceInASingleBatch()
    {
        var (metadata, gateway) = Meta(_ => [Info("STOCK") with { Symbol = "AAA" }, Info("ETF") with { Symbol = "BBB" }]);
        await metadata.EnsureAsync(["AAA", "bbb"], default);
        await metadata.EnsureAsync(["AAA", "BBB"], default);

        Assert.Equal(["AAA,BBB"], gateway.StockInfoRequests);
        Assert.Equal("STOCK", metadata.Get("aaa")!.SecurityType);
        Assert.Equal("ETF", metadata.Get("BBB")!.SecurityType);
    }

    [Fact]
    public async Task OnlyNewSymbolsAreRequestedWhenTheWatchlistGrows()
    {
        var (metadata, gateway) = Meta(symbols => symbols.Split(',').Select(x => Info("STOCK") with { Symbol = x }).ToArray());
        await metadata.EnsureAsync(["AAA"], default);
        await metadata.EnsureAsync(["AAA", "CCC"], default);

        Assert.Equal(["AAA", "CCC"], gateway.StockInfoRequests);
    }

    [Fact]
    public async Task AFailedLookupLeavesTheSymbolMissingAndIsRetried()
    {
        var calls = 0;
        var (metadata, gateway) = Meta(_ => ++calls == 1 ? throw new HttpRequestException("boom") : [Info("STOCK")]);
        await metadata.EnsureAsync([D3.Symbol], default);
        Assert.Null(metadata.Get(D3.Symbol));

        await metadata.EnsureAsync([D3.Symbol], default);
        Assert.Equal("STOCK", metadata.Get(D3.Symbol)!.SecurityType);
        Assert.Equal(2, gateway.StockInfoRequests.Count);
    }

    [Fact]
    public async Task ASymbolThatTheApiDoesNotReturnIsNotRequestedAgain()
    {
        var (metadata, gateway) = Meta(_ => []);
        await metadata.EnsureAsync([D3.Symbol], default);
        await metadata.EnsureAsync([D3.Symbol], default);

        Assert.Null(metadata.Get(D3.Symbol));
        Assert.Single(gateway.StockInfoRequests);
    }

    // ── 관측·API 노출 ──

    sealed record Harness(MonitorPollingService Poller, StructureAnalysisService Structure,
        MemoryObservationStore Observations, RecordingStore Store);

    static Harness Build(Func<string, StockInfo[]>? source)
    {
        const int bars = 46;
        var clock = new MovableClock(D3.At(45));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(D3.Symbol, "테스트"));
        var observations = new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var gateway = new ScriptedGateway(D3.Session, () => D3.Candles(bars), () => (100.0, clock.Now),
            () => D3.Daily()) { StockInfoSource = source };
        var metadata = new SymbolMetadataService(gateway, diagnostics);
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P),
            runtime, clock, diagnostics, new StructureEngineOptions(StructureEngineMode.Shadow), P, null, null, metadata);
        var poller = new MonitorPollingService(store, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure, null, null, metadata);
        runtime.CommitStart();
        return new Harness(poller, structure, observations, store);
    }

    static async Task<Harness> Polled(Func<string, StockInfo[]>? source)
    {
        var harness = Build(source);
        await harness.Poller.PollAsync(default);
        return harness;
    }

    [Fact]
    public async Task BlockedSymbolIsExposedInObservationsAndTheStructureApi()
    {
        var harness = await Polled(_ => [Info("ETF")]);

        Assert.Contains(harness.Observations.AllLines, x => x.Contains(SymbolEligibility.CodeTypeUnsupported, StringComparison.Ordinal));
        var (status, response) = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.Equal(200, status);
        Assert.Contains(SymbolEligibility.CodeTypeUnsupported, response!.Analysis!.Notes);
        Assert.Contains(SymbolEligibility.CodeTypeUnsupported, response.Analysis.Warnings);
    }

    [Fact]
    public async Task MissingMetadataOnlyLeavesANoteAndNeverAWarning()
    {
        var harness = await Polled(_ => []);

        var (_, response) = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.Contains(SymbolEligibility.NoteMetaUnknown, response!.Analysis!.Notes);
        Assert.DoesNotContain(SymbolEligibility.NoteMetaUnknown, response.Analysis.Warnings);
        Assert.DoesNotContain(SymbolEligibility.CodeTypeUnsupported, response.Analysis.Notes);
    }

    [Fact]
    public async Task AllowedSymbolLeavesNoSymbolDiagnostic()
    {
        var harness = await Polled(_ => [Info("DEPOSITARY_RECEIPT")]);

        var (_, response) = await harness.Structure.GetAsync(D3.Symbol, default);
        Assert.DoesNotContain(response!.Analysis!.Notes, SymbolEligibility.IsDiagnostic);
        Assert.DoesNotContain(response.Analysis.Warnings, SymbolEligibility.IsDiagnostic);
    }
}
