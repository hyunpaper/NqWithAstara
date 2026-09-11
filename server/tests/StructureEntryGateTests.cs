using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #61/#62 — 진입 게이트 배선. tick 판정 전달과 gate 안 시세 신선도·진입 마감 재확인을 고정한다.
/// D6 합성 데이터를 그대로 쓰며 임의 값이다. 실제 종목 추천이 아니다.
/// </summary>
public sealed class StructureEntryGateTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    sealed record Harness(StructureAnalysisService Structure, RecordingStore Store, MemoryObservationStore Observations,
        MonitorRuntimeState Runtime, GateClock Clock, CountingEntryPort Entries, long Generation, MarketSession Session);

    static Harness Build(StructureEngineMode mode, MarketSession? session = null)
    {
        var market = session ?? D6.Session;
        var clock = new GateClock(Fx.At(60));
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(Fx.Symbol, "테스트"));
        var observations = new MemoryObservationStore();
        var runtime = new MonitorRuntimeState();
        var entries = new CountingEntryPort(new StructuralTradeEntryService(store));
        var structure = new StructureAnalysisService(store, new StructureObservationWriter(observations, P), runtime,
            clock, new SilentDiagnostics(), new StructureEngineOptions(mode), P, entries);
        var generation = runtime.CommitStart();
        runtime.TryCommit(generation, s => s with { Market = market });
        return new Harness(structure, store, observations, runtime, clock, entries, generation, market);
    }

    static StructureObservationRequest Request(Harness harness, int minute, double? quotePrice = null) =>
        new(Fx.Symbol, harness.Generation, harness.Session, D6.Bars(minute), D6.Daily(),
            quotePrice ?? D6.QuotePrice(Fx.At(minute)), Fx.At(minute), null);

    static async Task ObserveAt(Harness harness, int minute, double? quotePrice = null, TimeSpan? gateDelay = null)
    {
        harness.Clock.Reset(Fx.At(minute), gateDelay ?? TimeSpan.Zero);
        await harness.Structure.ObserveAsync(Request(harness, minute, quotePrice), default);
    }

    static StructureAnalysisView Published(Harness harness)
    {
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        return view;
    }

    static string Describe(StructureAnalysisView view) =>
        $"summary={view.CandidateSummary} notes=[{string.Join(",", view.Notes)}] warnings=[{string.Join(",", view.Warnings)}] " +
        string.Join(" | ", view.Candidates.Select(x => $"{x.Kind}:{x.State}:rej[{string.Join(",", x.RejectionCodes)}]"));

    // ── #61 tick 판정 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(99.62, null)]
    [InlineData(99.60, null)]
    [InlineData(99.625, StructureAnalysisService.NotePriceTickUnsupported)]
    [InlineData(0.4321, StructureAnalysisService.NotePriceTickUnsupported)]
    public void QuotePriceOffTheTickGridIsNotSupported(double quote, string? expected) =>
        Assert.Equal(expected, StructureAnalysisService.PriceTickNote([], (decimal)quote, P));

    [Fact]
    public void ABarPriceOffTheTickGridIsNotSupported()
    {
        ImmutableArray<StructureBar> clean = [Fx.Bar(0, 100.00m, 100.10m, 99.90m, 100.05m)];
        ImmutableArray<StructureBar> dirty = [Fx.Bar(0, 100.00m, 100.1025m, 99.90m, 100.05m)];

        Assert.Null(StructureAnalysisService.PriceTickNote(clean, 100.05m, P));
        Assert.Equal(StructureAnalysisService.NotePriceTickUnsupported,
            StructureAnalysisService.PriceTickNote(dirty, 100.05m, P));
    }

    [Fact]
    public void NoPriceEvidenceBlocksInsteadOfAllowing()
    {
        Assert.Equal(StructureAnalysisService.NotePriceTickUnknown,
            StructureAnalysisService.PriceTickNote([], null, P));
        Assert.Equal(StructureAnalysisService.NotePriceTickUnknown,
            StructureAnalysisService.PriceTickNote([], 0m, P));
    }

    [Fact]
    public void OnlyTheMostRecentBarsDecideTheTick()
    {
        var bars = Enumerable.Range(0, StructureAnalysisService.PriceTickSampleBars + 1)
            .Select(i => i == 0
                ? Fx.Bar(i, 100.0001m, 100.0001m, 100.0001m, 100.0001m)
                : Fx.Bar(i, 100.00m, 100.10m, 99.90m, 100.05m))
            .ToImmutableArray();

        Assert.Null(StructureAnalysisService.PriceTickNote(bars, 100.05m, P));
        Assert.Equal(StructureAnalysisService.NotePriceTickUnsupported,
            StructureAnalysisService.PriceTickNote(bars.Take(2).ToImmutableArray(), 100.05m, P));
    }

    /// <summary>#41과 같은 미배선 재발 방지: Application이 판정을 넘기지 않으면 Domain 기본값(허용)으로 READY가 나온다.</summary>
    [Fact]
    public async Task AnUnsupportedTickBlocksTheNewEntryAndSurfacesTheRejection()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, quotePrice: 99.625);

        var view = Published(harness);
        Assert.Empty(harness.Store.Trades);
        Assert.Equal(0, harness.Entries.Calls);
        Assert.Contains(StructureAnalysisService.NotePriceTickUnsupported, view.Notes);
        Assert.Contains(StructureAnalysisService.NotePriceTickUnsupported, view.Warnings);

        var candidate = Assert.Single(view.Candidates);
        Assert.Equal("REJECTED", candidate.State);
        Assert.Contains(StructuralPlanner.UnsupportedPriceTick, candidate.RejectionCodes);
        Assert.Null(candidate.Plan);
    }

    [Fact]
    public async Task ASupportedTickLeavesTheEntryPathUnchanged()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65);

        var view = Published(harness);
        Assert.True(harness.Store.Trades.Count == 1, Describe(view));
        Assert.DoesNotContain(StructureAnalysisService.NotePriceTickUnsupported, view.Notes);
        Assert.DoesNotContain(StructureAnalysisService.NotePriceTickUnknown, view.Notes);
        Assert.All(view.Candidates, x => Assert.DoesNotContain(StructuralPlanner.UnsupportedPriceTick, x.RejectionCodes));
    }

    // ── #62 gate 안 재확인 ───────────────────────────────────────────────────

    [Fact]
    public async Task AQuoteThatGoesStaleBeforeTheGateNeverCommitsAnEntry()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, gateDelay: TimeSpan.FromSeconds(P.NewEntryQuoteMaxAgeSeconds + 5));

        var view = Published(harness);
        Assert.Empty(harness.Store.Trades);
        Assert.Equal(0, harness.Entries.Calls);
        Assert.Contains($"{StructureAnalysisService.NoteEntryGateRecheck}:{SetupDetector.BlockerStaleQuote}", view.Notes);
        Assert.Contains(view.Candidates, x => x.State == "READY");
    }

    [Fact]
    public async Task AQuoteStillFreshAtTheGateCommitsTheEntry()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, gateDelay: TimeSpan.FromSeconds(P.NewEntryQuoteMaxAgeSeconds - 1));

        var view = Published(harness);
        Assert.True(harness.Store.Trades.Count == 1, Describe(view));
        Assert.Contains(StructureAnalysisService.NoteEntryCommitted, view.Notes);
    }

    [Fact]
    public async Task CrossingTheEntryCutoffBeforeTheGateNeverCommitsAnEntry()
    {
        var closing = new MarketSession(true, "정규장", null, Fx.SessionStart,
            Fx.At(65 + P.EntryCutoffBeforeCloseMinutes));

        var allowed = Build(StructureEngineMode.Active, closing);
        await ObserveAt(allowed, 64);
        await ObserveAt(allowed, 65);
        Assert.True(allowed.Store.Trades.Count == 1, Describe(Published(allowed)));

        var blocked = Build(StructureEngineMode.Active, closing);
        await ObserveAt(blocked, 64);
        await ObserveAt(blocked, 65, gateDelay: TimeSpan.FromSeconds(1));

        var view = Published(blocked);
        Assert.Empty(blocked.Store.Trades);
        Assert.Equal(0, blocked.Entries.Calls);
        Assert.Contains($"{StructureAnalysisService.NoteEntryGateRecheck}:{SetupDetector.BlockerAfterEntryCutoff}",
            view.Notes);
    }

    [Fact]
    public async Task TheGateRecheckOnlyBlocksTheEntryAndStillPublishesTheObservation()
    {
        var harness = Build(StructureEngineMode.Active);
        await ObserveAt(harness, 64);
        var appendsAfterSeed = harness.Observations.Appends;
        await ObserveAt(harness, 65, gateDelay: TimeSpan.FromSeconds(P.NewEntryQuoteMaxAgeSeconds + 5));

        Assert.True(harness.Observations.Appends > appendsAfterSeed);
        Assert.Equal(Fx.At(64), Published(harness).LastCompletedBarStart);
    }

    // ── off 모드 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OffModeStaysInertOnBothPaths()
    {
        var harness = Build(StructureEngineMode.Off);
        await ObserveAt(harness, 64);
        await ObserveAt(harness, 65, quotePrice: 99.625, gateDelay: TimeSpan.FromMinutes(5));

        Assert.False(harness.Structure.TryGetPublished(Fx.Symbol, out _));
        Assert.Empty(harness.Store.Trades);
        Assert.Equal(0, harness.Entries.Calls);
        Assert.Equal(0, harness.Observations.Interactions);
    }
}

/// <summary>
/// 첫 조회는 스냅샷 시각, 이후 조회는 gate 시각을 돌려주는 TimeProvider. 계산·파일 I/O·gate 경합으로
/// 흘러간 시간을 결정적으로 재현한다.
/// </summary>
sealed class GateClock(DateTimeOffset now) : TimeProvider
{
    int _calls;
    DateTimeOffset _now = now;
    TimeSpan _gateDelay = TimeSpan.Zero;

    public void Reset(DateTimeOffset now, TimeSpan gateDelay)
    {
        _now = now;
        _gateDelay = gateDelay;
        _calls = 0;
    }

    public override DateTimeOffset GetUtcNow() => (_calls++ == 0 ? _now : _now + _gateDelay).ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
