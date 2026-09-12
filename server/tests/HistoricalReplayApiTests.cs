using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Astra.Server.Tests;

public sealed class HistoricalReplayApiTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("astra-replay-api-").FullName;
    readonly WebApplicationFactory<Program> _factory;

    public HistoricalReplayApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(_root);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHistoricalBarSource>();
                services.AddSingleton<IHistoricalBarSource, FakeHistoricalSource>();
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task PostSnapshotsWatchlistAndGetReturnsPersistedCompletedResult()
    {
        await _factory.Services.GetRequiredService<ILocalStore>().Write("watchlist.json",
            new List<WatchItem> { new("TSLA", "Tesla"), new("QQQ", "benchmark") });
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/replays",
            new { from = "2026-09-08", to = "2026-09-08" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = started.RootElement.GetProperty("id").GetString()!;
        JsonElement result = default;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(20);
            using var read = JsonDocument.Parse(await client.GetStringAsync($"/api/replays/{id}"));
            result = read.RootElement.Clone();
            if (result.GetProperty("status").GetString() is "completed" or "failed") break;
        }

        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal("TSLA", Assert.Single(result.GetProperty("watchlist").EnumerateArray()).GetString());
        Assert.Equal("QQQ", result.GetProperty("benchmark").GetString());
        Assert.Equal("unavailable", Assert.Single(result.GetProperty("symbols").EnumerateArray())
            .GetProperty("tradeReplayStatus").GetString());
        using var latest = JsonDocument.Parse(await client.GetStringAsync("/api/replays/latest"));
        Assert.Equal(id, latest.RootElement.GetProperty("id").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "App_Data", "replay-runs.json")));
        Assert.False(File.Exists(Path.Combine(_root, "App_Data", "simtrades.json")));
    }

    [Fact]
    public async Task PostRejectsAnInvalidRangeWithoutCreatingAJob()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/replays",
            new { from = "2026-09-09", to = "2026-09-08" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    sealed class FakeHistoricalSource : IHistoricalBarSource
    {
        public string Name => "mock";
        public bool Adjusted => false;
        public Task<IReadOnlyList<Candle>> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct)
        {
            var start = DateTimeOffset.Parse("2026-09-08T13:30:00Z");
            IReadOnlyList<Candle> bars = Enumerable.Range(0, 90).Select(i =>
                new Candle(start.AddMinutes(i), 100 + i, 101 + i, 99 + i, 100.5 + i, 1000)).ToArray();
            return Task.FromResult(bars);
        }
    }
}
