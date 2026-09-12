using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>
/// 이슈 #6 / D3 미검증 #3·#4 — 실제 호스트 기동(DI 해결)과 HTTP 레벨 계약
/// (라우팅·camelCase 직렬화·상태 코드) 검증. 콘텐츠 루트를 임시 디렉터리로 돌려
/// 운영 App_Data를 읽거나 쓰지 않으며, 외부 Toss API 호출이 없는 경로만 친다.
/// </summary>
public sealed class AstraHostFixture : IDisposable
{
    public string ContentRoot { get; } = Directory.CreateTempSubdirectory("astra-host-").FullName;
    public WebApplicationFactory<Program> Factory { get; }

    public AstraHostFixture()
    {
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseContentRoot(ContentRoot));
    }

    public void Dispose()
    {
        Factory.Dispose();
        try { Directory.Delete(ContentRoot, recursive: true); } catch { /* 임시 디렉터리 정리 실패는 무시 */ }
    }
}

public sealed class HostBootTests(AstraHostFixture host) : IClassFixture<AstraHostFixture>
{
    [Fact]
    public void AllTopLevelServicesResolveFromTheRealHost()
    {
        var services = host.Factory.Services;
        // Program.cs의 DI 배선이 실제 호스트 기동에서 전부 해결되는지 확인한다.
        Assert.NotNull(services.GetRequiredService<StateQueryService>());
        Assert.NotNull(services.GetRequiredService<MonitorControlService>());
        Assert.NotNull(services.GetRequiredService<MetricsQueryService>());
        Assert.NotNull(services.GetRequiredService<LiquidityQueryService>());
        Assert.NotNull(services.GetRequiredService<SimulationReportQueryService>());
        Assert.NotNull(services.GetRequiredService<ValidationQueryService>());
        Assert.NotNull(services.GetRequiredService<CatalogQueryService>());
        Assert.NotNull(services.GetRequiredService<PositionService>());
        Assert.NotNull(services.GetRequiredService<StructureAnalysisService>());
        Assert.NotNull(services.GetRequiredService<StructureObservationWriter>());
        Assert.NotNull(services.GetRequiredService<StructureAlertPublisher>());
        Assert.NotNull(services.GetRequiredService<MonitorPollingService>());
        Assert.NotNull(services.GetRequiredService<MonitorRuntimeState>());
        Assert.NotNull(services.GetRequiredService<IMonitorSignals>());
        Assert.NotNull(services.GetRequiredService<IMonitorDiagnostics>());
        Assert.NotNull(services.GetRequiredService<IStructureObservationStore>());
        Assert.NotNull(services.GetRequiredService<ILocalStore>());
        Assert.NotNull(services.GetRequiredService<IMarketDataGateway>());
        Assert.NotNull(services.GetRequiredService<IOrderBookGateway>());
        Assert.NotNull(services.GetRequiredService<IRealtimeMarketStream>());
        Assert.NotNull(services.GetRequiredService<Astra.Server.Domain.Structure.StructurePolicy>());
        Assert.NotNull(services.GetRequiredService<StructureEngineOptions>());
        Assert.NotNull(services.GetRequiredService<StructureLiquidityFeed>());
        Assert.NotNull(services.GetRequiredService<IStructuralTradeEntries>());
        Assert.NotNull(services.GetRequiredService<FeeRateCheckService>());
        Assert.NotNull(services.GetRequiredService<IBarStore>());
        Assert.NotNull(services.GetRequiredService<BarStoreService>());
        Assert.NotNull(services.GetRequiredService<BenchmarkPollingService>());
        Assert.NotNull(services.GetRequiredService<ConfluenceOptions>());
    }

    [Fact]
    public void MonitorServiceIsRegisteredAsTheHostedService()
    {
        Assert.Contains(host.Factory.Services.GetServices<IHostedService>(),
            s => s is MonitorService);
    }

    [Fact]
    public void DefaultModeWithoutAppSettingsIsOff()
    {
        // 임시 콘텐츠 루트에는 appsettings.json이 없다 → 설정 부재는 안전한 off다(§18).
        Assert.Equal(StructureEngineMode.Off,
            host.Factory.Services.GetRequiredService<StructureEngineOptions>().Mode);
    }
}

public sealed class HttpContractTests(AstraHostFixture host) : IClassFixture<AstraHostFixture>
{
    static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    Task SeedWatchlistAsync() =>
        host.Factory.Services.GetRequiredService<ILocalStore>()
            .Write("watchlist.json", new List<WatchItem> { new("TSLA", "Tesla") });

    [Fact]
    public async Task HealthEndpointReturnsOkWithCamelCaseBody()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJson(response);
        Assert.Equal("Astra", json.RootElement.GetProperty("app").GetString());
        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
        // camelCase 직렬화 — PascalCase 키가 아니어야 한다.
        Assert.True(json.RootElement.TryGetProperty("credentialsRequired", out _));
        Assert.False(json.RootElement.TryGetProperty("CredentialsRequired", out _));
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("warnings").ValueKind);
        Assert.Equal(0, json.RootElement.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task HealthCarriesTheAdditiveBarsSection()
    {
        using var client = host.Factory.CreateClient();
        using var json = await ReadJson(await client.GetAsync("/api/health"));

        var bars = json.RootElement.GetProperty("bars");
        Assert.Equal(JsonValueKind.Number, bars.GetProperty("days").ValueKind);
        Assert.Equal(JsonValueKind.Number, bars.GetProperty("todayBars").ValueKind);
        var benchmark = bars.GetProperty("benchmark");
        Assert.Equal("QQQ", benchmark.GetProperty("symbol").GetString());
        Assert.Equal(JsonValueKind.Number, benchmark.GetProperty("todayBars").ValueKind);
    }

    [Fact]
    public async Task StructureRejectsInvalidSymbolWith400()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/structure/bad_symbol");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StructureReturns404ForSymbolNotOnWatchlist()
    {
        await SeedWatchlistAsync();
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/structure/MSFT");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StructureReturnsDisabledSnapshotWithCamelCaseContract()
    {
        await SeedWatchlistAsync();
        using var client = host.Factory.CreateClient();
        // 소문자 입력은 서버가 대문자로 정규화한다.
        var response = await client.GetAsync("/api/structure/tsla");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJson(response);
        var root = json.RootElement;
        Assert.Equal("TSLA", root.GetProperty("symbol").GetString());
        Assert.Equal("off", root.GetProperty("mode").GetString());
        Assert.Equal("disabled", root.GetProperty("status").GetString());
        Assert.Equal("v4", root.GetProperty("entryOwner").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("analysis").ValueKind);
        // §12 계약 필드가 camelCase로 전부 존재한다.
        foreach (var name in new[] { "engineVersion", "policyHash", "message", "updatedAt" })
            Assert.True(root.TryGetProperty(name, out _), $"missing property: {name}");
        Assert.False(root.TryGetProperty("Symbol", out _));
    }

    [Fact]
    public async Task StateIncludesAdditiveStructureSummary()
    {
        await SeedWatchlistAsync();
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/state");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJson(response);
        var root = json.RootElement;
        Assert.True(root.TryGetProperty("running", out _));
        Assert.True(root.TryGetProperty("watchlist", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);

        var summary = root.GetProperty("structureSummary");
        Assert.Equal("off", summary.GetProperty("mode").GetString());
        Assert.Equal("v4", summary.GetProperty("entryOwner").GetString());
        Assert.Equal("v4", summary.GetProperty("legacyScoreEngine").GetString());
        var rows = summary.GetProperty("symbols").EnumerateArray().ToArray();
        var tsla = Assert.Single(rows, row => row.GetProperty("symbol").GetString() == "TSLA");
        Assert.Equal("disabled", tsla.GetProperty("status").GetString());

        // 이슈 #26: additive `structureEvents` — off에서는 v5 이벤트가 없으므로 빈 배열이다.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("structureEvents").ValueKind);
        Assert.Equal(0, root.GetProperty("structureEvents").GetArrayLength());
    }

    [Fact]
    public async Task UnknownApiRouteReturns404WithMessage()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        using var json = await ReadJson(response);
        Assert.Equal("API endpoint not found.", json.RootElement.GetProperty("message").GetString());
    }
}

/// <summary>설정으로 shadow 모드를 켠 호스트 — 설정 문자열이 실제 기동에서 파싱·주입되는지 확인.</summary>
public sealed class ShadowModeHostTests : IDisposable
{
    readonly string _contentRoot = Directory.CreateTempSubdirectory("astra-shadow-").FullName;
    readonly WebApplicationFactory<Program> _factory;

    public ShadowModeHostTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(_contentRoot);
                builder.UseSetting("StructureEngineMode", "shadow");
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_contentRoot, recursive: true); } catch { /* 임시 디렉터리 정리 실패는 무시 */ }
    }

    [Fact]
    public async Task ShadowModeIsParsedFromConfigurationAndExposedOverHttp()
    {
        Assert.Equal(StructureEngineMode.Shadow,
            _factory.Services.GetRequiredService<StructureEngineOptions>().Mode);

        await _factory.Services.GetRequiredService<ILocalStore>()
            .Write("watchlist.json", new List<WatchItem> { new("TSLA", "Tesla") });
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/structure/TSLA");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("shadow", json.RootElement.GetProperty("mode").GetString());
        // 모니터링이 꺼져 있으므로 stopped — 조회가 계산을 유발하지 않는다(§12).
        Assert.Equal("stopped", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("v4", json.RootElement.GetProperty("entryOwner").GetString());
    }
}
