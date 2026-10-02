using Astra.Server.Domain.Structure;

namespace Astra.Server;

public sealed record BenchmarkEntryGateResult(bool Allowed, bool Available, double? ReturnPercent, string Reason);

public static class BenchmarkEntryGate
{
    public const int LookbackMinutes = 15;
    public const string NotRequired = "REBOUND_BENCHMARK_NOT_REQUIRED";
    public const string NonNegative = "REBOUND_BENCHMARK_NON_NEGATIVE";
    public const string Negative = "REBOUND_BENCHMARK_NEGATIVE";
    public const string Unavailable = "REBOUND_BENCHMARK_UNAVAILABLE";

    public static double? SessionReturnPercent(IReadOnlyList<Candle>? benchmarkBars, DateTimeOffset confirmationEnd)
    {
        if (benchmarkBars is null ||
            benchmarkBars is System.Collections.Immutable.ImmutableArray<Candle> { IsDefault: true } ||
            benchmarkBars.Count == 0) return null;
        var currentStart = confirmationEnd - TimeSpan.FromMinutes(1);
        var available = benchmarkBars.Where(x => x.Timestamp <= currentStart).OrderBy(x => x.Timestamp).ToArray();
        if (available.Length == 0) return null;
        var first = available[0].Open;
        var current = available[^1].Close;
        if (!double.IsFinite(first) || first <= 0 || !double.IsFinite(current) || current <= 0) return null;
        return (current / first - 1d) * 100d;
    }

    public static BenchmarkEntryGateResult Evaluate(StructurePolicy policy, string? setupKind, TradeSide side,
        IReadOnlyList<Candle>? benchmarkBars, DateTimeOffset confirmationEnd)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.RequirePositiveBenchmarkForRebound || side != TradeSide.Long ||
            !string.Equals(setupKind, "REBOUND", StringComparison.Ordinal))
            return new(true, false, null, NotRequired);

        if (benchmarkBars is null || benchmarkBars.Count == 0)
            return new(true, false, null, Unavailable);

        var currentStart = confirmationEnd - TimeSpan.FromMinutes(1);
        var firstStart = currentStart - TimeSpan.FromMinutes(LookbackMinutes);
        var exact = new Dictionary<DateTimeOffset, Candle>();
        foreach (var bar in benchmarkBars)
        {
            if (bar.Timestamp < firstStart || bar.Timestamp > currentStart) continue;
            if (!exact.TryAdd(bar.Timestamp, bar)) return new(true, false, null, Unavailable);
        }

        for (var minute = 0; minute <= LookbackMinutes; minute++)
            if (!exact.ContainsKey(firstStart.AddMinutes(minute)))
                return new(true, false, null, Unavailable);

        var first = exact[firstStart].Close;
        var current = exact[currentStart].Close;
        if (!double.IsFinite(first) || first <= 0 || !double.IsFinite(current) || current <= 0)
            return new(true, false, null, Unavailable);

        var value = (current / first - 1d) * 100d;
        return value < 0
            ? new(false, true, value, Negative)
            : new(true, true, value, NonNegative);
    }
}
