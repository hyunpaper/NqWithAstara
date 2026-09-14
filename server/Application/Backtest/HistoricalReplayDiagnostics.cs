using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Astra.Server.Domain;

namespace Astra.Server.Application.Backtest;

public sealed record HistoricalReplayCostSummary(int ClosedTrades, int Wins, int Losses, double? WinRatePercent,
    int CostCollectedTrades, int CostUncollectedTrades, double? GrossPnlPercent, double? FeePercent,
    double? SlippagePercent, double? NetPnlPercent, int ReconciledTrades, int MismatchedTrades,
    int UnverifiableTrades, double? MaxAbsoluteDifference, string ReconciliationBasis);

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
            "gross·fee·net은 완료 봉 기반 가상 체결의 확정 집계입니다. slippage가 미수집이면 gross - fee = net을 검증하고, 수집되면 gross - fee - slippage = net을 검증합니다. 코호트 차이는 가설 검토용입니다.");
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
        var netRows = rows.Where(x => Finite(x.NetPnlPercent)).ToArray();
        var wins = netRows.Count(x => x.NetPnlPercent > 0);
        var costs = rows.Where(x => Finite(x.GrossPnlPercent) && Finite(x.FeePercent) && Finite(x.NetPnlPercent)).ToArray();
        var differences = costs.Select(x => Math.Abs(x.GrossPnlPercent!.Value - x.FeePercent -
            (x.SlippagePercent ?? 0) - x.NetPnlPercent!.Value)).ToArray();
        var reconciled = differences.Count(x => x <= .000001d);
        var complete = costs.Length == rows.Count;
        var slippageCollected = complete && rows.All(x => Finite(x.SlippagePercent));
        return new HistoricalReplayCostSummary(rows.Count, wins, netRows.Length - wins,
            netRows.Length == 0 ? null : Math.Round(wins * 100d / netRows.Length, 1), costs.Length,
            rows.Count - costs.Length,
            complete ? Math.Round(costs.Sum(x => x.GrossPnlPercent!.Value), 6) : null,
            complete ? Math.Round(costs.Sum(x => x.FeePercent), 6) : null,
            slippageCollected ? Math.Round(rows.Sum(x => x.SlippagePercent!.Value), 6) : null,
            complete ? Math.Round(costs.Sum(x => x.NetPnlPercent!.Value), 6) : null,
            reconciled, differences.Length - reconciled, rows.Count - differences.Length,
            differences.Length == 0 ? null : Math.Round(differences.Max(), 6),
            "slippage 수집 행: gross - fee - slippage = net; slippage 미수집 행: gross - fee = net");
    }

    static string Fingerprint(IEnumerable<HistoricalReplayTradeResult> rows)
    {
        var data = string.Join('\n', rows.OrderBy(x => x.Trade.EnteredAt).ThenBy(x => x.Trade.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.Trade.Structure?.EntryEventId, StringComparer.Ordinal).ThenBy(x => x.Trade.Id, StringComparer.Ordinal)
            .Select(x => string.Join('|', x.Trade.Id, x.Trade.Symbol, x.Trade.Kind, x.Trade.EnteredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                x.Trade.Status, Number(x.Trade.EntryPrice), Number(x.Trade.ExitPrice),
                x.Trade.ExitAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                x.Trade.Structure?.TrendAtEntry ?? string.Empty, Number(x.Trade.Structure?.EntryQualityAtEntry),
                Number(x.Trade.Structure?.PlanSnapshot.NetR), Number(x.GrossPnlPercent), Number(x.FeePercent),
                Number(x.SlippagePercent), Number(x.NetPnlPercent))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    static string Number(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
    static string Number(decimal? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
    static bool Finite(double? value) => value is { } number && double.IsFinite(number);
}
