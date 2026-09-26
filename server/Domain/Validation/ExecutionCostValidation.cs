namespace Astra.Server.Domain.Validation;

public enum ExecutionCostBasis { Modeled, Observed }

public enum ExecutionValidationSegment { InSample, OutOfSample }

public enum ExecutionFillStatus { Filled, Rejected, Cancelled, Expired }

public static class ExecutionCostAvailability
{
    public const string Available = "AVAILABLE";
    public const string DataUnavailable = "DATA_UNAVAILABLE";
    public const string GrossPnlUnavailable = "GROSS_PNL_UNAVAILABLE";
    public const string SpreadUnavailable = "SPREAD_UNAVAILABLE";
    public const string FeeUnavailable = "FEE_UNAVAILABLE";
    public const string SlippageUnavailable = "SLIPPAGE_UNAVAILABLE";
    public const string PriceGapUnavailable = "PRICE_GAP_UNAVAILABLE";
    public const string BorrowCostUnavailable = "BORROW_COST_UNAVAILABLE";
    public const string LiquidityUnavailable = "LIQUIDITY_UNAVAILABLE";
    public const string FillFailed = "FILL_FAILED";
    public const string OutsideSession = "OUTSIDE_SESSION";
    public const string FutureObservation = "FUTURE_OBSERVATION";
}

public sealed record ExecutionCostObservation(string Id, string Symbol, DateOnly TradingDate,
    DateTimeOffset SessionStart, DateTimeOffset SessionEnd, DateTimeOffset ObservedAt,
    DateTimeOffset? FilledAt, DateTimeOffset? ExitedAt, ExecutionValidationSegment Segment,
    ExecutionCostBasis Basis, string CostProfile, ExecutionFillStatus FillStatus,
    double? GrossPnlPercent, double? SpreadPercent, double? FeePercent, double? SlippagePercent,
    double? PriceGapPercent, double? BorrowCostPercent, decimal? RequestedQuantity,
    decimal? AvailableQuantity, bool DataAvailable = true);

public sealed record ExecutionCostRow(string Id, string Symbol, DateOnly TradingDate,
    DateTimeOffset ObservedAt,
    ExecutionValidationSegment Segment, ExecutionCostBasis Basis, string CostProfile,
    ExecutionFillStatus FillStatus, string Availability, IReadOnlyList<string> Reasons,
    double? GrossPnlPercent, double? SpreadPercent, double? FeePercent, double? SlippagePercent,
    double? PriceGapPercent, double? BorrowCostPercent, double? TotalCostPercent,
    double? NetPnlPercent, decimal? LiquidityCoveragePercent);

public sealed record ExecutionCostMetrics(int Samples, double? GrossPnlPercent, double? TotalCostPercent,
    double? NetPnlPercent, double? ProfitFactor, double? MaxDrawdownPercent);

public sealed record ExecutionCostCohort(ExecutionValidationSegment Segment, ExecutionCostBasis Basis,
    string CostProfile, int Observations, int Filled, int FillFailures, int Available, int Unavailable,
    int LiquidityShortfalls, ExecutionCostMetrics Metrics);

public sealed record ExecutionCostValidationReport(DateTimeOffset AsOf, IReadOnlyList<ExecutionCostRow> Rows,
    IReadOnlyList<ExecutionCostCohort> Cohorts, IReadOnlyDictionary<string, int> ReasonCounts);

public static class ExecutionCostValidator
{
    public static ExecutionCostValidationReport Evaluate(IReadOnlyList<ExecutionCostObservation> observations,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var rows = observations.Select(x => Classify(x, asOf))
            .OrderBy(x => x.TradingDate).ThenBy(x => x.ObservedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var cohorts = rows.GroupBy(x => (x.Segment, x.Basis, x.CostProfile))
            .OrderBy(x => x.Key.Segment).ThenBy(x => x.Key.Basis)
            .ThenBy(x => x.Key.CostProfile, StringComparer.Ordinal)
            .Select(group => BuildCohort(group.Key.Segment, group.Key.Basis, group.Key.CostProfile,
                group.ToArray())).ToArray();
        var reasons = rows.SelectMany(x => x.Reasons).GroupBy(x => x, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return new ExecutionCostValidationReport(asOf, rows, cohorts, reasons);
    }

    static ExecutionCostRow Classify(ExecutionCostObservation row, DateTimeOffset asOf)
    {
        var reasons = new List<string>();
        if (row.ObservedAt > asOf || row.FilledAt > asOf || row.ExitedAt > asOf)
            reasons.Add(ExecutionCostAvailability.FutureObservation);
        if (row.ObservedAt < row.SessionStart || row.ObservedAt > row.SessionEnd ||
            row.FilledAt < row.SessionStart || row.FilledAt > row.SessionEnd ||
            row.ExitedAt < row.SessionStart || row.ExitedAt > row.SessionEnd)
            reasons.Add(ExecutionCostAvailability.OutsideSession);
        if (!row.DataAvailable) reasons.Add(ExecutionCostAvailability.DataUnavailable);
        if (row.FillStatus != ExecutionFillStatus.Filled) reasons.Add(ExecutionCostAvailability.FillFailed);
        if (!FiniteSigned(row.GrossPnlPercent)) reasons.Add(ExecutionCostAvailability.GrossPnlUnavailable);
        Missing(row.SpreadPercent, ExecutionCostAvailability.SpreadUnavailable, reasons);
        Missing(row.FeePercent, ExecutionCostAvailability.FeeUnavailable, reasons);
        Missing(row.SlippagePercent, ExecutionCostAvailability.SlippageUnavailable, reasons);
        Missing(row.PriceGapPercent, ExecutionCostAvailability.PriceGapUnavailable, reasons);
        Missing(row.BorrowCostPercent, ExecutionCostAvailability.BorrowCostUnavailable, reasons);
        if (row.RequestedQuantity is null || row.AvailableQuantity is null)
            reasons.Add(ExecutionCostAvailability.LiquidityUnavailable);
        var coverage = Coverage(row.RequestedQuantity, row.AvailableQuantity);
        var complete = reasons.Count == 0 && FiniteSigned(row.GrossPnlPercent);
        double? totalCost = complete ? Sum(row.SpreadPercent, row.FeePercent, row.SlippagePercent,
            row.PriceGapPercent, row.BorrowCostPercent) : null;
        double? net = complete ? row.GrossPnlPercent!.Value - totalCost!.Value : null;
        return new ExecutionCostRow(row.Id, row.Symbol, row.TradingDate, row.ObservedAt, row.Segment, row.Basis,
            row.CostProfile, row.FillStatus,
            reasons.Count == 0 ? ExecutionCostAvailability.Available : reasons[0], reasons,
            FiniteSigned(row.GrossPnlPercent) ? row.GrossPnlPercent : null,
            Finite(row.SpreadPercent) ? row.SpreadPercent : null,
            Finite(row.FeePercent) ? row.FeePercent : null,
            Finite(row.SlippagePercent) ? row.SlippagePercent : null,
            Finite(row.PriceGapPercent) ? row.PriceGapPercent : null,
            Finite(row.BorrowCostPercent) ? row.BorrowCostPercent : null,
            totalCost, net, coverage);
    }

    static ExecutionCostCohort BuildCohort(ExecutionValidationSegment segment, ExecutionCostBasis basis,
        string profile, ExecutionCostRow[] rows)
    {
        var valid = rows.Where(x => x.Availability == ExecutionCostAvailability.Available &&
            x.NetPnlPercent is not null).ToArray();
        var nets = valid.Select(x => x.NetPnlPercent!.Value).ToArray();
        var gains = nets.Where(x => x > 0).Sum();
        var losses = Math.Abs(nets.Where(x => x < 0).Sum());
        double? profitFactor = valid.Length == 0 ? null : losses == 0 ? (gains > 0 ? double.PositiveInfinity : null) : gains / losses;
        var peak = 0d;
        var equity = 0d;
        var maxDrawdown = 0d;
        foreach (var net in nets)
        {
            equity += net;
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, peak - equity);
        }
        return new ExecutionCostCohort(segment, basis, profile, rows.Length,
            rows.Count(x => x.FillStatus == ExecutionFillStatus.Filled),
            rows.Count(x => x.FillStatus != ExecutionFillStatus.Filled), valid.Length, rows.Length - valid.Length,
            rows.Count(x => x.LiquidityCoveragePercent is < 100m),
            new ExecutionCostMetrics(valid.Length, SumOrNull(valid.Select(x => x.GrossPnlPercent)),
                SumOrNull(valid.Select(x => x.TotalCostPercent)), SumOrNull(valid.Select(x => x.NetPnlPercent)),
                profitFactor is null || double.IsInfinity(profitFactor.Value) ? profitFactor : Math.Round(profitFactor.Value, 6),
                valid.Length == 0 ? null : Math.Round(maxDrawdown, 6)));
    }

    static void Missing(double? value, string reason, List<string> reasons)
    {
        if (!Finite(value)) reasons.Add(reason);
    }

    static bool Finite(double? value) => value is { } number && double.IsFinite(number) && number >= 0;

    static bool FiniteSigned(double? value) => value is { } number && double.IsFinite(number);

    static double Sum(params double?[] values) => values.Sum(x => x!.Value);

    static double? SumOrNull(IEnumerable<double?> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? null : Math.Round(array.Sum(x => x!.Value), 6);
    }

    static decimal? Coverage(decimal? requested, decimal? available) => requested is > 0 && available is >= 0
        ? Math.Round(available.Value * 100m / requested.Value, 2) : null;
}
