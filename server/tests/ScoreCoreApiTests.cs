using System.Net;
using System.Text.Json;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.ScoreCore;
using Astra.Server.Infrastructure.ScoreCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ScoreCoreApiTests
{
    static readonly DateTimeOffset AsOf = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledScoreCoreReturnsEnabledFalseAndHealthBlock()
    {
        using var host = new Host(enabled: false);
        using var client = host.Factory.CreateClient();

        using var latest = JsonDocument.Parse(await client.GetStringAsync("/api/score-core/company/NVDA"));
        using var detail = JsonDocument.Parse(await client.GetStringAsync("/api/score-core/snapshots/abcdef0123456789abcdef01"));
        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health"));

        Assert.False(latest.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Single(latest.RootElement.EnumerateObject());
        Assert.False(detail.RootElement.GetProperty("enabled").GetBoolean());
        var scoreCore = health.RootElement.GetProperty("scoreCore");
        Assert.False(scoreCore.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", scoreCore.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LatestAndDetailReturnDtoShapes()
    {
        using var host = new Host(enabled: true);
        var captured = await host.SeedAsync();
        using var client = host.Factory.CreateClient();
        host.Factory.Services.GetRequiredService<ScoreCoreRuntimeState>().RunCompleted(AsOf.AddMinutes(10),
            AsOf.AddMinutes(10),
            [new("NVDA", ImpactTargetKind.Company, captured, ScoreSnapshotAppendResult.Unchanged, null)]);

        var latestResponse = await client.GetAsync("/api/score-core/company/nvda");
        using var latest = JsonDocument.Parse(await latestResponse.Content.ReadAsStringAsync());
        using var detail = JsonDocument.Parse(
            await client.GetStringAsync($"/api/score-core/snapshots/{captured.CaptureId}"));
        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health"));

        Assert.Equal(HttpStatusCode.OK, latestResponse.StatusCode);
        var root = latest.RootElement;
        Assert.Equal(
        [
            "enabled", "captureId", "targetId", "targetKind", "asOf", "capturedAt", "lastConfirmedAt", "status", "calibrationStatus", "schemaVersion",
            "policyVersion", "classifierVersion", "evidenceCount", "uniqueEventCount", "inputCount", "includedCount",
            "unknownCount", "horizons", "coverage", "exclusionSummary", "excludedEvidence",
        ], root.EnumerateObject().Select(x => x.Name));
        Assert.Equal(captured.CaptureId, root.GetProperty("captureId").GetString());
        Assert.Equal("NVDA", root.GetProperty("targetId").GetString());
        Assert.Equal("company", root.GetProperty("targetKind").GetString());
        Assert.Equal(AsOf, root.GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(AsOf.AddMinutes(10), root.GetProperty("lastConfirmedAt").GetDateTimeOffset());
        Assert.Equal(AsOf.AddMinutes(10), detail.RootElement.GetProperty("lastConfirmedAt").GetDateTimeOffset());
        Assert.Equal("uncalibrated", root.GetProperty("calibrationStatus").GetString());
        Assert.Equal(2, root.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(1, root.GetProperty("uniqueEventCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("excludedEvidence").ValueKind);
        Assert.Equal("intraday", root.GetProperty("horizons")[0].GetProperty("horizon").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("horizons")[0].GetProperty("topContributions").ValueKind);
        Assert.Contains(root.GetProperty("coverage").EnumerateArray(), x =>
            x.GetProperty("source").GetString() == "macro_calendar" &&
            x.GetProperty("reason").GetString() == "no_point_in_time_vintage");
        Assert.Contains(root.GetProperty("exclusionSummary").EnumerateArray(), x =>
            x.GetProperty("stage").GetString() == "score" &&
            x.GetProperty("reason").GetString() == "impact_direction_unknown" &&
            x.GetProperty("count").GetInt32() == 2);
        Assert.False(root.TryGetProperty("score", out _));

        Assert.Equal(JsonValueKind.Array, detail.RootElement.GetProperty("excludedEvidence").ValueKind);
        Assert.Equal(JsonValueKind.Array,
            detail.RootElement.GetProperty("horizons")[0].GetProperty("topContributions").ValueKind);
        Assert.True(health.RootElement.GetProperty("scoreCore").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task MissingOrInvalidRequestsReturnNotFoundOrBadRequest()
    {
        using var host = new Host(enabled: true);
        using var client = host.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/score-core/company/ZZZZ")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/score-core/snapshots/abcdef0123456789abcdef01")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/score-core/snapshots/not-a-capture")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/score-core/planet/NVDA")).StatusCode);
    }

    sealed class Host : IDisposable
    {
        readonly string _root = Directory.CreateTempSubdirectory("astra-score-core-api-").FullName;
        public WebApplicationFactory<Program> Factory { get; }

        public Host(bool enabled)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(_root);
                builder.UseSetting("ScoreCore:Enabled", enabled ? "true" : "false");
            });
        }

        public async Task<ScoreCoreShadowSnapshot> SeedAsync()
        {
            var news = ScoreCoreNewsEvidenceTests.Store(
                ScoreCoreNewsEvidenceTests.Record("lead", AsOf.AddHours(-1)),
                ScoreCoreNewsEvidenceTests.Record("copy", AsOf.AddHours(-1)) with { ClassifiedFrom = "lead" });
            var options = new ScoreCoreOptions { Enabled = true };
            var assembler = new AsOfEvidenceAssembler(
            [
                new NewsScoreEvidenceSource(new NewsScoreRecordReader(news), options),
                new MacroCalendarEvidenceSource(),
            ]);
            var store = new JsonlScoreCoreSnapshotStore(Path.Combine(_root, "App_Data", "score-core"));
            return await new ScoreCoreSnapshotService(assembler, store)
                .CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));
        }

        public void Dispose()
        {
            Factory.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
