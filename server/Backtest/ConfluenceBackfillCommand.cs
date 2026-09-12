using System.Text.Json;
using Astra.Server.Application.Backtest;
using Astra.Server.Domain;

namespace Astra.Server.Backtest;

public static class ConfluenceBackfillCommand
{
    public const string Name = "confluence-backfill";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TimeProvider clock,
        IHistoricalBarSource source, CancellationToken ct)
    {
        var root = ConfluenceMeasureCommand.Option(args, "--root");
        if (string.IsNullOrWhiteSpace(root))
        {
            output.WriteLine("별도 replay 경로를 --root로 지정해야 한다.");
            return 2;
        }
        root = Path.GetFullPath(root);
        var liveRoot = ConfluenceMeasureCommand.ResolveContentRoot();
        if (string.Equals(Path.GetFullPath(Path.Combine(root, "bars")),
                Path.GetFullPath(Path.Combine(liveRoot, "App_Data", "bars")), StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("운영 App_Data/bars는 replay 경로로 사용할 수 없다.");
            return 2;
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = ParseDate(args, "--from") ?? today.AddDays(-28);
        var to = ParseDate(args, "--to") ?? today.AddDays(-1);
        var benchmark = ConfluenceMeasureCommand.Option(args, "--benchmark") ?? "QQQ";
        var watchlistPath = ConfluenceMeasureCommand.Option(args, "--watchlist") ??
                            Path.Combine(liveRoot, "App_Data", "watchlist.json");
        if (!File.Exists(watchlistPath))
        {
            output.WriteLine($"관심종목 파일을 찾을 수 없다: {watchlistPath}");
            return 2;
        }
        var watch = JsonSerializer.Deserialize<List<WatchItem>>(await File.ReadAllTextAsync(watchlistPath, ct)) ?? [];
        if (watch.Count == 0) { output.WriteLine("관심종목이 비어 있다."); return 2; }

        var report = await new ReplayBackfill(source, clock).RunAsync(root, watch.Select(x => x.Symbol).ToArray(),
            benchmark, from, to, ct);
        output.WriteLine($"백필 완료 {report.From:yyyy-MM-dd}~{report.To:yyyy-MM-dd} · 소스 {report.Source} · " +
                         $"관심종목 {report.Watchlist.Length}개 · 품질 행 {report.Rows.Length}개");
        output.WriteLine($"품질 리포트: {Path.Combine(root, "import-report.json")}");
        return 0;
    }

    static DateOnly? ParseDate(string[] args, string name) =>
        ConfluenceMeasureCommand.Option(args, name) is not { } value ? null :
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed) ? parsed :
        throw new ArgumentException($"{name} 값은 yyyy-MM-dd 형식이어야 한다: {value}");
}

public sealed class TossHistoricalBarSource(TossClient client) : IHistoricalBarSource
{
    public string Name => "Toss";
    public bool Adjusted => true;
    public Task<IReadOnlyList<Candle>> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct) => client.HistoricalCandles(symbol, from, to, ct);
}
