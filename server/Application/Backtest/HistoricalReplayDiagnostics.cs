using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed record HistoricalReplayDiagnosticMetric(bool Present, bool Null, bool Finite, double? Value)
{
    public static HistoricalReplayDiagnosticMetric Read(JsonElement row, string name)
    {
        if (!TryGetProperty(row, name, out var value)) return new(false, false, false, null);
        if (value.ValueKind == JsonValueKind.Null) return new(true, true, false, null);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return new(true, false, false, null);
        return new(true, false, double.IsFinite(number), number);
    }

    static bool TryGetProperty(JsonElement row, string name, out JsonElement value)
    {
        foreach (var property in row.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}

public sealed record HistoricalReplayDiagnosticTrade(SimTrade Trade, HistoricalReplayDiagnosticMetric GrossPnlPercent,
    HistoricalReplayDiagnosticMetric FeePercent, HistoricalReplayDiagnosticMetric SlippagePercent,
    HistoricalReplayDiagnosticMetric NetPnlPercent)
{
    public static bool TryRead(string line, out HistoricalReplayDiagnosticTrade? row)
    {
        row = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "trade", out var trade)) return false;
            var parsed = JsonSerializer.Deserialize<SimTrade>(trade.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed is null) return false;
            row = new(parsed, HistoricalReplayDiagnosticMetric.Read(root, "grossPnlPercent"),
                HistoricalReplayDiagnosticMetric.Read(root, "feePercent"),
                HistoricalReplayDiagnosticMetric.Read(root, "slippagePercent"),
                HistoricalReplayDiagnosticMetric.Read(root, "netPnlPercent"));
            return true;
        }
        catch (JsonException) { return false; }
    }

    static bool TryGetProperty(JsonElement row, string name, out JsonElement value)
    {
        foreach (var property in row.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}

public static class HistoricalReplayDiagnosticsBuilder
{
    public const string Version = "replay-diagnostics.1";

    public static HistoricalReplayDiagnostics Build(IEnumerable<HistoricalReplayDiagnosticTrade> source)
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

    static bool IsClosed(HistoricalReplayDiagnosticTrade row) => row.Trade.Status != "OPEN" && row.Trade.ExitAt.HasValue;

    static void Add(List<HistoricalReplayCohort> destination, HistoricalReplayDiagnosticTrade[] rows, string dimension,
        Func<HistoricalReplayDiagnosticTrade, (string Key, string Label, bool Collected)> classify)
    {
        destination.AddRange(rows.GroupBy(classify, x => x, EqualityComparer<(string Key, string Label, bool Collected)>.Default)
            .Select(group => new HistoricalReplayCohort(dimension, group.Key.Key, group.Key.Label, group.Key.Collected,
                Summary(group.ToArray()))));
    }

    static (string Key, string Label, bool Collected) Quality(HistoricalReplayDiagnosticTrade row) =>
        row.Trade.Structure?.EntryQualityAtEntry switch
        {
            null => ("UNCOLLECTED", "EntryQuality 미수집", false),
            < 50 => ("LT_50", "50 미만", true),
            < 55 => ("50_55", "50 이상 55 미만", true),
            < 60 => ("55_60", "55 이상 60 미만", true),
            < 70 => ("60_70", "60 이상 70 미만", true),
            _ => ("GE_70", "70 이상", true)
        };

    static (string Key, string Label, bool Collected) PlanNetR(HistoricalReplayDiagnosticTrade row) =>
        row.Trade.Structure?.PlanSnapshot.NetR switch
        {
            null => ("UNCOLLECTED", "NetR 미수집", false),
            < 1.5m => ("LT_1_5", "1.5 미만", true),
            < 2m => ("1_5_2_0", "1.5 이상 2.0 미만", true),
            < 3m => ("2_0_3_0", "2.0 이상 3.0 미만", true),
            _ => ("GE_3_0", "3.0 이상", true)
        };

    static HistoricalReplayCostSummary Summary(IReadOnlyList<HistoricalReplayDiagnosticTrade> rows)
    {
        var netRows = rows.Where(x => x.NetPnlPercent.Finite).ToArray();
        var wins = netRows.Count(x => x.NetPnlPercent.Value > 0);
        var costs = rows.Where(x => x.GrossPnlPercent.Finite && x.FeePercent.Finite && x.NetPnlPercent.Finite &&
            (!x.SlippagePercent.Present || x.SlippagePercent.Null || x.SlippagePercent.Finite)).ToArray();
        var differences = costs.Select(x => Math.Abs(x.GrossPnlPercent.Value!.Value - x.FeePercent.Value!.Value -
            (x.SlippagePercent.Finite ? x.SlippagePercent.Value!.Value : 0) - x.NetPnlPercent.Value!.Value)).ToArray();
        var reconciled = differences.Count(x => x <= .000001d);
        var complete = costs.Length == rows.Count;
        var slippageCollected = complete && rows.All(x => x.SlippagePercent.Finite);
        return new HistoricalReplayCostSummary(rows.Count, wins, netRows.Length - wins,
            netRows.Length == 0 ? null : Math.Round(wins * 100d / netRows.Length, 1), costs.Length,
            rows.Count - costs.Length,
            complete ? Math.Round(costs.Sum(x => x.GrossPnlPercent.Value!.Value), 6) : null,
            complete ? Math.Round(costs.Sum(x => x.FeePercent.Value!.Value), 6) : null,
            slippageCollected ? Math.Round(rows.Sum(x => x.SlippagePercent.Value!.Value), 6) : null,
            complete ? Math.Round(costs.Sum(x => x.NetPnlPercent.Value!.Value), 6) : null,
            reconciled, differences.Length - reconciled, rows.Count - differences.Length,
            differences.Length == 0 ? null : Math.Round(differences.Max(), 6),
            "slippage 수집 행: gross - fee - slippage = net; slippage 미수집 행: gross - fee = net");
    }

    static string Fingerprint(IEnumerable<HistoricalReplayDiagnosticTrade> rows)
    {
        var data = string.Join('\n', rows.OrderBy(x => x.Trade.EnteredAt).ThenBy(x => x.Trade.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.Trade.Structure?.EntryEventId, StringComparer.Ordinal).ThenBy(x => x.Trade.Id, StringComparer.Ordinal)
            .Select(x => string.Join('|', x.Trade.Id, x.Trade.Symbol, x.Trade.Kind, x.Trade.EnteredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                x.Trade.Status, Number(x.Trade.EntryPrice), Number(x.Trade.ExitPrice),
                x.Trade.ExitAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                x.Trade.Structure?.TrendAtEntry ?? string.Empty, Number(x.Trade.Structure?.EntryQualityAtEntry),
                Number(x.Trade.Structure?.PlanSnapshot.NetR), Number(x.GrossPnlPercent.Value), Number(x.FeePercent.Value),
                Number(x.SlippagePercent.Value), Number(x.NetPnlPercent.Value))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    static string Number(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
    static string Number(decimal? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;
}
