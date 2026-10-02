using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Backtest;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

namespace Astra.Server.Tests;

[Collection("TossClient serial")]
public sealed class BarTimeConventionTests : IDisposable
{
    static readonly DateTimeOffset SessionStart = new(2026, 9, 25, 13, 30, 0, TimeSpan.Zero);
    static readonly DateTimeOffset SessionEnd = new(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
    static readonly MarketSession Session = new(true, "정규장", null, SessionStart, SessionEnd);
    readonly string _credentials = Path.GetTempFileName();

    public BarTimeConventionTests()
    {
        File.WriteAllText(_credentials, "API Key : test-id\nSecret Key : test-secret");
        Environment.SetEnvironmentVariable("TOSS_CREDENTIALS_PATH", _credentials);
    }

    public void Dispose() { Environment.SetEnvironmentVariable("TOSS_CREDENTIALS_PATH", null); File.Delete(_credentials); }

    [Fact]
    public async Task CandlesNormalizeTheTossEndLabelToTheBarStart()
    {
        var client = Client(Fixture(Candles(("2026-09-25T22:31:00+09:00", 100, 101, 99, 100, 10))));

        var bar = Assert.Single(await client.Candles("CRDO", CancellationToken.None));

        Assert.Equal(SessionStart, bar.Timestamp);
    }

    [Fact]
    public async Task CrdoSessionOpenReproducesTheYahooOpenAfterNormalization()
    {
        var client = Client(Fixture(Candles(
            ("2026-09-25T22:30:00+09:00", 201.71, 202, 201.2, 201.59, 2333),
            ("2026-09-25T22:31:00+09:00", 201.59, 203.357, 201.345, 202.687, 163284),
            ("2026-09-25T22:32:00+09:00", 202.435, 203.51, 201.44, 202.21, 39778))));

        var bars = await client.Candles("CRDO", CancellationToken.None);
        var regular = MarketRules.CompletedRegularBars(bars, Session, SessionStart.AddMinutes(5));

        Assert.Equal(3, bars.Count);
        Assert.Equal(SessionStart.AddMinutes(-1), bars[0].Timestamp);
        Assert.Equal(2, regular.Length);
        Assert.Equal(SessionStart, regular[0].Timestamp);
        Assert.Equal(201.59, regular[0].Open);
        Assert.Equal(163284, regular[0].Volume);
    }

    [Fact]
    public void TossNineThirtyLabelIsPremarketAndSixteenHundredLabelIsTheLastRegularBar()
    {
        var bars = new[] { TossBar("2026-09-25T13:30:00Z"), TossBar("2026-09-25T13:31:00Z"), TossBar("2026-09-25T16:00:00-04:00") };

        var regular = MarketRules.CompletedRegularBars(bars, Session, SessionEnd.AddMinutes(1));

        Assert.Equal([SessionStart, SessionEnd.AddMinutes(-1)], regular.Select(x => x.Timestamp));
    }

    [Fact]
    public async Task BackfillSessionFilterKeepsTheSixteenHundredLabelAndDropsTheNineThirtyLabel()
    {
        var root = Directory.CreateTempSubdirectory("astra-bars-tc-").FullName;
        try
        {
            var source = new FakeSource(BarTimeConvention.Current);
            source.Add("TSLA", TossBar("2026-09-25T13:30:00Z"), TossBar("2026-09-25T13:31:00Z"), TossBar("2026-09-25T20:00:00Z"));
            source.Add("QQQ", TossBar("2026-09-25T13:31:00Z"));

            var report = await new ReplayBackfill(source, TimeProvider.System).RunAsync(root, ["TSLA"], "QQQ",
                new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25), CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(Path.Combine(root, "bars", "2026-09-25", "TSLA.jsonl"));
            Assert.Equal(2, lines.Length);
            Assert.Equal(SessionStart, JsonDocument.Parse(lines[0]).RootElement.GetProperty("t").GetDateTimeOffset());
            Assert.Equal(SessionEnd.AddMinutes(-1), JsonDocument.Parse(lines[1]).RootElement.GetProperty("t").GetDateTimeOffset());
            Assert.All(lines, line => Assert.Equal(BarTimeConvention.BarStart, BarTimeConvention.Of(line)));
            Assert.Equal(BarTimeConvention.BarStart, report.BarTimeConvention);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RealtimeCompletionIncludesTheBarThatReallyEndedAndExcludesTheRunningOne()
    {
        var bars = new[] { TossBar("2026-09-25T13:31:00Z"), TossBar("2026-09-25T13:32:00Z") };

        var regular = MarketRules.CompletedRegularBars(bars, Session, SessionStart.AddSeconds(70));
        var normalized = BarAggregator.Normalize(bars, SessionStart, SessionEnd, SessionStart.AddMinutes(1));

        Assert.Equal([SessionStart], regular.Select(x => x.Timestamp));
        Assert.Equal([SessionStart], normalized.Bars.Select(x => x.Start));
        Assert.Equal(1, normalized.DroppedIncomplete);
    }

    [Fact]
    public void OneSecondBeforeTheCloseOnlyBarsThroughFifteenFiftyEightAreComplete()
    {
        var bars = new[] { TossBar("2026-09-25T19:58:00Z"), TossBar("2026-09-25T19:59:00Z"), TossBar("2026-09-25T20:00:00Z") };
        var quoteAt = SessionEnd.AddSeconds(-1);

        var regular = MarketRules.CompletedRegularBars(bars, Session, quoteAt);
        var normalized = BarAggregator.Normalize(bars, SessionStart, SessionEnd, new DateTimeOffset(2026, 9, 25, 19, 59, 0, TimeSpan.Zero));

        Assert.Equal([SessionEnd.AddMinutes(-3), SessionEnd.AddMinutes(-2)], regular.Select(x => x.Timestamp));
        Assert.Equal(SessionEnd.AddMinutes(-2), normalized.Bars[^1].Start);
    }

    [Fact]
    public async Task HistoricalCandlesKeepTheLastBarEndingExactlyAtTheRequestedEnd()
    {
        var client = Client(Fixture(Candles(
            ("2026-09-09T00:00:00Z", 100, 101, 99, 100, 10),
            ("2026-09-08T00:00:00Z", 100, 101, 99, 100, 10))));

        var result = await client.HistoricalCandles("TSLA", DateTimeOffset.Parse("2026-09-08T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"), CancellationToken.None);

        var bar = Assert.Single(result.Bars);
        Assert.Equal(DateTimeOffset.Parse("2026-09-08T23:59:00Z"), bar.Timestamp);
        Assert.True(result.ReachedRequestedStart);
    }

    [Fact]
    public async Task HistoricalCandlePagesCarryTheCurrentConvention()
    {
        var client = Client(Fixture(Candles(("2026-09-08T13:31:00Z", 100, 101, 99, 100, 10))));
        var pages = new List<HistoricalBarPage>();

        await foreach (var page in client.HistoricalCandlePages("TSLA", DateTimeOffset.Parse("2026-09-08T00:00:00Z"),
                           DateTimeOffset.Parse("2026-09-09T00:00:00Z"), null, 0, [], CancellationToken.None))
            pages.Add(page);

        Assert.Equal(BarTimeConvention.BarStart, Assert.Single(pages).BarTimeConvention);
    }

    [Fact]
    public void LegacyLinesWithoutTheFieldParseWithUnchangedTimestamps()
    {
        var lines = new[]
        {
            "{\"t\":\"2026-09-25T13:30:00Z\",\"o\":201.71,\"h\":202,\"l\":201.2,\"c\":201.59,\"v\":2333}",
            "{\"t\":\"2026-09-25T13:31:00Z\",\"o\":201.59,\"h\":203.357,\"l\":201.345,\"c\":202.687,\"v\":163284}"
        };

        var session = StoredBarLine.ParseSession(lines);

        Assert.Null(session.Rejection);
        Assert.Equal(BarTimeConvention.Legacy, session.Convention);
        Assert.Equal([SessionStart, SessionStart.AddMinutes(1)], session.Bars.Select(x => x.Start));
        Assert.Equal(session.Bars.ToArray(), ConfluenceReplay.Parse(lines).ToArray());
    }

    [Fact]
    public void CurrentConventionLinesParseWithTheirConvention()
    {
        var line = StoredBarLine.Serialize(TossBar("2026-09-25T13:31:00Z"), BarTimeConvention.Current);

        var session = StoredBarLine.ParseSession([line]);

        Assert.Equal(BarTimeConvention.BarStart, session.Convention);
        Assert.Equal(SessionStart, Assert.Single(session.Bars).Start);
        Assert.Contains("\"tc\":\"bar-start.1\"", line);
    }

    [Fact]
    public void MixedConventionSessionIsRejectedWithAnExplicitReasonInsteadOfBarConflict()
    {
        var lines = new[]
        {
            "{\"t\":\"2026-09-25T13:30:00Z\",\"o\":1,\"h\":1,\"l\":1,\"c\":1,\"v\":1}",
            StoredBarLine.Serialize(TossBar("2026-09-25T13:31:00Z"), BarTimeConvention.Current)
        };

        var session = StoredBarLine.ParseSession(lines);

        Assert.Empty(session.Bars);
        Assert.StartsWith(BarTimeConvention.MixedRejection, session.Rejection);
        Assert.Empty(ConfluenceReplay.Parse(lines));
    }

    [Fact]
    public async Task BarStoreStampsTheConventionAndRefusesToAppendToALegacySessionFile()
    {
        var store = new MemoryBarStore();
        store.Seed("2026-09-25", "LEGACY", "{\"t\":\"2026-09-25T13:30:00Z\",\"o\":1,\"h\":1,\"l\":1,\"c\":1,\"v\":1}");
        var service = new BarStoreService(store, new FixedClock(SessionStart));

        await service.SaveNewBarsAsync("NVDA", [TossBar("2026-09-25T13:31:00Z")], default);
        await service.SaveNewBarsAsync("LEGACY", [TossBar("2026-09-25T13:32:00Z")], default);

        Assert.Equal(BarTimeConvention.BarStart, BarTimeConvention.Of(Assert.Single(store.Lines("2026-09-25", "NVDA"))));
        Assert.Single(store.Lines("2026-09-25", "LEGACY"));
        Assert.Equal(["2026-09-25/LEGACY"], service.ConventionConflicts);
    }

    [Fact]
    public void ImportReportWithoutTheConventionFieldReadsAsLegacy()
    {
        var json = "{\"Source\":\"Toss\",\"FetchedAt\":\"2026-09-30T00:00:00Z\",\"AsOf\":\"2026-09-30T00:00:00Z\",\"From\":\"2026-06-29\",\"To\":\"2026-09-26\",\"Adjusted\":true,\"TimeZone\":\"UTC\",\"Benchmark\":\"QQQ\",\"Watchlist\":[],\"HistoricalWatchlistUnavailable\":true,\"DataStatus\":\"available\",\"DataReason\":null,\"Sources\":[],\"Rows\":[]}";

        var report = JsonSerializer.Deserialize<ReplayImportReport>(json);

        Assert.Null(report!.BarTimeConvention);
    }

    [Fact]
    public async Task StaleConventionPagesAreDiscardedBeforeTheAcquisitionResumes()
    {
        var root = Directory.CreateTempSubdirectory("astra-dataset-tc-").FullName;
        try
        {
            var day = new DateOnly(2026, 9, 8);
            var legacy = new PagedSource(null) { FailAfterFirstPage = true };
            await Assert.ThrowsAsync<HttpRequestException>(() => new ReplayDatasetCache(root, legacy, TimeProvider.System)
                .AcquireAsync(["TSLA"], "QQQ", day, day, false, CancellationToken.None));

            var current = new PagedSource(BarTimeConvention.Current);
            var completed = await new ReplayDatasetCache(root, current, TimeProvider.System)
                .AcquireAsync(["TSLA"], "QQQ", day, day, false, CancellationToken.None);

            Assert.Equal("available", completed.Manifest.Status);
            Assert.Equal(BarTimeConvention.BarStart, completed.Import.BarTimeConvention);
            Assert.Equal(1, current.Calls.Count(x => x.Symbol == "TSLA" && x.Before is null));
            Assert.Equal(1, current.Calls.Count(x => x.Symbol == "QQQ" && x.Before is null));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task MigrationDryRunLeavesFilesUntouchedAndApplyShiftsDropsAndStamps()
    {
        var root = Directory.CreateTempSubdirectory("astra-migrate-tc-").FullName;
        try
        {
            var day = Path.Combine(root, "2026-09-25");
            Directory.CreateDirectory(day);
            var legacy = Path.Combine(day, "CRDO.jsonl");
            var original = "{\"t\":\"2026-09-25T13:30:00Z\",\"o\":201.71,\"h\":202,\"l\":201.2,\"c\":201.59,\"v\":2333}\n" +
                           "{\"t\":\"2026-09-25T13:31:00Z\",\"o\":201.59,\"h\":203.357,\"l\":201.345,\"c\":202.687,\"v\":163284}\n";
            await File.WriteAllTextAsync(legacy, original);
            var mixed = Path.Combine(day, "MIXED.jsonl");
            await File.WriteAllTextAsync(mixed, "{\"t\":\"2026-09-25T13:30:00Z\",\"o\":1,\"h\":1,\"l\":1,\"c\":1,\"v\":1}\n" +
                                               StoredBarLine.Serialize(TossBar("2026-09-25T13:32:00Z"), BarTimeConvention.Current) + "\n");

            var dryRun = await BarTimeMigrationCommand.MigrateAsync(root, false, CancellationToken.None);
            Assert.Equal(original, await File.ReadAllTextAsync(legacy));
            Assert.Equal("would-migrate", Assert.Single(dryRun.Rows, x => x.Symbol == "CRDO").Status);
            Assert.Equal("mixed-skipped", Assert.Single(dryRun.Rows, x => x.Symbol == "MIXED").Status);

            var output = new StringWriter();
            Assert.Equal(0, await BarTimeMigrationCommand.RunAsync(["bars-migrate-time-convention", "--root", root], output, CancellationToken.None));
            Assert.Equal(original, await File.ReadAllTextAsync(legacy));
            Assert.Contains("dry-run", output.ToString());

            var applied = await BarTimeMigrationCommand.MigrateAsync(root, true, CancellationToken.None);
            var migrated = Assert.Single(applied.Rows, x => x.Symbol == "CRDO");
            Assert.Equal(("migrated", 2, 1, 1), (migrated.Status, migrated.Before, migrated.After, migrated.Dropped));
            var session = StoredBarLine.ParseSession(await File.ReadAllLinesAsync(legacy));
            Assert.Equal(BarTimeConvention.BarStart, session.Convention);
            var bar = Assert.Single(session.Bars);
            Assert.Equal(SessionStart, bar.Start);
            Assert.Equal(201.59m, bar.Open);

            var again = await BarTimeMigrationCommand.MigrateAsync(root, true, CancellationToken.None);
            Assert.Equal("already-current", Assert.Single(again.Rows, x => x.Symbol == "CRDO").Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EngineVersionMovesToStructureTwoForTheNewBarConvention()
    {
        Assert.Equal("v5-structure.2", StructurePolicy.Default.Version);
        Assert.NotEqual((StructurePolicy.Default with { Version = "v5-structure.1" }).PolicyHash, StructurePolicy.Default.PolicyHash);
    }

    static Candle TossBar(string label, double close = 100) =>
        new(TossBarTime.StartOf(DateTimeOffset.Parse(label)), close, close + 1, close - 1, close, 1000);

    static TossClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://openapi.tossinvest.com/") });

    static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    static string Candles(params (string Timestamp, double O, double H, double L, double C, double V)[] candles)
    {
        var items = candles.Select(x => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"timestamp\":\"{x.Timestamp}\",\"openPrice\":\"{x.O}\",\"highPrice\":\"{x.H}\",\"lowPrice\":\"{x.L}\",\"closePrice\":\"{x.C}\",\"volume\":\"{x.V}\"}}"));
        return $"{{\"result\":{{\"candles\":[{string.Join(',', items)}],\"nextBefore\":null}}}}";
    }

    static HttpMessageHandler Fixture(string candles) => new FixtureHandler(request =>
        request.RequestUri!.AbsolutePath == "/oauth2/token"
            ? Json("{\"access_token\":\"fixture\",\"expires_in\":3600}")
            : Json(candles));

    sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed class FakeSource(string? convention) : IHistoricalBarSource
    {
        readonly Dictionary<string, IReadOnlyList<Candle>> _bars = new(StringComparer.OrdinalIgnoreCase);
        public string Name => "Toss";
        public bool Adjusted => true;
        public string? BarTimeConvention => convention;
        public void Add(string symbol, params Candle[] bars) => _bars[symbol] = bars;
        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            var bars = _bars.GetValueOrDefault(symbol) ?? [];
            return Task.FromResult(new HistoricalBarReadResult(bars, bars.Count, bars.Count > 0,
                bars.Count == 0 ? null : bars.Min(x => x.Timestamp), bars.Count == 0 ? "빈 응답" : null));
        }
    }

    sealed class PagedSource(string? convention) : IHistoricalBarPageSource
    {
        public string Name => "Toss";
        public bool Adjusted => true;
        public string? BarTimeConvention => convention;
        public bool FailAfterFirstPage { get; init; }
        public ConcurrentBag<(string Symbol, string? Before)> Calls { get; } = [];

        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            throw new InvalidOperationException("page source를 사용해야 한다.");

        public async IAsyncEnumerable<HistoricalBarPage> ReadPagesAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            string? before, int completedPages, IReadOnlyCollection<string> visitedCursors, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            Calls.Add((symbol, before));
            if (before is null)
            {
                var bar = new Candle(DateTimeOffset.Parse("2026-09-08T13:30:00Z"), 100, 101, 99, 100, 1000);
                yield return new(to.ToString("O"), "older", [bar], 1, false, bar.Timestamp, null, convention);
                if (FailAfterFirstPage) throw new HttpRequestException("fixture network failure");
            }
            var oldest = new Candle(from, 1, 1, 1, 1, 0);
            yield return new("older", null, [oldest], 1, true, oldest.Timestamp, null, convention);
        }
    }

    sealed class MemoryBarStore : IBarStore
    {
        readonly Dictionary<(string Day, string Symbol), List<string>> _files = new();

        public IReadOnlyList<string> Lines(string day, string symbol) =>
            _files.TryGetValue((day, symbol.ToUpperInvariant()), out var lines) ? lines : [];

        public void Seed(string day, string symbol, params string[] lines) => _files[(day, symbol.ToUpperInvariant())] = [.. lines];

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct)
        {
            var lines = Lines(day, symbol);
            return Task.FromResult(lines.Count == 0 ? null : lines[^1]);
        }

        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct)
        {
            var key = (day, symbol.ToUpperInvariant());
            if (!_files.TryGetValue(key, out var lines)) _files[key] = lines = [];
            lines.Add(line);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Select(x => x.Day).Distinct().ToArray());

        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Where(x => x.Day == day).Select(x => x.Symbol).ToArray());

        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(Lines(day, symbol).Count);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(Lines(day, symbol));

        public Task DeleteDayAsync(string day, CancellationToken ct)
        {
            foreach (var key in _files.Keys.Where(x => x.Day == day).ToArray()) _files.Remove(key);
            return Task.CompletedTask;
        }
    }
}
