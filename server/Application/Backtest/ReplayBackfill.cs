using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Domain;

namespace Astra.Server.Application.Backtest;

public interface IHistoricalBarSource
{
    string Name { get; }
    bool Adjusted { get; }
    Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct);
}

public sealed record HistoricalBarPage(string RequestCursor, string? NextCursor, IReadOnlyList<Candle> Bars,
    int RawBarCount, bool ReachedRequestedStart, DateTimeOffset? OldestBar, string? StopReason);

public interface IHistoricalBarPageSource : IHistoricalBarSource
{
    IAsyncEnumerable<HistoricalBarPage> ReadPagesAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
        string? before, int completedPages, IReadOnlyCollection<string> visitedCursors, CancellationToken ct);
}

public sealed record HistoricalBarReadResult(IReadOnlyList<Candle> Bars, int RawBarCount,
    bool ReachedRequestedStart, DateTimeOffset? OldestBar, string? StopReason);

public sealed record ReplayImportRow(string Day, string Symbol, int ExpectedBars, int ActualBars, int Gaps,
    int Duplicates, DateTimeOffset? FirstBar, DateTimeOffset? LastBar, bool BenchmarkMissing)
{
    public double MissingRate => ExpectedBars == 0 ? 0 : Math.Max(0, (double)(ExpectedBars - ActualBars) / ExpectedBars);
}

public sealed record ReplayImportReport(string Source, DateTimeOffset FetchedAt, DateTimeOffset AsOf, DateOnly From, DateOnly To,
    bool Adjusted, string TimeZone, string Benchmark, ImmutableArray<string> Watchlist,
    bool HistoricalWatchlistUnavailable, string DataStatus, string? DataReason,
    ImmutableArray<ReplayImportSourceRow> Sources, ImmutableArray<ReplayImportRow> Rows);

public sealed record ReplayImportSourceRow(string Symbol, int RawBars, int ActualTradingDays,
    bool ReachedRequestedStart, DateTimeOffset? OldestBar, string DataStatus, string? Reason,
    DateTimeOffset? NewestBar = null, DateOnly? FirstCoveredSession = null,
    DateOnly? LastCoveredSession = null, DateOnly? RequestedFrom = null, DateOnly? RequestedTo = null);

public sealed class ReplayBackfill(IHistoricalBarSource source, TimeProvider clock)
{
    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public async Task<ReplayImportReport> RunAsync(string root, IReadOnlyList<string> watchlist, string benchmark,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (to < from) throw new ArgumentException("종료일은 시작일보다 앞설 수 없다.");
        root = Path.GetFullPath(root);
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("드라이브 루트는 replay 경로로 사용할 수 없다.", nameof(root));
        var symbols = watchlist.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.Equals(x, benchmark, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        var all = symbols.Append(benchmark.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fetchedAt = clock.GetUtcNow();
        var start = EasternOffset(from, 0, 0);
        var end = EasternOffset(to.AddDays(1), 0, 0);
        var reads = new Dictionary<string, HistoricalBarReadResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in all)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await source.ReadAsync(symbol, start, end, ct);
            reads[symbol] = raw;
        }

        return await WriteAsync(root, watchlist, benchmark, from, to, fetchedAt, reads, ct);
    }

    internal async Task<ReplayImportReport> WriteAsync(string root, IReadOnlyList<string> watchlist, string benchmark,
        DateOnly from, DateOnly to, DateTimeOffset fetchedAt,
        IReadOnlyDictionary<string, HistoricalBarReadResult> reads, CancellationToken ct)
    {
        var symbols = watchlist.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.Equals(x, benchmark, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
        var all = symbols.Append(benchmark.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var normalized = all.ToDictionary(x => x, x => Normalize(reads[x].Bars, from, to),
            StringComparer.OrdinalIgnoreCase);
        var end = EasternOffset(to.AddDays(1), 0, 0);

        var sourceRows = all.Select(symbol => SourceRow(symbol, reads[symbol], normalized[symbol], from, to))
            .ToImmutableArray();
        var benchmarkRow = sourceRows.First(x => string.Equals(x.Symbol, benchmark, StringComparison.OrdinalIgnoreCase));
        var dataStatus = sourceRows.All(x => x.ActualTradingDays == 0) || benchmarkRow.ActualTradingDays == 0 ? "no-data" :
            sourceRows.Any(x => x.DataStatus != "available") ? "partial" : "available";
        var dataReason = dataStatus switch
        {
            "no-data" when sourceRows.All(x => x.ActualTradingDays == 0) => "요청 기간에 사용할 수 있는 봉이 없습니다.",
            "no-data" => $"벤치마크 {benchmark.ToUpperInvariant()} 데이터가 없습니다.",
            "partial" => "일부 종목 데이터가 없거나 요청 시작일까지 페이지를 조회하지 못했습니다.",
            _ => null
        };

        var barsRoot = Path.Combine(Path.GetFullPath(root), "bars");
        if (Directory.Exists(barsRoot)) Directory.Delete(barsRoot, true);
        Directory.CreateDirectory(barsRoot);
        var tradingDays = normalized.Values.SelectMany(x => x.Keys).Distinct().Order().ToArray();
        var rows = ImmutableArray.CreateBuilder<ReplayImportRow>();
        foreach (var day in tradingDays)
            foreach (var symbol in all)
            {
                var data = normalized[symbol].GetValueOrDefault(day) ?? new Normalized([], 0, 0);
                var dayText = day.ToString("yyyy-MM-dd");
                await WriteBarsAsync(root, dayText, symbol, data.Bars, ct);
                rows.Add(new ReplayImportRow(dayText, symbol, 390, data.Bars.Length, data.Gaps, data.Duplicates,
                    data.Bars.FirstOrDefault()?.Timestamp, data.Bars.LastOrDefault()?.Timestamp,
                    !normalized[benchmark].ContainsKey(day)));
            }

        var report = new ReplayImportReport(source.Name, fetchedAt, end, from, to, source.Adjusted, "UTC",
            benchmark.ToUpperInvariant(), [..symbols], true, dataStatus, dataReason, sourceRows, rows.ToImmutable());
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "import-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        return report;
    }

    static ReplayImportSourceRow SourceRow(string symbol, HistoricalBarReadResult read,
        Dictionary<DateOnly, Normalized> normalized, DateOnly requestedFrom, DateOnly requestedTo)
    {
        var status = normalized.Count == 0 ? "no-data" : read.ReachedRequestedStart ? "available" : "partial";
        var reason = status switch
        {
            "no-data" => read.StopReason ?? "요청 기간에 수집된 원시 봉이 없습니다.",
            "partial" => read.StopReason ?? "페이지네이션이 요청 시작일에 도달하지 못했습니다.",
            _ => null
        };
        var actual = normalized.Values.SelectMany(x => x.Bars).Select(x => (DateTimeOffset?)x.Timestamp);
        return new ReplayImportSourceRow(symbol, read.RawBarCount, normalized.Count,
            read.ReachedRequestedStart, actual.Min(), status, reason, actual.Max(),
            normalized.Count == 0 ? null : normalized.Keys.Min(),
            normalized.Count == 0 ? null : normalized.Keys.Max(), requestedFrom, requestedTo);
    }

    static Dictionary<DateOnly, Normalized> Normalize(IEnumerable<Candle> source, DateOnly from, DateOnly to)
    {
        var result = new Dictionary<DateOnly, Normalized>();
        foreach (var group in source.Where(Valid).Select(x => (Bar: x, Local: TimeZoneInfo.ConvertTime(x.Timestamp, Eastern)))
                     .Where(x => TimeOnly.FromDateTime(x.Local.DateTime) >= new TimeOnly(9, 30) &&
                                 TimeOnly.FromDateTime(x.Local.DateTime) < new TimeOnly(16, 0))
                     .Where(x => DateOnly.FromDateTime(x.Local.DateTime) >= from && DateOnly.FromDateTime(x.Local.DateTime) <= to)
                     .GroupBy(x => DateOnly.FromDateTime(x.Local.DateTime)))
        {
            var ordered = group.OrderBy(x => x.Bar.Timestamp).ToArray();
            var unique = ordered.DistinctBy(x => x.Bar.Timestamp.UtcDateTime).Select(x => x.Bar with
            {
                Timestamp = new DateTimeOffset(x.Bar.Timestamp.UtcDateTime, TimeSpan.Zero)
            }).ToArray();
            var gaps = unique.Zip(unique.Skip(1)).Sum(x => Math.Max(0, (int)(x.Second.Timestamp - x.First.Timestamp).TotalMinutes - 1));
            result[group.Key] = new Normalized(unique, ordered.Length - unique.Length, gaps);
        }
        return result;
    }

    static bool Valid(Candle x) => double.IsFinite(x.Open) && double.IsFinite(x.High) && double.IsFinite(x.Low) &&
        double.IsFinite(x.Close) && double.IsFinite(x.Volume) && x.Open > 0 && x.High >= Math.Max(x.Open, x.Close) &&
        x.Low <= Math.Min(x.Open, x.Close) && x.Low > 0 && x.Volume >= 0;

    static async Task WriteBarsAsync(string root, string day, string symbol, IReadOnlyList<Candle> bars,
        CancellationToken ct)
    {
        var directory = Path.Combine(root, "bars", day);
        Directory.CreateDirectory(directory);
        var lines = bars.Select(x => JsonSerializer.Serialize(new
        {
            t = x.Timestamp.UtcDateTime, o = x.Open, h = x.High, l = x.Low, c = x.Close, v = x.Volume
        }));
        await File.WriteAllLinesAsync(Path.Combine(directory, symbol + ".jsonl"), lines, ct);
    }

    static DateTimeOffset EasternOffset(DateOnly day, int hour, int minute)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Eastern.GetUtcOffset(local));
    }

    sealed record Normalized(Candle[] Bars, int Duplicates, int Gaps);
}
