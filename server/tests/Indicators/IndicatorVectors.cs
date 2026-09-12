using System.Globalization;
using System.Text.Json;
using Astra.Server.Domain.Indicators;
using Xunit;

namespace Astra.Server.Tests.ConfluenceIndicators;

public static class IndicatorVectors
{
    public const double Tolerance = 1e-4;

    public static JsonElement Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Indicators", "vectors", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static IReadOnlyList<IndicatorBar> Bars(JsonElement root, string property = "bars")
        => root.GetProperty(property).EnumerateArray().Select(ToBar).ToList();

    public static IndicatorBar ToBar(JsonElement element) => new(
        Time(element.GetProperty("start").GetString()!),
        Time(element.GetProperty("end").GetString()!),
        element.GetProperty("open").GetDecimal(),
        element.GetProperty("high").GetDecimal(),
        element.GetProperty("low").GetDecimal(),
        element.GetProperty("close").GetDecimal(),
        element.GetProperty("volume").GetDecimal());

    public static DateTimeOffset Time(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static double?[] Expected(JsonElement root, string property)
        => root.GetProperty(property).EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.Null ? (double?)null : x.GetDouble())
            .ToArray();

    public static void AssertMatches(string label, IReadOnlyList<double?> actual, double?[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] is not { } want)
            {
                Assert.True(actual[i] is null, $"{label}[{i}] expected warmup null but was {actual[i]}");
                continue;
            }
            Assert.True(actual[i] is not null, $"{label}[{i}] expected {want} but was null");
            Assert.True(Math.Abs(actual[i]!.Value - want) <= Tolerance,
                $"{label}[{i}] expected {want} but was {actual[i]!.Value}");
        }
    }

    public static void AssertBarsEqual(string label, IReadOnlyList<IndicatorBar> actual, JsonElement expected)
    {
        var want = expected.EnumerateArray().Select(ToBar).ToList();
        Assert.Equal(want.Count, actual.Count);
        for (var i = 0; i < want.Count; i++)
            Assert.True(want[i] == actual[i], $"{label}[{i}] expected {want[i]} but was {actual[i]}");
    }
}
