using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Application.Opening;
using Astra.Server.Domain.Opening;
using Xunit;

sealed class OpeningMemoryBarStore : IBarStore
{
    readonly Dictionary<(string Day, string Symbol), List<string>> _files = new();

    public void Seed(string day, string symbol, IEnumerable<string> lines) =>
        _files[(day, symbol.ToUpperInvariant())] = lines.ToList();

    List<string> Lines(string day, string symbol) => _files.TryGetValue((day, symbol.ToUpperInvariant()), out var l) ? l : [];
    public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(Lines(day, symbol) is { Count: > 0 } l ? l[^1] : null);
    public Task AppendAsync(string day, string symbol, string line, CancellationToken ct) { var key = (day, symbol.ToUpperInvariant()); if (!_files.TryGetValue(key, out var l)) _files[key] = l = []; l.Add(line); return Task.CompletedTask; }
    public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(_files.Keys.Select(x => x.Day).Distinct().ToArray());
    public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(_files.Keys.Where(x => x.Day == day).Select(x => x.Symbol).ToArray());
    public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(Lines(day, symbol).Count);
    public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Lines(day, symbol).ToArray());
    public Task DeleteDayAsync(string day, CancellationToken ct) { foreach (var k in _files.Keys.Where(x => x.Day == day).ToArray()) _files.Remove(k); return Task.CompletedTask; }
}

public sealed class OpeningVolumeProfileSourceTests
{
    static readonly DateOnly SessionDate = new(2026, 10, 2);

    static string Line(DateTimeOffset start, string? convention, decimal volume) =>
        StoredBarLine.Serialize(new Candle(start, 100, 100, 100, 100, (double)volume), convention);

    static IEnumerable<string> CurrentSession(DateOnly date, int minutes)
    {
        var open = OpeningVolumeProfileSource.OpenOf(date);
        return Enumerable.Range(0, minutes).Select(i => Line(open.AddMinutes(i), "bar-start.1", 10));
    }

    static IEnumerable<string> LegacySession(DateOnly date, int minutes)
    {
        var open = OpeningVolumeProfileSource.OpenOf(date);
        return Enumerable.Range(0, minutes).Select(i => Line(open.AddMinutes(i + 1), null, 10));
    }

    static string Day(int back) => SessionDate.AddDays(-back).ToString("yyyy-MM-dd");

    [Fact]
    public async Task LookbackCapsCollectedSessionsAndExcludesToday()
    {
        var store = new OpeningMemoryBarStore();
        store.Seed(SessionDate.ToString("yyyy-MM-dd"), "NVDA", CurrentSession(SessionDate, 30));
        for (var i = 1; i <= 25; i++) store.Seed(Day(i), "NVDA", CurrentSession(SessionDate.AddDays(-i), 30));
        var source = new OpeningVolumeProfileSource(store);

        var result = await source.GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal(20, result.Profiles.Count);
        Assert.Equal(20, result.Stats.Sessions);
    }

    [Fact]
    public async Task StatsCountLegacyRejectedAndIncomplete()
    {
        var store = new OpeningMemoryBarStore();
        for (var i = 1; i <= 5; i++) store.Seed(Day(i), "NVDA", CurrentSession(SessionDate.AddDays(-i), 30));
        store.Seed(Day(6), "NVDA", LegacySession(SessionDate.AddDays(-6), 30));
        store.Seed(Day(7), "NVDA", LegacySession(SessionDate.AddDays(-7), 30));
        store.Seed(Day(8), "NVDA", CurrentSession(SessionDate.AddDays(-8), 30).Concat(LegacySession(SessionDate.AddDays(-8), 30)));
        store.Seed(Day(9), "NVDA", CurrentSession(SessionDate.AddDays(-9), 10));
        var source = new OpeningVolumeProfileSource(store);

        var result = await source.GetAsync("NVDA", SessionDate, 20, default);

        Assert.Equal(7, result.Stats.Sessions);
        Assert.Equal(2, result.Stats.LegacyShifted);
        Assert.Equal(1, result.Stats.Rejected);
        Assert.Equal(1, result.Stats.Incomplete);
    }

    [Fact]
    public async Task ResetReloadsAfterNewPriorDayAppears()
    {
        var store = new OpeningMemoryBarStore();
        for (var i = 1; i <= 3; i++) store.Seed(Day(i), "NVDA", CurrentSession(SessionDate.AddDays(-i), 30));
        var source = new OpeningVolumeProfileSource(store);

        Assert.Equal(3, (await source.GetAsync("NVDA", SessionDate, 20, default)).Profiles.Count);
        store.Seed(Day(4), "NVDA", CurrentSession(SessionDate.AddDays(-4), 30));
        Assert.Equal(3, (await source.GetAsync("NVDA", SessionDate, 20, default)).Profiles.Count);

        source.ResetBefore(SessionDate.AddDays(10));
        Assert.Equal(4, (await source.GetAsync("NVDA", SessionDate, 20, default)).Profiles.Count);
    }
}

public sealed class OpeningScanObservationWriterTests
{
    [Fact]
    public async Task DuplicateObservationIdIsSuppressed()
    {
        var store = new MemoryObservationStore();
        var writer = new OpeningScanObservationWriter(store);
        var date = new DateOnly(2026, 10, 2);

        Assert.True(await writer.AppendAsync(date, "NVDA:2026-10-02:5", "snapshot", new { observationId = "NVDA:2026-10-02:5" }, default));
        Assert.False(await writer.AppendAsync(date, "NVDA:2026-10-02:5", "snapshot", new { observationId = "NVDA:2026-10-02:5" }, default));

        Assert.Equal(1, writer.Counts.Snapshots);
        Assert.Equal(1, writer.Counts.DuplicatesSuppressed);
    }
}

public sealed class OpeningScanServiceTests
{
    static readonly DateTimeOffset Open = DateTimeOffset.Parse("2026-09-09T13:30:00Z");
    static readonly MarketSession Market = new(true, "open", null, Open, Open.AddHours(6.5));

    static Candle[] Rising(int count) => Enumerable.Range(0, count)
        .Select(i => new Candle(Open.AddMinutes(i), 100 + i * 0.1, 101 + i * 0.1, 99 + i * 0.1, 100.5 + i * 0.1, 1000)).ToArray();

    static (OpeningScanService Svc, OpeningScanObservationWriter Writer, MemoryObservationStore Obs) Build(TimeProvider clock)
    {
        var obs = new MemoryObservationStore();
        var writer = new OpeningScanObservationWriter(obs);
        var svc = new OpeningScanService(OpeningScanPolicy.Default, new OpeningVolumeProfileSource(new OpeningMemoryBarStore()),
            writer, clock, new SilentDiagnostics());
        return (svc, writer, obs);
    }

    static WatchItem Nvda => new("NVDA", "엔비디아");

    [Fact]
    public void NoSessionReportsIdle()
    {
        var (svc, _, _) = Build(new MovableClock(Open));
        Assert.Equal("idle", svc.Query().Phase);
    }

    [Fact]
    public async Task ScanningThenSummaryAcrossWindow()
    {
        var clock = new MovableClock(Open.AddMinutes(5));
        var (svc, _, _) = Build(clock);

        await svc.ObserveAsync(Nvda, [], Rising(5), (100.9, Open.AddMinutes(5)), true, Market, null, default);
        Assert.Equal("scanning", svc.Query().Phase);
        Assert.Single(svc.Query().Rows);

        clock.Now = Open.AddMinutes(40);
        var summary = svc.Query();
        Assert.Equal("summary", summary.Phase);
        Assert.Empty(summary.Rows);
        Assert.Single(summary.Summary!.At5);
    }

    [Fact]
    public async Task RepeatedPollSameMinuteWritesOneSnapshot()
    {
        var clock = new MovableClock(Open.AddMinutes(5));
        var (svc, writer, _) = Build(clock);

        await svc.ObserveAsync(Nvda, [], Rising(5), (100.9, Open.AddMinutes(5)), true, Market, null, default);
        await svc.ObserveAsync(Nvda, [], Rising(5), (100.9, Open.AddMinutes(5)), true, Market, null, default);

        Assert.Equal(1, writer.Counts.Snapshots);
    }

    [Fact]
    public async Task Followup30WrittenOnceWithAnchorFromMinuteFive()
    {
        var clock = new MovableClock(Open.AddMinutes(35));
        var (svc, writer, obs) = Build(clock);

        await svc.ObserveAsync(Nvda, [], Rising(35), (104, Open.AddMinutes(35)), true, Market, null, default);
        await svc.ObserveAsync(Nvda, [], Rising(35), (104, Open.AddMinutes(35)), true, Market, null, default);

        Assert.Equal(1, writer.Counts.Followup30);
        var line = obs.AllLines.Single(x => x.Contains("\"followup30\""));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(100.9, doc.RootElement.GetProperty("anchorBarClose").GetDouble(), 3);
        Assert.True(doc.RootElement.GetProperty("returnPercent30").GetDouble() > 0);
        Assert.True(doc.RootElement.GetProperty("mfePercent30").GetDouble() >= doc.RootElement.GetProperty("maePercent30").GetDouble());
    }

    [Fact]
    public async Task FinalizeWritesCloseWithAnchorMissingWhenNoFiveMinuteBar()
    {
        var clock = new MovableClock(Open.AddMinutes(3));
        var (svc, writer, obs) = Build(clock);

        await svc.ObserveAsync(Nvda, [], Rising(3), (100.3, Open.AddMinutes(3)), true, Market, null, default);
        await svc.FinalizeSessionAsync(Market, default);

        Assert.Equal(1, writer.Counts.Close);
        Assert.Contains("ANCHOR_MISSING", obs.AllLines.Single(x => x.Contains("\"close\"")));
    }

    [Fact]
    public async Task FinalizeComputesCloseReturnWhenAnchorPresent()
    {
        var clock = new MovableClock(Open.AddMinutes(6));
        var (svc, _, obs) = Build(clock);

        await svc.ObserveAsync(Nvda, [], Rising(6), (110, Open.AddMinutes(6)), true, Market, null, default);
        await svc.FinalizeSessionAsync(Market, default);

        var line = obs.AllLines.Single(x => x.Contains("\"close\""));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("returnPercentClose").ValueKind);
    }

    [Fact]
    public async Task RestartRestoresSummaryFromFile()
    {
        var clock = new MovableClock(Open.AddMinutes(6));
        var (svc1, _, obs) = Build(clock);
        await svc1.ObserveAsync(Nvda, [], Rising(6), (110, Open.AddMinutes(6)), true, Market, null, default);
        await svc1.FinalizeSessionAsync(Market, default);

        var writer2 = new OpeningScanObservationWriter(obs);
        var svc2 = new OpeningScanService(OpeningScanPolicy.Default,
            new OpeningVolumeProfileSource(new OpeningMemoryBarStore()), writer2, clock, new SilentDiagnostics());
        await svc2.ObserveAsync(Nvda, [], Rising(6), (110, Open.AddMinutes(6)), true, Market, null, default);

        clock.Now = Open.AddMinutes(40);
        var summary = svc2.Query();
        Assert.Equal("summary", summary.Phase);
        Assert.Single(summary.Summary!.At5);
        Assert.Single(summary.Summary.Close);
    }

    [Fact]
    public void ServiceTakesNoMarketDataGateway()
    {
        var parameters = typeof(OpeningScanService).GetConstructors().Single().GetParameters();
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IMarketDataGateway));
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IOrderBookGateway));
    }
}
