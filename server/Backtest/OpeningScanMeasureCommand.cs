using System.Globalization;
using System.Text;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Application.Opening;
using Astra.Server.Domain;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Opening;

namespace Astra.Server.Backtest;

/// <summary>
/// `dotnet run --project server -- opening-scan-measure` 진입점(#371 §6.4). 서버·Toss 없이 저장 봉만 재생해
/// 운영과 같은 <see cref="OpeningSnapshotEvaluator"/>로 09:35(k=5) 스냅샷과 사후 수익률 분포를 낸다. 합격 기준은 없다.
/// </summary>
public static class OpeningScanMeasureCommand
{
    public const string Name = "opening-scan-measure";
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        var root = ConfluenceMeasureCommand.Option(args, "--root")
            ?? Path.Combine(ConfluenceMeasureCommand.ResolveContentRoot(), "App_Data", "bars");
        var barTime = ConfluenceMeasureCommand.Option(args, "--bar-time");
        if (barTime is not ("start" or "end-label" or "auto")) { output.WriteLine("--bar-time은 start|end-label|auto 중 하나여야 한다."); return 2; }
        if (!Directory.Exists(root)) { output.WriteLine($"봉 저장 경로가 없다: {root}"); return 2; }

        var lookback = Int(ConfluenceMeasureCommand.Option(args, "--lookback"), 20);
        var minSessions = Int(ConfluenceMeasureCommand.Option(args, "--min-sessions"), 5);
        var policy = OpeningScanPolicy.Default with { LookbackSessions = lookback, MinimumSessions = minSessions };

        var dates = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(x => x is not null && DateOnly.TryParseExact(x, "yyyy-MM-dd", out _))
            .Select(x => DateOnly.ParseExact(x!, "yyyy-MM-dd"))
            .OrderBy(x => x).ToArray();
        var from = ConfluenceMeasureCommand.Option(args, "--from") is { } rf ? DateOnly.ParseExact(rf, "yyyy-MM-dd") : dates.FirstOrDefault();
        var to = ConfluenceMeasureCommand.Option(args, "--to") is { } rt ? DateOnly.ParseExact(rt, "yyyy-MM-dd") : dates.LastOrDefault();

        var rows = new List<Row>();
        foreach (var date in dates.Where(d => d >= from && d <= to))
        {
            ct.ThrowIfCancellationRequested();
            var open = OpeningVolumeProfileSource.OpenOf(date);
            foreach (var symbol in SymbolsOf(root, date))
            {
                var today = LoadSession(root, date, symbol, barTime);
                if (today.Length == 0) continue;
                var regular = Regular(today, open);
                var anchor = regular.FirstOrDefault(x => x.Timestamp == open.AddMinutes(4));
                var bars5 = regular.Where(x => x.Timestamp >= open && x.Timestamp < open.AddMinutes(5)).ToArray();
                if (anchor is null || bars5.Length < 5) continue;

                var previous = PreviousProfiles(root, dates, date, symbol, barTime, lookback);
                if (previous.Count < minSessions) continue;
                var prevSession = PreviousSession(root, dates, date, symbol, barTime);

                var input = new OpeningScanInput(symbol, symbol, date, open, anchor.Timestamp.AddMinutes(1), bars5,
                    prevSession, null, previous, anchor.Close, anchor.Timestamp.AddMinutes(1),
                    OpeningSnapshotEvaluator.QuoteBarClose, policy);
                var snapshot = OpeningSnapshotEvaluator.Evaluate(input);

                var bar35 = regular.FirstOrDefault(x => x.Timestamp == open.AddMinutes(34));
                var followWindow = regular.Where(x => x.Timestamp >= open.AddMinutes(5) && x.Timestamp < open.AddMinutes(35)).ToArray();
                var last = regular[^1];
                double? ret30 = bar35 is null ? null : (bar35.Close / anchor.Close - 1) * 100;
                double? mfe30 = followWindow.Length == 0 ? null : (followWindow.Max(x => x.High) / anchor.Close - 1) * 100;
                double? mae30 = followWindow.Length == 0 ? null : (followWindow.Min(x => x.Low) / anchor.Close - 1) * 100;
                var retClose = (last.Close / anchor.Close - 1) * 100;

                rows.Add(new Row(date, symbol, snapshot.Grade, snapshot.Score, snapshot.Rvol3.Ratio, snapshot.RvolNow,
                    snapshot.Rvol20.Ratio, snapshot.SampleCount,
                    snapshot.ChangeFromOpenPercent, snapshot.GapPercent, snapshot.AboveVwap,
                    snapshot.First5?.UpBars ?? 0, snapshot.First5?.NewHighs ?? 0, ret30, mfe30, mae30, retClose));
            }
        }

        var outPath = ConfluenceMeasureCommand.Option(args, "--out")
            ?? Path.Combine(ConfluenceMeasureCommand.ResolveContentRoot(), "outputs", "371", "opening-scan.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, Csv(rows), ct);
        var summary = Summary(rows);
        await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct);

        output.WriteLine($"측정 창 {from:yyyy-MM-dd}~{to:yyyy-MM-dd} · bar-time {barTime} · lookback {lookback} · min-sessions {minSessions} · 표본 {rows.Count}건");
        output.WriteLine($"CSV: {outPath}");
        output.Write(GradeTable(rows));
        output.Write(BucketTable(rows, policy));
        return 0;
    }

    sealed record Row(DateOnly Date, string Symbol, string Grade, double Score, double? Rvol3, double? Rvol5,
        double? Rvol20, int SampleCount, double? ChangeFromOpen, double? Gap, bool? AboveVwap, int UpBars, int NewHighs,
        double? Ret30, double? Mfe30, double? Mae30, double RetClose);

    static IEnumerable<string> SymbolsOf(string root, DateOnly date) =>
        Directory.EnumerateFiles(Path.Combine(root, date.ToString("yyyy-MM-dd")), "*.jsonl")
            .Select(Path.GetFileNameWithoutExtension).Where(x => x is not null).Select(x => x!).Order(StringComparer.Ordinal);

    static Candle[] LoadSession(string root, DateOnly date, string symbol, string barTime)
    {
        var path = Path.Combine(root, date.ToString("yyyy-MM-dd"), symbol + ".jsonl");
        if (!File.Exists(path)) return [];
        var lines = File.ReadAllLines(path).Where(x => x.Length > 0).ToArray();
        if (lines.Length == 0) return [];
        int shift;
        if (barTime == "start") shift = 0;
        else if (barTime == "end-label") shift = -1;
        else
        {
            var conventions = lines.Select(SafeConvention).Where(x => x is not null).Distinct().ToArray();
            if (conventions.Length != 1) return [];
            shift = conventions[0] == BarTimeConvention.Legacy ? -1 : 0;
        }
        var bars = new List<Candle>();
        foreach (var line in lines)
        {
            try
            {
                var e = JsonDocument.Parse(line).RootElement;
                var start = e.GetProperty("t").GetDateTimeOffset().AddMinutes(shift);
                bars.Add(new Candle(start, e.GetProperty("o").GetDouble(), e.GetProperty("h").GetDouble(),
                    e.GetProperty("l").GetDouble(), e.GetProperty("c").GetDouble(), e.GetProperty("v").GetDouble()));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { }
        }
        return bars.OrderBy(x => x.Timestamp).ToArray();
    }

    static Candle[] Regular(Candle[] bars, DateTimeOffset open)
    {
        var close = open.AddMinutes(390);
        return bars.Where(x => x.Timestamp >= open && x.Timestamp < close).ToArray();
    }

    static List<SessionVolumeProfile> PreviousProfiles(string root, DateOnly[] dates, DateOnly date, string symbol,
        string barTime, int lookback)
    {
        var collected = new List<SessionVolumeProfile>();
        foreach (var d in dates.Where(x => x < date).OrderByDescending(x => x))
        {
            if (collected.Count >= lookback) break;
            var open = OpeningVolumeProfileSource.OpenOf(d);
            var regular = Regular(LoadSession(root, d, symbol, barTime), open);
            var window = regular.Where(x => x.Timestamp >= open && x.Timestamp < open.AddMinutes(30))
                .Select(x => new IndicatorBar(x.Timestamp, x.Timestamp.AddMinutes(1), (decimal)x.Open, (decimal)x.High,
                    (decimal)x.Low, (decimal)x.Close, (decimal)x.Volume)).ToArray();
            if (window.All(x => x.Start != open)) continue;
            if (window.Select(x => x.Start).Distinct().Count() < OpeningSessionProfile.MinimumMinutes) continue;
            collected.Add(SessionVolumeProfile.FromBars(d, open, window));
        }
        collected.Reverse();
        return collected;
    }

    static Candle[] PreviousSession(string root, DateOnly[] dates, DateOnly date, string symbol, string barTime)
    {
        foreach (var d in dates.Where(x => x < date).OrderByDescending(x => x))
        {
            var bars = Regular(LoadSession(root, d, symbol, barTime), OpeningVolumeProfileSource.OpenOf(d));
            if (bars.Length > 0) return bars;
        }
        return [];
    }

    static string? SafeConvention(string line)
    {
        try { return BarTimeConvention.Of(line); }
        catch (JsonException) { return null; }
    }

    static string Csv(List<Row> rows)
    {
        var sb = new StringBuilder("date,symbol,grade,score,rvol3,rvol5,rvol20,sampleCount,changeFromOpen,gap,aboveVwap,upBars,newHighs,ret30,mfe30,mae30,retClose\n");
        foreach (var r in rows)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{r.Date:yyyy-MM-dd},{r.Symbol},{r.Grade},{r.Score:0.##},{N(r.Rvol3)},{N(r.Rvol5)},{N(r.Rvol20)},{r.SampleCount},{N(r.ChangeFromOpen)},{N(r.Gap)},{(r.AboveVwap == true ? 1 : 0)},{r.UpBars},{r.NewHighs},{N(r.Ret30)},{N(r.Mfe30)},{N(r.Mae30)},{r.RetClose:0.####}\n"));
        return sb.ToString();
    }

    static object Summary(List<Row> rows)
    {
        var byGrade = rows.GroupBy(x => x.Grade).ToDictionary(g => g.Key, g => GradeStat(g.ToArray()));
        return new { total = rows.Count, byGrade };
    }

    static object GradeStat(Row[] rows)
    {
        var ret = rows.Where(x => x.Ret30 is not null).Select(x => x.Ret30!.Value).OrderBy(x => x).ToArray();
        return new
        {
            n = rows.Length,
            meanRet30 = ret.Length == 0 ? (double?)null : ret.Average(),
            medianRet30 = ret.Length == 0 ? (double?)null : Percentile(ret, 0.5),
            p25Ret30 = ret.Length == 0 ? (double?)null : Percentile(ret, 0.25),
            p75Ret30 = ret.Length == 0 ? (double?)null : Percentile(ret, 0.75),
            winRate = ret.Length == 0 ? (double?)null : (double)ret.Count(x => x > 0.2) / ret.Length,
            meanRetClose = rows.Length == 0 ? (double?)null : rows.Average(x => x.RetClose),
        };
    }

    static string GradeTable(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| 등급 | n | 평균 ret30(%) | 중앙 ret30(%) | p25 | p75 | 승률(>0.2%) | 평균 retClose(%) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var grade in new[] { "STRONG", "VOLUME_ONLY", "PRICE_ONLY", "WEAK" })
        {
            var g = rows.Where(x => x.Grade == grade).ToArray();
            var ret = g.Where(x => x.Ret30 is not null).Select(x => x.Ret30!.Value).OrderBy(x => x).ToArray();
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {grade} | {g.Length} | {M(ret, a => a.Average())} | {M(ret, a => Percentile(a, 0.5))} | {M(ret, a => Percentile(a, 0.25))} | {M(ret, a => Percentile(a, 0.75))} | {(ret.Length == 0 ? "-" : ((double)ret.Count(x => x > 0.2) / ret.Length).ToString("0.00", CultureInfo.InvariantCulture))} | {(g.Length == 0 ? "-" : g.Average(x => x.RetClose).ToString("0.00", CultureInfo.InvariantCulture))} |"));
        }
        return sb.ToString();
    }

    static string BucketTable(List<Row> rows, OpeningScanPolicy policy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| rvol5 구간 | priceUp | n | 평균 ret30(%) |");
        sb.AppendLine("|---|---|---|---|");
        string[] labels = ["<1", "1~1.5", "1.5~2", "2~3", ">=3"];
        for (var b = 0; b < labels.Length; b++)
            foreach (var up in new[] { true, false })
            {
                var g = rows.Where(x => x.Rvol5 is { } r && Bucket(r) == b
                    && (x.Grade is "STRONG" or "PRICE_ONLY") == up && x.Ret30 is not null).Select(x => x.Ret30!.Value).ToArray();
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {labels[b]} | {(up ? "상승" : "비상승")} | {g.Length} | {(g.Length == 0 ? "-" : g.Average().ToString("0.00", CultureInfo.InvariantCulture))} |"));
            }
        return sb.ToString();
    }

    static int Bucket(double rvol) => rvol < 1 ? 0 : rvol < 1.5 ? 1 : rvol < 2 ? 2 : rvol < 3 ? 3 : 4;

    static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var pos = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(pos);
        var upper = (int)Math.Ceiling(pos);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (pos - lower);
    }

    static string M(double[] values, Func<double[], double> f) =>
        values.Length == 0 ? "-" : f(values).ToString("0.00", CultureInfo.InvariantCulture);

    static string N(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";

    static int Int(string? value, int fallback) =>
        value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
