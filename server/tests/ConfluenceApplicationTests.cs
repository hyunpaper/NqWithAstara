using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>컨플루언스 관측 additive 계약과 조회 API (C1·C4, #167).</summary>
public sealed class ConfluenceApplicationTests
{
    const int Bars = 46;
    const int NowMinute = 45;

    sealed class FakeBenchmark(IReadOnlyList<Candle> bars) : IBenchmarkBarSource
    {
        public string Symbol => "QQQ";
        public IReadOnlyList<Candle> Bars { get; } = bars;
    }

    static ConfluenceService Service(RecordingStore store, TimeProvider clock,
        IBenchmarkBarSource? benchmark = null, ConfluenceWeights? weights = null) =>
        new(store, clock, ConfluencePolicy.Default, weights, benchmark);

    static RecordingStore Watched()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem(D3.Symbol, D3.Symbol));
        return store;
    }

    [Fact]
    public async Task FullObservationCarriesConfluenceAndSummaryDoesNot()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = Watched();
        var confluence = Service(store, clock, new FakeBenchmark(D3.Candles(Bars)));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store, confluence);
        var generation = D3.StartedRuntime(runtime);

        await service.ObserveAsync(D3.Request(generation, Bars), default);
        clock.Now = D3.At(NowMinute + 1);
        await service.ObserveAsync(D3.Request(generation, Bars + 1, quoteAt: D3.At(NowMinute + 1)), default);

        var records = observations.AllLines.Select(x => JsonDocument.Parse(x).RootElement).ToArray();
        Assert.Equal("full", records[0].GetProperty("detail").GetString());
        var block = records[0].GetProperty("confluence");
        Assert.Equal(JsonValueKind.Object, block.ValueKind);
        Assert.Equal(ConfluencePolicy.Default.PolicyHash, block.GetProperty("policyHash").GetString());
        Assert.Equal(ConfluenceAggregator.DefaultWeightsVersion, block.GetProperty("weightsVersion").GetString());
        Assert.Equal(TechniqueNames.All.Length, block.GetProperty("techniques").GetArrayLength());

        Assert.Equal("summary", records[1].GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("confluence").ValueKind);
    }

    [Fact]
    public async Task ConfluenceIsAbsentWhenTheLayerIsNotWired()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);

        await service.ObserveAsync(D3.Request(D3.StartedRuntime(runtime), Bars), default);

        var record = JsonDocument.Parse(observations.AllLines.Single()).RootElement;
        Assert.Equal("full", record.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("confluence").ValueKind);
    }

    [Fact]
    public async Task StructureResponseCarriesTheAdditiveConfluenceSummary()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = Watched();
        var confluence = Service(store, clock, new FakeBenchmark(D3.Candles(Bars)));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store, confluence);
        var generation = D3.StartedRuntime(runtime);
        await service.ObserveAsync(D3.Request(generation, Bars), default);

        var (status, response) = await service.GetAsync(D3.Symbol, default);

        Assert.Equal(200, status);
        Assert.NotNull(response!.Confluence);
        Assert.Equal(ConfluenceAggregator.DefaultWeightsVersion, response.Confluence!.WeightsVersion);
        Assert.True(confluence.TryGet(D3.Symbol, out var cached));
        Assert.Equal(cached.Score, response.Confluence.Score);
        Assert.Equal(cached.WarmupCount, response.Confluence.WarmupCount);
    }

    [Fact]
    public async Task StateSummaryRowCarriesTheCachedConfluenceScore()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = Watched();
        var confluence = Service(store, clock, new FakeBenchmark(D3.Candles(Bars)));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store, confluence);
        await service.ObserveAsync(D3.Request(D3.StartedRuntime(runtime), Bars), default);

        var row = JsonDocument.Parse(JsonSerializer.Serialize(service.Summary([D3.Symbol]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.GetProperty("symbols")[0];

        Assert.True(confluence.TryGet(D3.Symbol, out var cached));
        var block = row.GetProperty("confluence");
        Assert.Equal(JsonValueKind.Object, block.ValueKind);
        Assert.Equal(cached.Score, block.GetProperty("score").GetDouble());
        Assert.Equal(cached.WarmupCount, block.GetProperty("warmupCount").GetInt32());
        Assert.Equal(cached.WeightsVersion, block.GetProperty("weightsVersion").GetString());
        Assert.Equal(cached.BarEnd, block.GetProperty("barEnd").GetDateTimeOffset());
    }

    [Fact]
    public void StateSummaryRowConfluenceIsNullWithoutACachedScore()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = Watched();
        var confluence = Service(store, clock, new FakeBenchmark(D3.Candles(Bars)));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store, confluence);

        var row = JsonDocument.Parse(JsonSerializer.Serialize(service.Summary([D3.Symbol]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.GetProperty("symbols")[0];

        Assert.False(confluence.TryGet(D3.Symbol, out _));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("confluence").ValueKind);
    }

    [Fact]
    public async Task StateSummaryRowConfluenceIsNullWhenTheLayerIsNotWired()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock);
        await service.ObserveAsync(D3.Request(D3.StartedRuntime(runtime), Bars), default);

        var row = JsonDocument.Parse(JsonSerializer.Serialize(service.Summary([D3.Symbol]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.GetProperty("symbols")[0];

        Assert.Equal(JsonValueKind.Null, row.GetProperty("confluence").ValueKind);
    }

    [Fact]
    public async Task QueryReturnsTheLatestScoreWithEveryTechnique()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var runtime = new MonitorRuntimeState();
        var observations = new MemoryObservationStore();
        var store = Watched();
        var confluence = Service(store, clock, new FakeBenchmark(D3.Candles(Bars)));
        var service = D3.Service(StructureEngineMode.Shadow, observations, runtime, clock, store, confluence);
        await service.ObserveAsync(D3.Request(D3.StartedRuntime(runtime), Bars), default);

        var (status, response) = await confluence.GetAsync("test", default);

        Assert.Equal(200, status);
        Assert.Equal(ConfluenceService.StatusReady, response!.Status);
        Assert.Equal(D3.Symbol, response.Symbol);
        Assert.Equal(ConfluencePolicy.Default.PolicyHash, response.PolicyHash);
        Assert.Equal(TechniqueNames.All, response.Techniques.Select(x => x.Name));
        Assert.Equal(D3.At(Bars - 1), response.BarEnd);
        Assert.All(response.Techniques, x => Assert.InRange(x.Confidence, 0, 1));
        Assert.All(response.Techniques, x => Assert.InRange(x.Score, -1, 1));
    }

    [Fact]
    public async Task QueryRejectsUnknownAndMalformedSymbols()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var confluence = Service(Watched(), clock);

        Assert.Equal(400, (await confluence.GetAsync("not a symbol", default)).HttpStatus);
        Assert.Equal(404, (await confluence.GetAsync("OTHER", default)).HttpStatus);

        var (status, response) = await confluence.GetAsync(D3.Symbol, default);
        Assert.Equal(200, status);
        Assert.Equal(ConfluenceService.StatusWarmup, response!.Status);
        Assert.Null(response.Score);
        Assert.Empty(response.Techniques);
    }

    [Fact]
    public void QuoteSnapshotsAreKeptPerSessionAndCappedByThePollWindow()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var confluence = Service(Watched(), clock);
        for (var i = 0; i < 5; i++)
            confluence.ObserveQuote(D3.Symbol, D3.SessionStart,
                new StructureLiquidity(10m, 11m, D3.At(i), 300 + i, 100));

        var bars = D3.Candles(Bars).Take(Bars - 1).Select(Bar).ToImmutableArray();
        var score = confluence.Evaluate(D3.Symbol, D3.SessionStart, bars, null);
        var obi = score!.Contributing.Single(x => x.Name == TechniqueNames.OrderBookImbalance);
        Assert.True(obi.Contributing);
        Assert.Equal(3, obi.Evidence["samples"]);

        confluence.ObserveQuote(D3.Symbol, D3.SessionStart.AddDays(1),
            new StructureLiquidity(10m, 11m, D3.At(9), 100, 300));
        var next = confluence.Evaluate(D3.Symbol, D3.SessionStart, bars, null);
        Assert.False(next!.Contributing.Single(x => x.Name == TechniqueNames.OrderBookImbalance).Contributing);
    }

    [Fact]
    public void ClearAndRemoveDropTheCachedScore()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var confluence = Service(Watched(), clock);
        var bars = D3.Candles(Bars).Take(Bars - 1).Select(Bar).ToImmutableArray();

        Assert.NotNull(confluence.Evaluate(D3.Symbol, D3.SessionStart, bars, null));
        confluence.Remove(D3.Symbol);
        Assert.False(confluence.TryGet(D3.Symbol, out _));

        Assert.NotNull(confluence.Evaluate(D3.Symbol, D3.SessionStart, bars, null));
        confluence.Clear();
        Assert.False(confluence.TryGet(D3.Symbol, out _));
        Assert.Null(confluence.Summary(D3.Symbol));
    }

    [Fact]
    public void EvaluateReturnsNullWithoutAnyCompletedBar()
    {
        var confluence = Service(Watched(), new MovableClock(D3.At(NowMinute)));
        Assert.Null(confluence.Evaluate(D3.Symbol, D3.SessionStart, [], null));
    }

    [Fact]
    public void InjectedWeightsReachTheCachedScore()
    {
        var weights = new ConfluenceWeights("measured.1",
            ImmutableDictionary<string, double>.Empty.Add(TechniqueNames.Macd, 4.0));
        var confluence = Service(Watched(), new MovableClock(D3.At(NowMinute)), weights: weights);
        var bars = D3.Candles(Bars).Take(Bars - 1).Select(Bar).ToImmutableArray();

        var score = confluence.Evaluate(D3.Symbol, D3.SessionStart, bars, null);

        Assert.Equal("measured.1", score!.WeightsVersion);
        Assert.Equal(4.0, score.Contributing.Single(x => x.Name == TechniqueNames.Macd).Weight);
    }

    /// <summary>2군 입력 배선 — 전일 일봉·체결 테이프·과거 세션 거래량 곡선 (C3-2, #170).</summary>
    [Fact]
    public void SecondTierInputsReachTheTechniquesFromTheApplicationLayer()
    {
        var clock = new MovableClock(D3.At(NowMinute));
        var tape = new TickFlowTape(clock);
        var confluence = new ConfluenceService(Watched(), clock, ConfluencePolicy.Default, null, null, tape);
        for (var i = 0; i < 30; i++)
            tape.RecordTick(new TossTrade(D3.Symbol, 100m + i * .01m, 10m, D3.At(NowMinute - 5).AddSeconds(i),
                "USD"));
        confluence.ObserveQuote(D3.Symbol, D3.SessionStart,
            new StructureLiquidity(99m, 101m, D3.At(NowMinute - 6), 200, 100));

        var bars = D3.Candles(Bars).Take(Bars - 1).Select(Bar).ToImmutableArray();
        var score = confluence.Evaluate(D3.Symbol, D3.SessionStart, bars, D3.Daily());

        var breakout = score!.Contributing.Single(x => x.Name == TechniqueNames.VolatilityBreakout);
        Assert.True(breakout.Contributing);
        Assert.True(breakout.Evidence["previousRange"] > 0);

        var delta = score.Contributing.Single(x => x.Name == TechniqueNames.LeeReadyDelta);
        Assert.Equal(30, delta.Evidence["samples"]);
        Assert.Equal(30, delta.Evidence["quotedSamples"]);
        Assert.True(delta.Contributing);

        Assert.True(score.Contributing.Single(x => x.Name == TechniqueNames.RelativeVolumeDaily).Warmup);
    }

    [Fact]
    public async Task VolumeProfilesAreBuiltFromTheStoredBarsOfEarlierSessions()
    {
        var store = new MemoryBarStore();
        store.Seed("2026-09-09", D3.Symbol, 60);
        store.Seed("2026-09-10", D3.Symbol, 60);
        var profiles = new BarStoreVolumeProfiles(store, 20);
        var session = new DateOnly(2026, 9, 11);

        var loaded = profiles.Profiles(D3.Symbol, session);
        for (var attempt = 0; attempt < 100 && loaded.Length < 2; attempt++)
        {
            await Task.Delay(20);
            loaded = profiles.Profiles(D3.Symbol, session);
        }

        Assert.Equal(2, loaded.Length);
        Assert.Equal(new DateOnly(2026, 9, 9), loaded[0].Date);
        Assert.Equal(60, loaded[1].Cumulative.Length);
        Assert.True(loaded[1].At(60) > loaded[1].At(30));
    }

    sealed class MemoryBarStore : IBarStore
    {
        readonly Dictionary<(string Day, string Symbol), List<string>> _files = [];

        public void Seed(string day, string symbol, int count)
        {
            var start = DateTimeOffset.Parse($"{day}T13:30:00Z");
            _files[(day, symbol)] = Enumerable.Range(0, count)
                .Select(i => JsonSerializer.Serialize(new
                {
                    t = start.AddMinutes(i).UtcDateTime, o = 100.0, h = 100.5, l = 99.5, c = 100.2, v = 1000.0
                }))
                .ToList();
        }

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Select(x => x.Day).Distinct().Order().ToList());

        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _files.Keys.Where(x => x.Day == day).Select(x => x.Symbol).ToList());

        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(
                _files.TryGetValue((day, symbol), out var lines) ? lines : []);

        public Task DeleteDayAsync(string day, CancellationToken ct) => Task.CompletedTask;
    }

    static StructureBar Bar(Candle candle) =>
        new(candle.Timestamp, candle.Timestamp.AddMinutes(1), (decimal)candle.Open, (decimal)candle.High,
            (decimal)candle.Low, (decimal)candle.Close, candle.Volume);
}
