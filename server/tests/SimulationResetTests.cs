using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Astra.Server.Tests;

public sealed class SimulationResetTests : IDisposable
{
    readonly string _contentRoot = Directory.CreateTempSubdirectory("astra-sim-reset-").FullName;
    string AppData => Path.Combine(_contentRoot, "App_Data");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    LocalStore NewStore() => new(new FakeEnv(_contentRoot));
    SimulationResetService NewService(DateTimeOffset? now = null) =>
        new(NewStore(), new FixedClock(now ?? new DateTimeOffset(2026, 9, 17, 21, 34, 56, TimeSpan.Zero)));

    static SimTrade Trade(string id, string status) =>
        new(id, "AAPL", "SETUP", new DateTimeOffset(2026, 9, 17, 14, 0, 0, TimeSpan.Zero), 100, 102, 99, null, null,
            status, status == "OPEN" ? null : 102, status == "OPEN" ? null : new DateTimeOffset(2026, 9, 17, 15, 0, 0, TimeSpan.Zero),
            status == "OPEN" ? null : 2.0, 101);

    async Task WriteTrades(params SimTrade[] trades)
    {
        Directory.CreateDirectory(AppData);
        await File.WriteAllTextAsync(Path.Combine(AppData, "simtrades.json"), JsonSerializer.Serialize(trades.ToList(), Json));
    }

    List<SimTrade> ReadTrades(string file = "simtrades.json") =>
        JsonSerializer.Deserialize<List<SimTrade>>(File.ReadAllText(Path.Combine(AppData, file)), Json)!;

    [Fact]
    public async Task ResetRemovesClosedTradesAndKeepsOpenByDefault()
    {
        await WriteTrades(Trade("a", "TARGET"), Trade("b", "OPEN"), Trade("c", "STOP"), Trade("d", "EOD"));

        var result = await NewService().ResetAsync(includeOpen: false);

        Assert.Equal(3, result.Removed);
        Assert.Equal(1, result.Kept);
        Assert.Equal(["b"], ReadTrades().Select(x => x.Id));
    }

    [Fact]
    public async Task ResetWithIncludeOpenRemovesEverything()
    {
        await WriteTrades(Trade("a", "TARGET"), Trade("b", "OPEN"));

        var result = await NewService().ResetAsync(includeOpen: true);

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.Kept);
        Assert.Empty(ReadTrades());
    }

    [Fact]
    public async Task ResetWritesBackupNamedByClockWithOriginalContent()
    {
        await WriteTrades(Trade("a", "TARGET"), Trade("b", "OPEN"));

        var result = await NewService(new DateTimeOffset(2026, 9, 17, 21, 34, 56, TimeSpan.Zero)).ResetAsync(includeOpen: false);

        Assert.Matches("^simtrades\\.backup-20260917-213456-[0-9a-f]{8}\\.json$", result.Backup!);
        Assert.True(File.Exists(Path.Combine(AppData, result.Backup!)));
        Assert.Equal(["a", "b"], ReadTrades(result.Backup!).Select(x => x.Id));
    }

    [Fact]
    public async Task ResetUsesAnotherBackupNameWhenTimestampCollides()
    {
        await WriteTrades(Trade("a", "TARGET"));
        Directory.CreateDirectory(AppData);
        await File.WriteAllTextAsync(Path.Combine(AppData, "simtrades.backup-20260917-213456-deadbeef.json"), "기존 백업");

        var result = await NewService(new DateTimeOffset(2026, 9, 17, 21, 34, 56, TimeSpan.Zero)).ResetAsync(includeOpen: false);

        Assert.NotEqual("simtrades.backup-20260917-213456-deadbeef.json", result.Backup);
        Assert.Equal("기존 백업", await File.ReadAllTextAsync(Path.Combine(AppData, "simtrades.backup-20260917-213456-deadbeef.json")));
    }

    [Fact]
    public async Task ResetOnEmptyHistoryRemovesNothingAndWritesNoBackup()
    {
        await WriteTrades();

        var result = await NewService().ResetAsync(includeOpen: false);

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Kept);
        Assert.Null(result.Backup);
        Assert.Empty(Directory.GetFiles(AppData, "simtrades.backup-*.json"));
    }

    [Fact]
    public async Task ResetWithOnlyOpenTradesKeepsThemAndWritesNoBackup()
    {
        await WriteTrades(Trade("b", "OPEN"));

        var result = await NewService().ResetAsync(includeOpen: false);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Kept);
        Assert.Null(result.Backup);
        Assert.Empty(Directory.GetFiles(AppData, "simtrades.backup-*.json"));
        Assert.Equal(["b"], ReadTrades().Select(x => x.Id));
    }

    [Fact]
    public async Task ResetLeavesOtherAppDataFilesUntouched()
    {
        await WriteTrades(Trade("a", "TARGET"));
        Directory.CreateDirectory(Path.Combine(AppData, "structure"));
        var watchlist = Path.Combine(AppData, "watchlist.json");
        var observation = Path.Combine(AppData, "structure", "obs-2026-09-17.jsonl");
        await File.WriteAllTextAsync(watchlist, "[{\"symbol\":\"AAPL\"}]");
        await File.WriteAllTextAsync(observation, "{\"a\":1}");

        await NewService().ResetAsync(includeOpen: true);

        Assert.Equal("[{\"symbol\":\"AAPL\"}]", await File.ReadAllTextAsync(watchlist));
        Assert.Equal("{\"a\":1}", await File.ReadAllTextAsync(observation));
    }

    [Fact]
    public async Task UpdateWithBackupKeepsOriginalWhenBackupWriteFails()
    {
        await WriteTrades(Trade("a", "TARGET"));
        Directory.CreateDirectory(Path.Combine(AppData, "blocked.json"));

        await Assert.ThrowsAnyAsync<IOException>(() => NewStore().UpdateWithBackup("simtrades.json", "blocked.json",
            new List<SimTrade>(), _ => (new List<SimTrade>(), 0)));

        Assert.Equal(["a"], ReadTrades().Select(x => x.Id));
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Astra.Server.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
    }
}
