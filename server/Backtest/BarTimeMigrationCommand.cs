using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain;

namespace Astra.Server.Backtest;

public sealed record BarTimeMigrationRow(string Day, string Symbol, string Status, int Before, int After, int Dropped);

public sealed record BarTimeMigrationReport(string Root, bool Applied, IReadOnlyList<BarTimeMigrationRow> Rows);

/// <summary>v0 저장 봉(Toss 종료 라벨)을 시작 시각 규약으로 옮기는 도구(#332). 기본은 dry-run이며 `--apply`에서만 쓴다.</summary>
public static class BarTimeMigrationCommand
{
    public const string Name = "bars-migrate-time-convention";
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeOnly SessionOpen = new(9, 30);
    static readonly TimeOnly SessionClose = new(16, 0);

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var root = ConfluenceMeasureCommand.Option(args, "--root") ??
                   Path.Combine(ConfluenceMeasureCommand.ResolveContentRoot(), "App_Data", "bars");
        var apply = args.Contains("--apply", StringComparer.Ordinal);
        if (!Directory.Exists(root)) { output.WriteLine($"봉 저장 경로가 없다: {root}"); return 2; }
        var report = await MigrateAsync(root, apply, ct);
        output.WriteLine($"{(report.Applied ? "적용" : "dry-run")} · {report.Root} · 파일 {report.Rows.Count}개 · 규약 {BarTimeConvention.Current}");
        foreach (var row in report.Rows)
            output.WriteLine($"{row.Day} {row.Symbol} {row.Status} {row.Before}->{row.After} (제외 {row.Dropped})");
        var summary = report.Rows.GroupBy(x => x.Status).Select(g => $"{g.Key} {g.Count()}");
        output.WriteLine(string.Join(" · ", summary));
        if (!report.Applied) output.WriteLine("파일을 바꾸지 않았다. 실제 적용은 --apply.");
        return 0;
    }

    public static async Task<BarTimeMigrationReport> MigrateAsync(string root, bool apply, CancellationToken ct)
    {
        root = Path.GetFullPath(root);
        var rows = new List<BarTimeMigrationRow>();
        foreach (var dayDirectory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var day = Path.GetFileName(dayDirectory);
            foreach (var path in Directory.EnumerateFiles(dayDirectory, "*.jsonl").Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var symbol = Path.GetFileNameWithoutExtension(path);
                var lines = (await File.ReadAllLinesAsync(path, ct)).Where(x => x.Length > 0).ToArray();
                var conventions = lines.Select(SafeConvention).Where(x => x is not null).Distinct().ToArray();
                if (conventions.Length == 0) { rows.Add(new(day, symbol, "empty", 0, 0, 0)); continue; }
                if (conventions.Length > 1) { rows.Add(new(day, symbol, "mixed-skipped", lines.Length, lines.Length, 0)); continue; }
                if (conventions[0] == BarTimeConvention.Current) { rows.Add(new(day, symbol, "already-current", lines.Length, lines.Length, 0)); continue; }
                if (conventions[0] != BarTimeConvention.Legacy) { rows.Add(new(day, symbol, "unknown-skipped", lines.Length, lines.Length, 0)); continue; }
                var converted = ConvertLegacyLines(lines);
                rows.Add(new(day, symbol, apply ? "migrated" : "would-migrate", lines.Length, converted.Count, lines.Length - converted.Count));
                if (!apply) continue;
                var temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, string.Concat(converted.Select(x => x + "\n")), ct);
                File.Move(temporary, path, true);
            }
        }
        return new(root, apply, rows);
    }

    /// <summary>라벨 −1분, 정규장(09:30~16:00 ET) 밖으로 나간 봉 제외, 규약 필드 부여.</summary>
    public static IReadOnlyList<string> ConvertLegacyLines(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            var root = JsonDocument.Parse(line).RootElement;
            var start = TossBarTime.StartOf(root.GetProperty("t").GetDateTimeOffset());
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, NewYork).DateTime);
            if (local < SessionOpen || local >= SessionClose) continue;
            var bar = new Candle(start, root.GetProperty("o").GetDouble(), root.GetProperty("h").GetDouble(),
                root.GetProperty("l").GetDouble(), root.GetProperty("c").GetDouble(), root.GetProperty("v").GetDouble());
            result.Add(StoredBarLine.Serialize(bar, BarTimeConvention.Current));
        }
        return result;
    }

    static string? SafeConvention(string line)
    {
        try { return BarTimeConvention.Of(line); }
        catch (JsonException) { return null; }
    }
}
