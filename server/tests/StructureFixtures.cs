using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Domain.Structure;

/// <summary>v5 구조 엔진 D1 테스트 fixture. 임의 값이며 실제 종목 추천이 아니다.</summary>
static class Fx
{
    public static readonly DateTimeOffset SessionStart = DateTimeOffset.Parse("2026-09-09T09:30:00-04:00");
    public static readonly DateTimeOffset SessionEnd = DateTimeOffset.Parse("2026-09-09T16:00:00-04:00");
    public const string Symbol = "TEST";

    public static DateTimeOffset At(int minute) => SessionStart.AddMinutes(minute);

    public static Candle Candle(int minute, double open, double high, double low, double close, double volume = 1000)
        => new(At(minute), open, high, low, close, volume);

    public static Candle Flat(int minute, double low, double high, double close, double volume = 1000)
        => new(At(minute), (low + high) / 2, high, low, close, volume);

    public static StructureBar Bar(int minute, decimal open, decimal high, decimal low, decimal close, double volume = 1000)
        => new(At(minute), At(minute + 1), open, high, low, close, volume);

    /// <summary>동일 형태(TR=High-Low)로 이어지는 봉. 전 종가가 [Low,High] 안에 있으면 ATR이 High-Low로 수렴한다.</summary>
    public static StructureBar Steady(int minute, decimal low, decimal high, decimal close, double volume = 1000)
        => new(At(minute), At(minute + 1), (low + high) / 2m, high, low, close, volume);

    public static ImmutableArray<StructureBar> Bars(params StructureBar[] bars) => bars.ToImmutableArray();

    public static ZoneSource Pivot(string tag, decimal price, int occurredMinute, int confirmedMinute,
        BarTimeframe timeframe = BarTimeframe.OneMinute) =>
        new(StructureMath.SourceId("pivot", Symbol, tag), ZoneSourceFamily.Pivot,
            timeframe == BarTimeframe.OneMinute ? "pivot-1m-L" : "pivot-5m-L", price, At(occurredMinute),
            At(confirmedMinute), false);

    public static ZoneSource Daily(string tag, decimal price, int ageSessions = 1) =>
        new(StructureMath.SourceId("daily", Symbol, tag), ZoneSourceFamily.ContextLevel, "daily-L", price,
            SessionStart, SessionStart, false, ageSessions);

    public static ZoneSource ProfileSource(string tag, decimal price, int cutoffMinute) =>
        new(StructureMath.SourceId("profile", Symbol, tag), ZoneSourceFamily.Profile, "profile-node", price,
            At(cutoffMinute), At(cutoffMinute), true);

    public static PriceZone Zone(decimal lower, decimal upper, params ZoneSource[] sources)
    {
        var ordered = sources.OrderBy(x => x.ConfirmedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToImmutableArray();
        var representative = ordered.FirstOrDefault(x => !x.Temporary) ?? ordered[0];
        return new PriceZone(representative.Id, 1, 1, lower, upper,
            ZoneRole.Unresolved, ZoneRole.Unresolved, ordered.Min(x => x.ConfirmedAt), ordered.Max(x => x.ConfirmedAt),
            ordered, ImmutableArray<EvidenceGroup>.Empty, ImmutableArray<string>.Empty, null, false,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<ZoneRoleChange>.Empty,
            ordered.All(x => x.Family == ZoneSourceFamily.Profile), false);
    }

    public static ZoneEvaluationResult Evaluate(PriceZone zone, ImmutableArray<StructureBar> bars, int cutoffMinute,
        StructurePolicy? policy = null) =>
        ZoneEvaluator.Evaluate([zone], ZoneEvaluationRequest.Create(SessionStart, At(cutoffMinute), bars),
            policy ?? StructurePolicy.Default);
}
