using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [Fact]
    public async Task EmptyHistoricalDataReturnsNoDataAndUnavailableValues()
    {
        var isolatedRoot = Directory.CreateTempSubdirectory("astra-replay-no-data-").FullName;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(isolatedRoot);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHistoricalBarSource>();
                services.AddSingleton<IHistoricalBarSource, EmptyHistoricalSource>();
            });
        });
        try
        {
            var store = factory.Services.GetRequiredService<ILocalStore>();
            await store.Write("watchlist.json", new List<WatchItem> { new("TSLA", "Tesla") });
            await store.Write("simtrades.json", new[] { "운영 거래" });
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/replays", new { from = "2026-01-01", to = "2026-03-31" });
            using var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = started.RootElement.GetProperty("id").GetString()!;
            JsonElement result = default;
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(20);
                using var read = JsonDocument.Parse(await client.GetStringAsync($"/api/replays/{id}"));
                result = read.RootElement.Clone();
                if (result.GetProperty("status").GetString() == "no-data") break;
            }

            Assert.Equal("no-data", result.GetProperty("status").GetString());
            Assert.Equal("no-data", result.GetProperty("dataStatus").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("aggregate").ValueKind);
            var symbol = Assert.Single(result.GetProperty("symbols").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, symbol.GetProperty("signals").ValueKind);
            Assert.Equal("unavailable", symbol.GetProperty("tradeReplayStatus").GetString());
            Assert.Equal(2, result.GetProperty("sourceQuality").GetArrayLength());
            Assert.Equal(new[] { "운영 거래" }, await store.Read("simtrades.json", Array.Empty<string>()));
        }
        finally { try { Directory.Delete(isolatedRoot, true); } catch { } }
    }

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
        Assert.Equal("partial", result.GetProperty("dataStatus").GetString());
        Assert.Contains("실제 체결 성과", result.GetProperty("notice").GetString());
        Assert.Equal("TSLA", Assert.Single(result.GetProperty("watchlist").EnumerateArray()).GetString());
        Assert.Equal("QQQ", result.GetProperty("benchmark").GetString());
        var symbol = Assert.Single(result.GetProperty("symbols").EnumerateArray());
        Assert.Equal("partial", symbol.GetProperty("tradeReplayStatus").GetString());
        Assert.Equal(JsonValueKind.Number, symbol.GetProperty("virtualEntries").ValueKind);
        Assert.Equal(JsonValueKind.Number, symbol.GetProperty("wins").ValueKind);
        Assert.Equal(JsonValueKind.Number, symbol.GetProperty("losses").ValueKind);
        Assert.Equal(JsonValueKind.Number, symbol.GetProperty("pnlPercent").ValueKind);
        Assert.Equal(JsonValueKind.Object, symbol.GetProperty("exits").ValueKind);
        Assert.Equal(JsonValueKind.Number, symbol.GetProperty("grossPnlPercent").ValueKind);
        Assert.Equal(JsonValueKind.Number, symbol.GetProperty("feePercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, symbol.GetProperty("slippagePercent").ValueKind);
        var source = result.GetProperty("sourceQuality").EnumerateArray().First(x =>
            x.GetProperty("symbol").GetString() == "TSLA");
        Assert.Equal(20, source.GetProperty("requiredDailySeed").GetInt32());
        Assert.Equal(1, source.GetProperty("availableDailySeed").GetInt32());
        Assert.Equal("session-reset", source.GetProperty("carryPolicy").GetString());
        Assert.Equal(JsonValueKind.String, source.GetProperty("newestBar").ValueKind);
        var coverage = Assert.Single(result.GetProperty("replayCoverage").EnumerateArray());
        Assert.Equal(1, coverage.GetProperty("sourceBarMinutes").GetDouble());
        Assert.Equal("supported", coverage.GetProperty("granularityStatus").GetString());
        Assert.Equal(JsonValueKind.Object, result.GetProperty("strategyGateSummary").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("timeframeNotice").ValueKind);
        Assert.Equal("historical.ohlcv-spread-borrow-model.v2", result.GetProperty("costProfile").GetString());
        Assert.Equal("modeled-v2", result.GetProperty("selectedCostPolicy").GetString());
        Assert.Equal(2, result.GetProperty("costResults").GetArrayLength());
        Assert.Equal("insufficient-training-sample",
            result.GetProperty("selectionDiagnostics").GetProperty("status").GetString());
        using var diagnosticsResponse = await client.GetAsync($"/api/replays/{id}/diagnostics");
        Assert.Equal(HttpStatusCode.OK, diagnosticsResponse.StatusCode);
        using var diagnosticsDocument = JsonDocument.Parse(await diagnosticsResponse.Content.ReadAsStringAsync());
        var diagnostics = diagnosticsDocument.RootElement;
        Assert.Equal("replay-diagnostics.1", diagnostics.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.String, diagnostics.GetProperty("resultFingerprint").ValueKind);
        Assert.Equal(JsonValueKind.Object, diagnostics.GetProperty("summary").ValueKind);
        Assert.Contains("slippage", diagnostics.GetProperty("notice").GetString(), StringComparison.Ordinal);
        using var trades = JsonDocument.Parse(await client.GetStringAsync($"/api/replays/{id}/trades"));
        Assert.Equal(JsonValueKind.Array, trades.RootElement.ValueKind);
        Assert.True(File.Exists(Path.Combine(_root, "App_Data", "replays", id, "trades.jsonl")));
        using var latest = JsonDocument.Parse(await client.GetStringAsync("/api/replays/latest"));
        Assert.Equal(id, latest.RootElement.GetProperty("id").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "App_Data", "replay-runs.json")));
        Assert.False(File.Exists(Path.Combine(_root, "App_Data", "simtrades.json")));

        var cancel = await client.PostAsync($"/api/replays/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var unchanged = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.Equal("completed", unchanged.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostRejectsAnInvalidRangeWithoutCreatingAJob()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/replays",
            new { from = "2026-09-09", to = "2026-09-08" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancelStopsRunningReplayDeletesOnlyItsDirectoryAndIsIdempotent()
    {
        var blocking = new BlockingHistoricalSource();
        var isolatedRoot = Directory.CreateTempSubdirectory("astra-replay-cancel-").FullName;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(isolatedRoot);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHistoricalBarSource>();
                services.AddSingleton<IHistoricalBarSource>(blocking);
            });
        });
        try
        {
            var store = factory.Services.GetRequiredService<ILocalStore>();
            await store.Write("watchlist.json", new List<WatchItem> { new("TSLA", "Tesla") });
            await store.Write("simtrades.json", new[] { "운영 거래" });
            await store.Write("confluence-weights.json", new { marker = "운영 가중치" });
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/replays",
                new { from = "2026-09-08", to = "2026-09-08" });
            using var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = started.RootElement.GetProperty("id").GetString()!;
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replayDirectory = Path.Combine(isolatedRoot, "App_Data", "replays", id);
            Directory.CreateDirectory(replayDirectory);
            await File.WriteAllTextAsync(Path.Combine(replayDirectory, "partial.json"), "중간 결과");

            var cancel = await client.PostAsync($"/api/replays/{id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            using var cancelBody = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
            Assert.Equal("canceling", cancelBody.RootElement.GetProperty("status").GetString());

            JsonElement result = default;
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(20);
                using var read = JsonDocument.Parse(await client.GetStringAsync($"/api/replays/{id}"));
                result = read.RootElement.Clone();
                if (result.GetProperty("status").GetString() == "canceled") break;
            }
            Assert.Equal("canceled", result.GetProperty("status").GetString());
            Assert.False(Directory.Exists(replayDirectory));
            Assert.Equal(new[] { "운영 거래" }, await store.Read("simtrades.json", Array.Empty<string>()));
            Assert.Equal("운영 가중치", (await store.Read("confluence-weights.json", new WeightMarker(""))).Marker);

            var repeated = await client.PostAsync($"/api/replays/{id}/cancel", null);
            using var repeatedBody = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
            Assert.Equal("canceled", repeatedBody.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            try { Directory.Delete(isolatedRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task CancelReturnsNotFoundForUnknownReplay()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/replays/unknown/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TradeEndpointReturnsNotFoundForUnknownReplay()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/replays/unknown/trades");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DiagnosticsTreatsMissingAndNullStoredFeeAsUncollected()
    {
        const string id = "raw-costs";
        var store = _factory.Services.GetRequiredService<ILocalStore>();
        await store.Write("replay-runs.json", new List<HistoricalReplayRun> { Run(id) });
        var directory = Path.Combine(_root, "App_Data", "replays", id);
        Directory.CreateDirectory(directory);
        var missing = RawTrade("missing");
        missing.Remove("FeePercent");
        var nullValue = RawTrade("null");
        nullValue["FeePercent"] = null;
        await File.WriteAllLinesAsync(Path.Combine(directory, "trades.jsonl"), [missing.ToJsonString(), nullValue.ToJsonString()]);
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/api/replays/{id}/diagnostics");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = body.RootElement.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("costCollectedTrades").GetInt32());
        Assert.Equal(2, summary.GetProperty("costUncollectedTrades").GetInt32());
        Assert.Equal(2, summary.GetProperty("unverifiableTrades").GetInt32());
    }

    [Fact]
    public async Task CancelMarksPersistedQueuedReplayAsCanceled()
    {
        var store = _factory.Services.GetRequiredService<ILocalStore>();
        var queued = new HistoricalReplayRun("queued-run", new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
            ImmutableArray.Create("TSLA"), "QQQ", "mock", "policy", "weights", "queued",
            DateTimeOffset.Parse("2026-09-13T00:00:00Z"), null, null, [], null, [], "historical-virtual", "가상 결과");
        await store.Write("replay-runs.json", new List<HistoricalReplayRun> { queued });
        var directory = Path.Combine(_root, "App_Data", "replays", queued.Id);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "partial.json"), "중간 결과");
        using var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/replays/{queued.Id}/cancel", null);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("canceled", body.RootElement.GetProperty("status").GetString());
        Assert.False(Directory.Exists(directory));
    }

    sealed class FakeHistoricalSource : IHistoricalBarSource
    {
        public string Name => "mock";
        public bool Adjusted => false;
        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct)
        {
            var start = DateTimeOffset.Parse("2026-09-08T13:30:00Z");
            IReadOnlyList<Candle> bars = Enumerable.Range(0, 90).Select(i =>
                new Candle(start.AddMinutes(i), 100 + i, 101 + i, 99 + i, 100.5 + i, 1000)).ToArray();
            return Task.FromResult(new HistoricalBarReadResult(bars, bars.Count, true, bars.Min(x => x.Timestamp), null));
        }
    }

    sealed class BlockingHistoricalSource : IHistoricalBarSource
    {
        public string Name => "blocking";
        public bool Adjusted => false;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HistoricalBarReadResult([], 0, false, null, "빈 응답");
        }
    }

    sealed class EmptyHistoricalSource : IHistoricalBarSource
    {
        public string Name => "mock";
        public bool Adjusted => false;
        public Task<HistoricalBarReadResult> ReadAsync(string symbol, DateTimeOffset from, DateTimeOffset to,
            CancellationToken ct) => Task.FromResult(new HistoricalBarReadResult([], 0, false, null, "빈 응답"));
    }

    sealed record WeightMarker(string Marker);

    HistoricalReplayRun Run(string id) => new(id, new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
        ImmutableArray.Create("TSLA"), "QQQ", "mock", "policy", "weights", "completed",
        DateTimeOffset.Parse("2026-09-13T00:00:00Z"), DateTimeOffset.Parse("2026-09-13T00:01:00Z"), null,
        [], null, [], "historical-virtual", "가상 결과");

    static JsonObject RawTrade(string id) => JsonNode.Parse(JsonSerializer.Serialize(new HistoricalReplayTradeResult(
        new SimTrade(id, "TSLA", "BREAKOUT", DateTimeOffset.Parse("2026-09-08T13:30:00Z"), 100, 110, 95,
            null, null, "EOD", 99.8, DateTimeOffset.Parse("2026-09-08T13:35:00Z"), -.4, 100),
        -.2, .2, null, -.4)))!.AsObject();
}
