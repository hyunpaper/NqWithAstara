using System.Globalization;
using System.Text;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Domain.Confluence;
using Astra.Server.Infrastructure;

namespace Astra.Server.Backtest;

/// <summary>
/// `dotnet run --project server -- confluence-measure` 진입점 (C5, #169). 서버를 띄우지 않고
/// 저장 봉만 재생해 기법별 통계와 `App_Data/confluence-weights.json`을 만든다.
/// </summary>
public static class ConfluenceMeasureCommand
{
    public const string Name = "confluence-measure";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(clock);

        var measurement = MeasurementPolicy.Default;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var window = ConfluenceWalkForward.ForApplyWeek(today, measurement);
        var from = Option(args, "--from") is { } rawFrom ? Date(rawFrom, "--from") : window.From;
        var to = Option(args, "--to") is { } rawTo ? Date(rawTo, "--to") : window.To;
        var horizon = Option(args, "--horizon") is { } rawHorizon
            ? int.Parse(rawHorizon, CultureInfo.InvariantCulture)
            : measurement.DefaultHorizonBars;
        var root = Option(args, "--root") ?? ResolveContentRoot();
        var benchmark = Option(args, "--benchmark") ?? new ConfluenceOptions().BenchmarkSymbol;
        if (to < from) { output.WriteLine("--to는 --from보다 앞설 수 없다."); return 2; }

        var replayBars = Directory.Exists(Path.Combine(root, "bars"))
            ? Path.Combine(root, "bars")
            : Path.Combine(root, "App_Data", "bars");
        var replay = new ConfluenceReplay(new BarStore(replayBars),
            ConfluencePolicy.Default, measurement);
        var report = await replay.RunAsync(from, to, horizon, benchmark, ct);
        output.WriteLine($"측정 창 {from:yyyy-MM-dd}~{to:yyyy-MM-dd} · 날짜 {report.Days}일 · 심볼 " +
                         $"{report.Symbols}개 · 봉 {report.Bars}개 · 신호 {report.Signals}건 · 지평 {horizon}봉");
        if (report.Bars == 0)
        {
            output.WriteLine("저장 봉이 없어 가중치 파일을 쓰지 않는다(전부 1.0 유지).");
            return 0;
        }

        output.Write(Table(report));
        if (Directory.Exists(Path.Combine(root, "bars")))
        {
            output.Write(SymbolTable(report));
            var split = from.AddDays(21);
            if (split <= to)
            {
                var training = await replay.RunAsync(from, split.AddDays(-1), horizon, benchmark, ct);
                var validation = await replay.RunAsync(split, to, horizon, benchmark, ct);
                output.WriteLine($"워크포워드 학습 3주 {training.From:yyyy-MM-dd}~{training.To:yyyy-MM-dd} · " +
                                 $"검증 1주 {validation.From:yyyy-MM-dd}~{validation.To:yyyy-MM-dd}");
                output.WriteLine("검증 결과");
                output.Write(Table(validation));
            }
            output.WriteLine("호가·체결 입력 없음: OBI/LR_DELTA는 unavailable이며 표본 0이다.");
            return 0;
        }
        var document = ConfluenceWeightsDocument.FromMeasurement(report, clock.GetUtcNow());
        output.WriteLine($"가중치 파일: {ConfluenceWeightsStore.Save(root, document)} ({document.WeightsVersion})");
        return 0;
    }

    public static string SymbolTable(ConfluenceMeasurementReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("| 심볼 | 기법 | n | 적중률 | 평균수익(ATR) | 비용후(ATR) |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var symbol in report.BySymbol.Keys.Order(StringComparer.OrdinalIgnoreCase))
            foreach (var row in report.BySymbol[symbol])
                builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {symbol} | {row.Technique} | {row.N} | {row.HitRate:0.0000} | {row.MeanReturnAtr:0.0000} | {row.MeanNetReturnAtr:0.0000} |"));
        return builder.ToString();
    }

    /// <summary>PR 본문에 그대로 붙일 수 있는 마크다운 표.</summary>
    public static string Table(ConfluenceMeasurementReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("| 기법 | 지평 | n | 적중률 | Wilson 95% CI | Brier | p | 평균수익(ATR) | 비용후(ATR) | 상태 | w |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var horizon in report.ByHorizon.Keys.OrderBy(x => x))
            foreach (var row in report.ByHorizon[horizon])
                builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {row.Technique} | {row.HorizonBars} | {row.N} | {row.HitRate:0.0000} | " +
                    $"[{row.Ci.Low:0.0000}, {row.Ci.High:0.0000}] | {row.Brier:0.0000} | {row.PValue:0.0000} | " +
                    $"{row.MeanReturnAtr:0.0000} | {row.MeanNetReturnAtr:0.0000} | {row.StatusText} | " +
                    $"{row.Weight:0.0000} |"));
        return builder.ToString();
    }

    /// <summary>`App_Data`를 가진 가장 가까운 조상 디렉터리. 못 찾으면 현재 디렉터리다.</summary>
    public static string ResolveContentRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (Directory.Exists(Path.Combine(directory.FullName, "App_Data")))
                    return directory.FullName;
        return Directory.GetCurrentDirectory();
    }

    public static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    static DateOnly Date(string value, string option) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", out var parsed)
            ? parsed
            : throw new ArgumentException($"{option} 값은 yyyy-MM-dd 형식이어야 한다: {value}", nameof(value));
}
