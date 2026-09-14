using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Application.Backtest;
using Astra.Server.Backtest;
using Astra.Server.Domain;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ReplayBackfillTests
{
    [Fact]
    public async Task EmptySourceWritesNoDataReportWithOneSourceRowPerRequestedSymbol()
    {
        var root = Directory.CreateTempSubdirectory("astra-replay-empty-").FullName;
        try
        {
            var report = await new ReplayBackfill(new FakeSource(), TimeProvider.System).RunAsync(root,
                ["TSLA"], "QQQ", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), CancellationToken.None);

            Assert.Equal("no-data", report.DataStatus);
            Assert.Empty(report.Rows);
            Assert.Equal(2, report.Sources.Length);
            Assert.All(report.Sources, row =>
            {
                Assert.Equal(0, row.RawBars);
                Assert.Equal(0, row.ActualTradingDays);
                Assert.False(row.ReachedRequestedStart);
                Assert.Equal("no-data", row.DataStatus);
            });
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "import-report.json")));
            Assert.Equal("no-data", saved.RootElement.GetProperty("DataStatus").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PaginationThatDoesNotReachRequestedStartIsReportedAsPartial()
    {
        var root = Directory.CreateTempSubdirectory("astra-replay-partial-").FullName;
        try
        {
            var source = new FakeSource { ReachedRequestedStart = false, StopReason = "페이지 상한" };
            source.Add("TSLA", Bar("2026-09-08T13:30:00Z", 100));
            source.Add("QQQ", Bar("2026-09-08T13:30:00Z", 500));

            var report = await new ReplayBackfill(source, TimeProvider.System).RunAsync(root, ["TSLA"], "QQQ",
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), CancellationToken.None);

            Assert.Equal("partial", report.DataStatus);
            Assert.All(report.Sources, row => Assert.False(row.ReachedRequestedStart));
            Assert.All(report.Sources, row => Assert.Equal("페이지 상한", row.Reason));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BackfillWritesNormalizedBarsAndQualityReportUnderReplayRoot()
    {
        var root = Directory.CreateTempSubdirectory("astra-replay-").FullName;
        try
        {
            var source = new FakeSource();
            source.Add("TSLA", Bar("2026-09-08T13:31:00Z", 101), Bar("2026-09-08T13:30:00Z", 100),
                Bar("2026-09-08T13:31:00Z", 999), Bar("2026-09-08T12:00:00Z", 80));
            source.Add("QQQ", Bar("2026-09-08T13:30:00Z", 500));

            var report = await new ReplayBackfill(source, TimeProvider.System).RunAsync(root, ["TSLA", "QQQ"],
                "QQQ", new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), CancellationToken.None);

            Assert.Equal("TSLA", Assert.Single(report.Watchlist));
            Assert.True(report.HistoricalWatchlistUnavailable);
            Assert.Equal("Toss", report.Source);
            Assert.True(report.Adjusted);
            Assert.Equal(2, report.Rows.Length);
            var tsla = Assert.Single(report.Rows, x => x.Symbol == "TSLA");
            Assert.Equal(2, tsla.ActualBars);
            Assert.Equal(1, tsla.Duplicates);
            Assert.Equal(388d / 390, tsla.MissingRate, 8);
            var lines = await File.ReadAllLinesAsync(Path.Combine(root, "bars", "2026-09-08", "TSLA.jsonl"));
            Assert.Equal(2, lines.Length);
            Assert.Equal("2026-09-08T13:30:00Z", JsonDocument.Parse(lines[0]).RootElement.GetProperty("t").GetDateTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'"));
            Assert.True(File.Exists(Path.Combine(root, "import-report.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task QualityReportMarksBenchmarkMissingWhenOnlyAWatchSymbolHasTheDay()
    {
        var root = Directory.CreateTempSubdirectory("astra-replay-").FullName;
        try
        {
            var source = new FakeSource();
            source.Add("TSLA", Bar("2026-09-08T13:30:00Z", 100));

            var report = await new ReplayBackfill(source, TimeProvider.System).RunAsync(root, ["TSLA"], "QQQ",
                new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), CancellationToken.None);

            Assert.All(report.Rows, x => Assert.True(x.BenchmarkMissing));
            Assert.Equal(0, Assert.Single(report.Rows, x => x.Symbol == "QQQ").ActualBars);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CommandRequiresASeparateReplayRoot()
    {
        var output = new StringWriter();

        var exit = await ConfluenceBackfillCommand.RunAsync(["confluence-backfill"], output,
            TimeProvider.System, new FakeSource(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("--root", output.ToString());
    }

    [Fact]
    public async Task MeasureCommandReadsReplayRootAndDoesNotWriteLiveWeights()
    {
        var root = Directory.CreateTempSubdirectory("astra-replay-measure-").FullName;
        try
        {
            var source = new FakeSource();
            source.Add("TSLA", Enumerable.Range(0, 90).Select(i =>
                Bar(DateTimeOffset.Parse("2026-09-08T13:30:00Z").AddMinutes(i).ToString("O"), 100 + i)).ToArray());
            source.Add("QQQ", Enumerable.Range(0, 90).Select(i =>
                Bar(DateTimeOffset.Parse("2026-09-08T13:30:00Z").AddMinutes(i).ToString("O"), 500 + i)).ToArray());
            await new ReplayBackfill(source, TimeProvider.System).RunAsync(root, ["TSLA"], "QQQ",
                new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), CancellationToken.None);
            var output = new StringWriter();

            var exit = await ConfluenceMeasureCommand.RunAsync(["confluence-measure", "--root", root,
                "--from", "2026-09-08", "--to", "2026-09-08"], output, TimeProvider.System,
                CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.Contains("| TSLA |", output.ToString());
            Assert.Contains("unavailable", output.ToString());
            Assert.False(File.Exists(Path.Combine(root, "App_Data", "confluence-weights.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FutureSessionDoesNotMutateTheHistorySnapshotUsedAtAnEarlierTime()
    {
        var history = new ReplaySymbolHistory();
        var first = ConfluenceReplayTests.Rising(40);
        history.Append(new DateOnly(2026, 9, 8), first);
        var dailyAtSignal = history.DailyBars;
        var profilesAtSignal = history.Profiles;
        var previousCloseAtSignal = history.PreviousSessionClose;

        var future = ConfluenceReplayTests.Rising(40).Select(x => x with
        {
            Start = x.Start.AddDays(1), End = x.End.AddDays(1), Open = x.Open * 10, High = x.High * 10,
            Low = x.Low * 10, Close = x.Close * 10
        }).ToImmutableArray();
        history.Append(new DateOnly(2026, 9, 9), future);

        Assert.Single(dailyAtSignal);
        Assert.Single(profilesAtSignal);
        Assert.Equal(first[^1].Close, previousCloseAtSignal);
        Assert.Equal(first[^1].Close, dailyAtSignal[^1].Close);
    }

    static Candle Bar(string timestamp, double close) => new(DateTimeOffset.Parse(timestamp), close, close + 1,
        close - 1, close, 1000);

    sealed class FakeSource : IHistoricalBarSource
    {
        readonly Dictionary<string, IReadOnlyList<Candle>> _bars = new(StringComparer.OrdinalIgnoreCase);
        public string Name => "Toss";
        public bool Adjusted => true;
        public bool ReachedRequestedStart { get; init; } = true;
        public string? StopReason { get; init; }
        public void Add(string symbol, params Candle[] bars) => _bars[symbol] = bars;
        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct)
        {
            var bars = _bars.GetValueOrDefault(symbol) ?? [];
            return Task.FromResult(new HistoricalBarReadResult(bars, bars.Count,
                bars.Count > 0 && ReachedRequestedStart, bars.Count == 0 ? null : bars.Min(x => x.Timestamp),
                bars.Count == 0 ? "빈 응답" : StopReason));
        }
    }
}
