using System.Reflection;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application.Opening;
using Astra.Server.Domain;
using Astra.Server.Domain.Opening;
using Astra.Server.Domain.Structure;
using Astra.Server.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class OpeningScanInvariantTests
{
    [Fact]
    public void OpeningScanPolicyHashIsPinned()
    {
        Assert.Equal("4c891eee8dac984450177e6fa0aa900c755a28291ef479766b983d106df66884",
            OpeningScanPolicy.Default.PolicyHash);
    }

    [Fact]
    public void StructurePolicyHashIsUnchanged()
    {
        Assert.Equal("d7683e146a40f5ac329b5d9bc25fe081ff6755b58c78018ac05fbb8bf6049b9b",
            StructurePolicy.Default.PolicyHash);
    }

    [Fact]
    public void DomainOpeningNamespaceNeverReferencesDomainStructure()
    {
        var opening = typeof(OpeningScanPolicy).Assembly.GetTypes()
            .Where(t => t.Namespace == "Astra.Server.Domain.Opening").ToArray();
        Assert.NotEmpty(opening);
        foreach (var type in opening)
            foreach (var referenced in ReferencedTypes(type))
                Assert.False(referenced.Namespace?.StartsWith("Astra.Server.Domain.Structure", StringComparison.Ordinal) == true,
                    $"{type.Name} references {referenced.FullName}");
    }

    [Fact]
    public void RowSerializesThreeRvolWindowsCamelCase()
    {
        var open = DateTimeOffset.Parse("2026-10-02T13:30:00Z");
        var date = new DateOnly(2026, 10, 2);
        var bars = Enumerable.Range(0, 5).Select(i => new Candle(open.AddMinutes(i), 100, 100, 100, 100, 200)).ToArray();
        var previous = Enumerable.Range(0, 20)
            .Select(_ => new Astra.Server.Domain.Indicators.SessionVolumeProfile(date, [10, 20, 30, 40, 100m]))
            .ToArray();
        var input = new OpeningScanInput("NVDA", "엔비디아", date, open, open.AddMinutes(5), bars, [], null,
            previous, 103, open.AddMinutes(5), OpeningSnapshotEvaluator.QuoteFresh, OpeningScanPolicy.Default);
        var row = OpeningScanDtoMapper.Row(OpeningSnapshotEvaluator.Evaluate(input));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        foreach (var window in new[] { "rvol3", "rvol5", "rvol20" })
        {
            var w = doc.RootElement.GetProperty(window);
            foreach (var key in new[] { "lookbackSessions", "ratio", "baselineVolume", "sampleCount" })
                Assert.True(w.TryGetProperty(key, out _), $"{window}.{key} 누락");
        }
        Assert.Equal(3, doc.RootElement.GetProperty("rvol3").GetProperty("lookbackSessions").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("rvol20").GetProperty("lookbackSessions").GetInt32());
    }

    static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        void Add(Type? t) { if (t is null) return; if (t.IsByRef || t.IsArray || t.IsPointer) { Add(t.GetElementType()); return; } if (!seen.Add(t)) return; if (t.IsGenericType) foreach (var arg in t.GetGenericArguments()) Add(arg); }
        Add(type.BaseType);
        foreach (var i in type.GetInterfaces()) Add(i);
        foreach (var f in type.GetFields(all)) Add(f.FieldType);
        foreach (var p in type.GetProperties(all)) Add(p.PropertyType);
        foreach (var c in type.GetConstructors(all)) foreach (var pr in c.GetParameters()) Add(pr.ParameterType);
        foreach (var m in type.GetMethods(all)) { Add(m.ReturnType); foreach (var pr in m.GetParameters()) Add(pr.ParameterType); }
        return seen;
    }
}

public sealed class OpeningScanContractTests(AstraHostFixture host) : IClassFixture<AstraHostFixture>
{
    static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    static void AssertNoNaN(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) AssertNoNaN(p.Value);
                break;
            case JsonValueKind.Array:
                foreach (var i in element.EnumerateArray()) AssertNoNaN(i);
                break;
            case JsonValueKind.Number:
                Assert.True(double.IsFinite(element.GetDouble()));
                break;
        }
    }

    [Fact]
    public void OpeningScanServiceResolvesFromHost()
    {
        Assert.NotNull(host.Factory.Services.GetRequiredService<Astra.Server.Application.Opening.OpeningScanService>());
        Assert.NotNull(host.Factory.Services.GetRequiredService<Astra.Server.Application.Opening.OpeningVolumeProfileSource>());
        Assert.NotNull(host.Factory.Services.GetRequiredService<Astra.Server.Application.Opening.OpeningTossProfileSource>());
        Assert.NotNull(host.Factory.Services.GetRequiredService<Astra.Server.Application.Opening.OpeningProfileWarmup>());
        Assert.NotNull(host.Factory.Services.GetRequiredService<OpeningScanPolicy>());
    }

    [Fact]
    public async Task OpeningScanEndpointReturnsCamelCaseContract()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/opening-scan");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJson(response);
        var root = json.RootElement;
        var phase = root.GetProperty("phase").GetString();
        Assert.Contains(phase, new[] { "idle", "pending", "scanning", "summary", "disabled" });
        Assert.Equal("opening-scan.1", root.GetProperty("policyVersion").GetString());
        Assert.Equal(60, root.GetProperty("refreshSeconds").GetInt32());
        Assert.Equal(20, root.GetProperty("lookbackSessions").GetInt32());
        Assert.Equal(5, root.GetProperty("minimumSessions").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("rows").ValueKind);
        foreach (var name in new[] { "sessionDate", "windowStart", "windowEnd", "asOf", "elapsedMinutes", "summary", "warnings" })
            Assert.True(root.TryGetProperty(name, out _), $"missing property: {name}");
        Assert.False(root.TryGetProperty("Phase", out _));
        AssertNoNaN(root);
    }

    [Fact]
    public async Task HealthCarriesOpeningScanBlock()
    {
        using var client = host.Factory.CreateClient();
        using var json = await ReadJson(await client.GetAsync("/api/health"));

        var block = json.RootElement.GetProperty("openingScan");
        Assert.Equal("opening-scan.1", block.GetProperty("policyVersion").GetString());
        foreach (var name in new[] { "enabled", "status", "symbols", "profiles", "records", "lastObservedAt", "lastError" })
            Assert.True(block.TryGetProperty(name, out _), $"missing property: {name}");
        var profiles = block.GetProperty("profiles");
        foreach (var name in new[] { "loadedSymbols", "tossSymbols", "tossFailures", "lastRefresh", "bySymbol" })
            Assert.True(profiles.TryGetProperty(name, out _), $"missing profiles property: {name}");
        Assert.Equal(JsonValueKind.Array, profiles.GetProperty("bySymbol").ValueKind);
        var records = block.GetProperty("records");
        foreach (var name in new[] { "snapshots", "followup30", "close", "duplicatesSuppressed" })
            Assert.Equal(JsonValueKind.Number, records.GetProperty(name).ValueKind);
    }
}
