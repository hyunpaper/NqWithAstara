using System.Collections.Immutable;
using System.Globalization;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;

static class Cf
{
    public const string Symbol = "TEST";

    public static readonly DateTimeOffset SessionStart =
        DateTimeOffset.Parse("2026-09-11T13:30:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    public static IndicatorBar Bar(int index, double close, double volume = 1000)
    {
        var start = SessionStart.AddMinutes(index);
        return new IndicatorBar(start, start.AddMinutes(1), (decimal)close, (decimal)(close + .1),
            (decimal)(close - .1), (decimal)close, (decimal)volume);
    }

    public static ImmutableArray<IndicatorBar> Closes(IEnumerable<double> closes) =>
        closes.Select((close, index) => Bar(index, close)).ToImmutableArray();

    public static ImmutableArray<IndicatorBar> Ramp(int count, double start, double step) =>
        Closes(Enumerable.Range(0, count).Select(i => start + i * step));

    public static ImmutableArray<IndicatorBar> Zigzag(int count, double mid, double amplitude) =>
        Closes(Enumerable.Range(0, count).Select(i => mid + (i % 2 == 0 ? amplitude : -amplitude)));

    public static IEnumerable<double> Flat(int count, double value) => Enumerable.Repeat(value, count);

    public static IEnumerable<double> Slope(int count, double from, double step) =>
        Enumerable.Range(1, count).Select(i => from + i * step);

    public static ConfluenceInput Input(ImmutableArray<IndicatorBar> bars,
        ImmutableArray<IndicatorBar>? benchmark = null, ImmutableArray<OrderBookSnapshot>? book = null,
        double? relativeVolume = null, decimal? previousClose = null) =>
        new(Symbol, SessionStart, bars, benchmark ?? [], book ?? [], relativeVolume, previousClose);

    public static ImmutableArray<IndicatorBar> Shifted(ImmutableArray<IndicatorBar> bars, TimeSpan offset) =>
        bars.Select(x => x with { Start = x.Start + offset, End = x.End + offset }).ToImmutableArray();

    public static TechniqueSignal Signal(string name, double score, double confidence, bool warmup = false) =>
        warmup ? TechniqueSignal.WarmingUp(name) : TechniqueSignal.Create(name, score, confidence);

    public static double? Evidence(TechniqueSignal signal, string key) =>
        signal.Evidence.TryGetValue(key, out var value) ? value : null;
}
