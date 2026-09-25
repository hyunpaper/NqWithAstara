using System.Collections.Immutable;

namespace Astra.Server.Domain.Validation;

public enum EnginePathKind
{
    Replay,
    Live
}

public enum ReplayLiveParityCause
{
    LookAhead,
    BarNotClosed,
    TimeZone,
    Cost,
    Result
}

public sealed record EngineBarContract(
    string Symbol,
    DateTimeOffset BarStart,
    TimeSpan BarDuration,
    DateTimeOffset LastInputBarStart,
    DateTimeOffset EvaluatedAt,
    string TimeZoneId);

public sealed record EngineCandidateContract(bool Present, string? EventId, string? Disposition);

public sealed record EngineExecutionContract(
    bool Entered,
    double? StopPrice,
    double? TargetPrice,
    double? FillPrice,
    string CostSource,
    double? SpreadCost,
    double? FeePercent);

public sealed record EngineParitySnapshot(
    EnginePathKind Path,
    EngineBarContract Bar,
    EngineCandidateContract Candidate,
    EngineExecutionContract Execution);

public sealed record ReplayLiveParityMismatch(
    string Field,
    string? ReplayValue,
    string? LiveValue,
    ReplayLiveParityCause Cause);

public sealed record ReplayLiveParityReport(
    string Version,
    bool Matches,
    ImmutableArray<ReplayLiveParityMismatch> Mismatches,
    ImmutableArray<ReplayLiveParityCause> Causes);

public static class ReplayLiveParity
{
    public const string Version = "replay-live-parity.1";
    const double PriceTolerance = 0.0000001;

    public static ReplayLiveParityReport Compare(EngineParitySnapshot replay, EngineParitySnapshot live)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(live);
        if (replay.Path != EnginePathKind.Replay)
            throw new ArgumentException("replay 스냅샷 경로가 아닙니다.", nameof(replay));
        if (live.Path != EnginePathKind.Live)
            throw new ArgumentException("실시간 스냅샷 경로가 아닙니다.", nameof(live));

        var rows = ImmutableArray.CreateBuilder<ReplayLiveParityMismatch>();
        CompareBar(replay.Bar, live.Bar, rows);

        var causal = PrimaryCause(replay, live);
        Add(rows, "candidate.present", replay.Candidate.Present, live.Candidate.Present, causal);
        Add(rows, "candidate.eventId", replay.Candidate.EventId, live.Candidate.EventId, causal);
        Add(rows, "candidate.disposition", replay.Candidate.Disposition, live.Candidate.Disposition, causal);
        Add(rows, "entry.entered", replay.Execution.Entered, live.Execution.Entered, causal);
        Add(rows, "plan.stop", replay.Execution.StopPrice, live.Execution.StopPrice, causal);
        Add(rows, "plan.target", replay.Execution.TargetPrice, live.Execution.TargetPrice, causal);
        Add(rows, "execution.fill", replay.Execution.FillPrice, live.Execution.FillPrice,
            CostsDiffer(replay.Execution, live.Execution) ? ReplayLiveParityCause.Cost : causal);
        Add(rows, "cost.source", replay.Execution.CostSource, live.Execution.CostSource,
            ReplayLiveParityCause.Cost);
        Add(rows, "cost.spread", replay.Execution.SpreadCost, live.Execution.SpreadCost,
            ReplayLiveParityCause.Cost);
        Add(rows, "cost.feePercent", replay.Execution.FeePercent, live.Execution.FeePercent,
            ReplayLiveParityCause.Cost);

        var mismatches = rows.ToImmutable();
        return new(Version, mismatches.IsEmpty, mismatches,
            mismatches.Select(x => x.Cause).Distinct().ToImmutableArray());
    }

    static void CompareBar(EngineBarContract replay, EngineBarContract live,
        ImmutableArray<ReplayLiveParityMismatch>.Builder rows)
    {
        Add(rows, "bar.symbol", replay.Symbol, live.Symbol, ReplayLiveParityCause.Result);
        Add(rows, "bar.start", replay.BarStart, live.BarStart, ReplayLiveParityCause.TimeZone);
        Add(rows, "bar.duration", replay.BarDuration, live.BarDuration, ReplayLiveParityCause.BarNotClosed);
        Add(rows, "bar.timeZone", replay.TimeZoneId, live.TimeZoneId, ReplayLiveParityCause.TimeZone);
        Add(rows, "bar.lastInputStart", replay.LastInputBarStart, live.LastInputBarStart,
            ReplayLiveParityCause.LookAhead);
        Add(rows, "bar.evaluatedAt", replay.EvaluatedAt, live.EvaluatedAt,
            IsBeforeClose(replay) || IsBeforeClose(live)
                ? ReplayLiveParityCause.BarNotClosed
                : ReplayLiveParityCause.Result);
        if (replay.LastInputBarStart > replay.BarStart)
            rows.Add(new("replay.lookAhead", Value(replay.LastInputBarStart), Value(replay.BarStart),
                ReplayLiveParityCause.LookAhead));
        if (live.LastInputBarStart > live.BarStart)
            rows.Add(new("live.lookAhead", Value(live.LastInputBarStart), Value(live.BarStart),
                ReplayLiveParityCause.LookAhead));
        if (IsBeforeClose(replay))
            rows.Add(new("replay.barClosed", Value(replay.EvaluatedAt), Value(replay.BarStart + replay.BarDuration),
                ReplayLiveParityCause.BarNotClosed));
        if (IsBeforeClose(live))
            rows.Add(new("live.barClosed", Value(live.EvaluatedAt), Value(live.BarStart + live.BarDuration),
                ReplayLiveParityCause.BarNotClosed));
    }

    static ReplayLiveParityCause PrimaryCause(EngineParitySnapshot replay, EngineParitySnapshot live)
    {
        if (replay.Bar.LastInputBarStart > replay.Bar.BarStart || live.Bar.LastInputBarStart > live.Bar.BarStart)
            return ReplayLiveParityCause.LookAhead;
        if (IsBeforeClose(replay.Bar) || IsBeforeClose(live.Bar))
            return ReplayLiveParityCause.BarNotClosed;
        if (replay.Bar.BarStart != live.Bar.BarStart ||
            !string.Equals(replay.Bar.TimeZoneId, live.Bar.TimeZoneId, StringComparison.Ordinal))
            return ReplayLiveParityCause.TimeZone;
        if (CostsDiffer(replay.Execution, live.Execution))
            return ReplayLiveParityCause.Cost;
        return ReplayLiveParityCause.Result;
    }

    static bool CostsDiffer(EngineExecutionContract replay, EngineExecutionContract live) =>
        !string.Equals(replay.CostSource, live.CostSource, StringComparison.Ordinal) ||
        Different(replay.SpreadCost, live.SpreadCost) || Different(replay.FeePercent, live.FeePercent);

    static bool IsBeforeClose(EngineBarContract bar) => bar.EvaluatedAt < bar.BarStart + bar.BarDuration;

    static void Add(ImmutableArray<ReplayLiveParityMismatch>.Builder rows, string field,
        double? replay, double? live, ReplayLiveParityCause cause)
    {
        if (Different(replay, live)) rows.Add(new(field, Value(replay), Value(live), cause));
    }

    static void Add<T>(ImmutableArray<ReplayLiveParityMismatch>.Builder rows, string field,
        T replay, T live, ReplayLiveParityCause cause)
    {
        if (!EqualityComparer<T>.Default.Equals(replay, live))
            rows.Add(new(field, Value(replay), Value(live), cause));
    }

    static bool Different(double? left, double? right) => left.HasValue != right.HasValue ||
        left.HasValue && Math.Abs(left.Value - right!.Value) > PriceTolerance;

    static string? Value<T>(T value) => value switch
    {
        null => null,
        DateTimeOffset timestamp => timestamp.ToString("O"),
        TimeSpan duration => duration.ToString("c"),
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
