using System.Text.Json;
using Astra.Server.Application.Rates;
using Astra.Server.Infrastructure.Rates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RatesApiTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 비활성이면_rates는_disabled_스냅샷을_돌려주고_health에도_블록이_있다()
    {
        using var host = new Host(enabled: false);
        using var client = host.Factory.CreateClient();

        using var rates = JsonDocument.Parse(await client.GetStringAsync("/api/rates"));
        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health"));

        Assert.False(rates.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", rates.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, rates.RootElement.GetProperty("tenors").GetArrayLength());
        var block = health.RootElement.GetProperty("rates");
        Assert.False(block.GetProperty("enabled").GetBoolean());
        Assert.Equal("disabled", block.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 활성_상태의_DTO_모양과_health_블록을_확인한다()
    {
        using var host = new Host(enabled: true);
        var state = host.Factory.Services.GetRequiredService<RatesRuntimeState>();
        state.MarkSupport(TreasuryTenor.Y2, false);
        state.MarkSupport(TreasuryTenor.Y10, true);
        state.MarkSupport(TreasuryTenor.Y30, true);
        state.IntradaySucceeded(TreasuryTenor.Y10, new IntradayRateQuote(TreasuryTenor.Y10, "^TNX", 5.237, 5.293, Now.AddSeconds(-30), "yahoo:^TNX"), Now);
        state.IntradayFailed(TreasuryTenor.Y30, "HTTP 429", Now);
        state.DailySucceeded(new DailyRateSeries(TreasuryTenor.Y2, "DGS2", [new(new DateOnly(2026, 9, 30), 4.89), new(new DateOnly(2026, 10, 1), 4.88)], Now, "fred:DGS2"), Now);
        state.Appended(2, Now);
        using var client = host.Factory.CreateClient();

        using var rates = JsonDocument.Parse(await client.GetStringAsync("/api/rates"));
        using var health = JsonDocument.Parse(await client.GetStringAsync("/api/health"));

        var root = rates.RootElement;
        Assert.Equal(["enabled", "status", "asOf", "intradaySource", "dailySource", "refreshSeconds", "tenors", "spreads", "directionChecks", "warnings", "limitations"],
            root.EnumerateObject().Select(x => x.Name));
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("partial", root.GetProperty("status").GetString());
        var tenors = root.GetProperty("tenors").EnumerateArray().ToArray();
        Assert.Equal(["tenor", "label", "value", "changeBp", "previousClose", "asOf", "fetchedAt", "source", "mode", "delayStatus", "sessionStatus", "delaySeconds", "daily", "reason"],
            tenors[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal("2Y", tenors[0].GetProperty("tenor").GetString());
        Assert.Equal("daily_only", tenors[0].GetProperty("mode").GetString());
        Assert.Equal(4.88, tenors[0].GetProperty("value").GetDouble());
        Assert.Equal(-1.0, tenors[0].GetProperty("changeBp").GetDouble());
        Assert.Equal("2026-10-01", tenors[0].GetProperty("daily").GetProperty("date").GetString());
        Assert.Equal("10Y", tenors[1].GetProperty("tenor").GetString());
        Assert.Equal("intraday", tenors[1].GetProperty("mode").GetString());
        Assert.Equal("open", tenors[1].GetProperty("sessionStatus").GetString());
        Assert.Equal("closed", tenors[0].GetProperty("sessionStatus").GetString());
        Assert.Equal(-5.6, tenors[1].GetProperty("changeBp").GetDouble());
        Assert.Equal("30Y", tenors[2].GetProperty("tenor").GetString());
        Assert.Equal("unavailable", tenors[2].GetProperty("delayStatus").GetString());
        Assert.Contains("HTTP 429", tenors[2].GetProperty("reason").GetString());
        var spreads = root.GetProperty("spreads").EnumerateArray().ToArray();
        Assert.Equal(["key", "label", "valueBp", "changeBp", "mode", "reason"], spreads[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal("2s10s", spreads[0].GetProperty("key").GetString());
        Assert.Equal("mixed", spreads[0].GetProperty("mode").GetString());
        Assert.Equal("10s30s", spreads[1].GetProperty("key").GetString());
        Assert.Equal("unavailable", spreads[1].GetProperty("mode").GetString());
        var checks = root.GetProperty("directionChecks").EnumerateArray().ToArray();
        Assert.Equal(["tenor", "intradayDirection", "dailyBaselineDirection", "changeBp", "changeVsDailyBp", "baselineGapBp", "agreement", "reason"],
            checks[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("limitations").ValueKind);

        var block = health.RootElement.GetProperty("rates");
        Assert.Equal(["enabled", "status", "lastRunAt", "lastSuccessAt", "lastError", "lastDailyRefreshAt", "appendedToday", "failedSources", "lastPrunedAt", "prunedFiles"],
            block.EnumerateObject().Select(x => x.Name));
        Assert.Equal("partial", block.GetProperty("status").GetString());
        Assert.Equal(2, block.GetProperty("appendedToday").GetInt32());
        Assert.Equal("intraday:30Y: HTTP 429", block.GetProperty("failedSources")[0].GetString());
    }

    sealed class Host : IDisposable
    {
        readonly string _root = Directory.CreateTempSubdirectory("astra-rates-api-").FullName;
        public WebApplicationFactory<Program> Factory { get; }

        public Host(bool enabled)
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(_root);
                builder.UseSetting("Rates:Enabled", enabled ? "true" : "false");
                builder.UseSetting("Rates:YahooChartUrl", "http://127.0.0.1:9/chart/");
                builder.UseSetting("Rates:FredCsvUrl", "http://127.0.0.1:9/fredgraph.csv");
                builder.ConfigureServices(services =>
                    services.Remove(services.Single(x => x.ImplementationType == typeof(RatesCollectorService))));
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
