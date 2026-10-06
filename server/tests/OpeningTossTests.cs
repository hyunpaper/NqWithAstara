using Astra.Server;
using System.Net.Http;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Application.Opening;
using Astra.Server.Domain;
using Astra.Server.Domain.Opening;
using Xunit;

sealed class FakeHistoricalBarSource : IHistoricalBarSource
{
    public string Name => "fake";
    public bool Adjusted => true;
    public string? BarTimeConvention => Astra.Server.Domain.BarTimeConvention.Current;
    public HashSet<DateOnly> Available { get; } = new();
    public HashSet<DateOnly> Failing { get; } = new();
    public Dictionary<DateOnly, int> Minutes { get; } = new();
    public int Calls { get; private set; }

    public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        Calls++;
        var date = DateOnly.FromDateTime(from.DateTime);
        if (Failing.Contains(date)) throw new HttpRequestException("toss 과거 분봉 실패");
        if (!Available.Contains(date)) return Task.FromResult(new HistoricalBarReadResult([], 0, true, null, null));
        var count = Minutes.TryGetValue(date, out var m) ? m : 30;
        var bars = Enumerable.Range(0, count)
            .Select(i => new Candle(from.AddMinutes(i), 100, 100, 100, 100, 10)).ToList();
        return Task.FromResult(new HistoricalBarReadResult(bars, bars.Count, true, bars[0].Timestamp, null));
    }
}

public sealed class OpeningTossProfileSourceTests
{
    static readonly DateOnly SessionDate = new(2026, 10, 2);

    static DateOnly[] Weekdays(int count)
    {
        var result = new List<DateOnly>();
        for (var d = SessionDate.AddDays(-1); result.Count < count; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) result.Add(d);
        return result.ToArray();
    }

    static OpeningTossProfileSource Build(FakeHistoricalBarSource source) =>
        new(source, TimeProvider.System, new SilentDiagnostics());

    [Fact]
    public async Task NormalizesSessionsIntoProfiles()
    {
        var source = new FakeHistoricalBarSource();
        foreach (var d in Weekdays(5)) source.Available.Add(d);
        var load = await Build(source).GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal(5, load.Sessions);
        Assert.Equal(5, load.Profiles.Count);
        Assert.Equal(0, load.Failures);
        Assert.All(load.Profiles, p => Assert.Equal(10m, p.At(1)));
    }

    [Fact]
    public async Task FailureIsIsolatedPerSession()
    {
        var source = new FakeHistoricalBarSource();
        var days = Weekdays(4);
        foreach (var d in days) source.Available.Add(d);
        source.Failing.Add(days[1]);
        var load = await Build(source).GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal(3, load.Sessions);
        Assert.Equal(1, load.Failures);
    }

    [Fact]
    public async Task IncompleteSessionIsNotCounted()
    {
        var source = new FakeHistoricalBarSource();
        var days = Weekdays(3);
        foreach (var d in days) source.Available.Add(d);
        source.Minutes[days[0]] = 10;
        var load = await Build(source).GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal(2, load.Sessions);
        Assert.Equal(1, load.Incomplete);
    }

    [Fact]
    public async Task StopsWhenLookbackSatisfied()
    {
        var source = new FakeHistoricalBarSource();
        foreach (var d in Weekdays(10)) source.Available.Add(d);
        var load = await Build(source).GetAsync("NVDA", SessionDate, 3, default);

        Assert.Equal(3, load.Sessions);
    }

    [Fact]
    public async Task CachesPerDayAndReloadsAfterReset()
    {
        var source = new FakeHistoricalBarSource();
        foreach (var d in Weekdays(3)) source.Available.Add(d);
        var toss = Build(source);

        await toss.GetAsync("NVDA", SessionDate, 20, default);
        var callsAfterFirst = source.Calls;
        await toss.GetAsync("NVDA", SessionDate, 20, default);
        Assert.Equal(callsAfterFirst, source.Calls);

        toss.ResetBefore(SessionDate.AddDays(1));
        await toss.GetAsync("NVDA", SessionDate, 20, default);
        Assert.True(source.Calls > callsAfterFirst);
    }
}

public sealed class OpeningVolumeProfileSourceTossFallbackTests
{
    static readonly DateOnly SessionDate = new(2026, 10, 2);

    static string Day(int back) => SessionDate.AddDays(-back).ToString("yyyy-MM-dd");

    static IEnumerable<string> BarSession(DateOnly date, int minutes)
    {
        var open = OpeningVolumeProfileSource.OpenOf(date);
        return Enumerable.Range(0, minutes)
            .Select(i => StoredBarLine.Serialize(new Candle(open.AddMinutes(i), 100, 100, 100, 100, 10), "bar-start.1"));
    }

    static DateOnly[] Weekdays(int count)
    {
        var result = new List<DateOnly>();
        for (var d = SessionDate.AddDays(-1); result.Count < count; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) result.Add(d);
        return result.ToArray();
    }

    [Fact]
    public async Task PrefersTossWhenRicherThanBars()
    {
        var store = new OpeningMemoryBarStore();
        for (var i = 1; i <= 2; i++) store.Seed(Day(i), "NVDA", BarSession(SessionDate.AddDays(-i), 30));
        var source = new FakeHistoricalBarSource();
        foreach (var d in Weekdays(5)) source.Available.Add(d);
        var profiles = new OpeningVolumeProfileSource(store,
            new OpeningTossProfileSource(source, TimeProvider.System, new SilentDiagnostics()));

        var result = await profiles.GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal("toss", result.Stats.Source);
        Assert.Equal(5, result.Stats.Sessions);
        Assert.Equal(5, result.Stats.TossSessions);
    }

    [Fact]
    public async Task FallsBackToBarsWhenTossFails()
    {
        var store = new OpeningMemoryBarStore();
        for (var i = 1; i <= 3; i++) store.Seed(Day(i), "NVDA", BarSession(SessionDate.AddDays(-i), 30));
        var source = new FakeHistoricalBarSource();
        foreach (var d in Weekdays(3)) { source.Available.Add(d); source.Failing.Add(d); }
        var profiles = new OpeningVolumeProfileSource(store,
            new OpeningTossProfileSource(source, TimeProvider.System, new SilentDiagnostics()));

        var result = await profiles.GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal("bars", result.Stats.Source);
        Assert.Equal(3, result.Stats.Sessions);
        Assert.Equal(3, result.Stats.TossFailures);
    }
}

public sealed class OpeningProfileWarmupTests
{
    static readonly DateOnly SessionDate = new(2026, 10, 2);

    [Fact]
    public async Task WarmsEachWatchlistSymbolAndIsolatesFailures()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem("NVDA", "엔비디아"));
        store.Watch.Add(new WatchItem("AAPL", "애플"));
        var source = new FakeHistoricalBarSource();
        for (var d = SessionDate.AddDays(-1); d > SessionDate.AddDays(-8); d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) source.Available.Add(d);
        source.Failing.Add(SessionDate.AddDays(-1));
        var toss = new OpeningTossProfileSource(source, TimeProvider.System, new SilentDiagnostics());
        var profiles = new OpeningVolumeProfileSource(new OpeningMemoryBarStore(), toss);
        var warmup = new OpeningProfileWarmup(store, profiles, OpeningScanPolicy.Default, TimeProvider.System, new SilentDiagnostics());

        var warmed = await warmup.WarmAsync(SessionDate, default);

        Assert.Equal(2, warmed);
    }

    [Fact]
    public async Task DisabledPolicyWarmsNothing()
    {
        var store = new RecordingStore();
        store.Watch.Add(new WatchItem("NVDA", "엔비디아"));
        var warmup = new OpeningProfileWarmup(store, new OpeningVolumeProfileSource(new OpeningMemoryBarStore()),
            OpeningScanPolicy.Default with { Enabled = false }, TimeProvider.System, new SilentDiagnostics());

        Assert.Equal(0, await warmup.WarmAsync(SessionDate, default));
    }
}
