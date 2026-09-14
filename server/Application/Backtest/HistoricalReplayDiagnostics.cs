using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Astra.Server.Domain;

namespace Astra.Server.Application.Backtest;

public sealed record HistoricalReplayCostSummary(int ClosedTrades, int Wins, int Losses, double? WinRatePercent,
    double GrossPnlPercent, double FeePercent, double? SlippagePercent, double NetPnlPercent);

public sealed record HistoricalReplayCohort(string Dimension, string Key, string Label, bool Collected,
    HistoricalReplayCostSummary Summary);

public sealed record HistoricalReplayDiagnostics(string Version, string ResultFingerprint,
    HistoricalReplayCostSummary Summary, ImmutableArray<HistoricalReplayCohort> Cohorts, string Notice);

public static class HistoricalReplayDiagnosticsBuilder
{
    public const string Version = "replay-diagnostics.1";

    public static HistoricalReplayDiagnostics Build(IEnumerable<HistoricalReplayTradeResult> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rows = source.ToArray();
        var closed = rows.Where(IsClosed).ToArray();
        var cohorts = new List<HistoricalReplayCohort>();
        Add(cohorts, closed, "kind", x => (x.Trade.Kind, x.Trade.Kind, true));
        Add(cohorts, closed, "trend", x => x.Trade.Structure?.TrendAtEntry is { Length: > 0 } trend
            ? (trend, trend, true) : ("UNCOLLECTED", "추세 미수집", false));
        Add(cohorts, closed, "entryQuality", Quality);
        Add(cohorts, closed, "planNetR", PlanNetR);
        return new HistoricalReplayDiagnostics(Version, Fingerprint(rows), Summary(closed), cohorts
            .OrderBy(x => x.Dimension, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToImmutableArray(),
            "gross·fee·net은 완료 봉 기반 가상 체결의 확정 집계입니다. slippage는 과거 호가·체결 입력이 없어 미수집이며, 코호트 차이는 가설 검토용입니다.");
    }

    static bool IsClosed(HistoricalReplayTradeResult row) => row.Trade.Status != "OPEN" && row.Trade.ExitAt.HasValue;

    static void Add(List<HistoricalReplayCohort> destination, HistoricalReplayTradeResult[] rows, string dimension,
        Func<HistoricalReplayTradeResult, (string Key, string Label, bool Collected)> classify)
    {
        destination.AddRange(rows.GroupBy(classify, x => x, EqualityComparer<(string Key, string Label, bool Collected)>.Default)
            .Select(group => new HistoricalReplayCohort(dimension, group.Key.Key, group.Key.Label, group.Key.Collected,
                Summary(group.ToArray()))));
    }

    static (string Key, string Label, bool Collected) Quality(HistoricalReplayTradeResult row) =>
        row.Trade.Structure?.EntryQualityAtEntry switch
        {
            null => ("UNCOLLECTED", "EntryQuality 미수집", false),
            < 50 => ("LT_50", "50 미만", true),
            < 55 => ("50_55", "50 이상 55 미만", true),
            < 60 => ("55_60", "55 이상 60 미만", true),
            < 70 => ("60_70", "60 이상 70 미만", true),
            _ => ("GE_70", "70 이상", true)
        };

    static (string Key, string Label, bool Collected) PlanNetR(HistoricalReplayTradeResult row) =>
        row.Trade.Structure?.PlanSnapshot.NetR switch
        {
            null => ("UNCOLLECTED", "NetR 미수집", false),
            < 1.5m => ("LT_1_5", "1.5 미만", true),
            < 2m => ("1_5_2_0", "1.5 이상 2.0 미만", true),
            < 3m => ("2_0_3_0", "2.0 이상 3.0 미만", true),
            _ => ("GE_3_0", "3.0 이상", true)
        };

    static HistoricalReplayCostSummary Summary(IReadOnlyList<HistoricalReplayTradeResult> rows)
    {
        var wins = rows.Count(x => x.NetPnlPercent is > 0);
        var slippage = rows.All(x => x.SlippagePercent.HasValue)
            ? (double?)Math.Round(rows.Sum(x => x.SlippagePercent!.Value), 6) : null;
        return new HistoricalReplayCostSummary(rows.Count, wins, rows.Count - wins,
            rows.Count == 0 ? null : Math.Round(wins * 100d / rows.Count, 1),
            Math.Round(rows.Sum(x => x.GrossPnlPercent ?? 0), 6), Math.Round(rows.Sum(x => x.FeePercent), 6), slippage,
            Math.Round(rows.Sum(x => x.NetPnlPercent ?? 0), 6));
    }

    static string Fingerprint(IEnumerable<HistoricalReplayTradeResult> rows)
    {
        var data = string.Join('\n', rows.OrderBy(x => x.Trade.EnteredAt).ThenBy(x => x.Trade.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.Trade.Structure?.EntryEventId, StringComparer.Ordinal).Select(x => string.Join('|',
                x.Trade.Symbol, x.Trade.Kind, x.Trade.EnteredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                x.Trade.Status, Number(x.Trade.EntryPrice), Number(x.Trade.ExitPrice),
                x.Trade.ExitAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                x.Trade.Structure?.TrendAtEntry ?? string.Empty, Number(x.Trade.Structure?.EntryQualityAtEntry),
                Number(x.Trade.Structure?.PlanSnapshot.NetR), Number(x.GrossPnlPercent), Number(x.FeePercent),
                Number(x.SlippagePercent), Number(x.NetPnlPercent))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    static string Number(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
    static string Number(decimal? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
}
