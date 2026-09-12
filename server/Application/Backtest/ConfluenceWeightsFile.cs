using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

/// <summary>가중치 파일의 기법 한 줄 (C5, #169).</summary>
public sealed record ConfluenceWeightEntry(double W, int N, double HitRate, double CiLow, double CiHigh,
    double Brier, double PValue, string Status);

/// <summary>
/// `App_Data/confluence-weights.json` 문서 (C5, #169). 서버는 기동 시 이 파일만 읽고 K2 합산에 주입한다.
/// 파일이 없거나 깨졌으면 <see cref="Default"/>(전부 1.0)가 쓰인다.
/// </summary>
public sealed record ConfluenceWeightsDocument(string WeightsVersion, DateTimeOffset? MeasuredAt,
    string? WindowFrom, string? WindowTo, int HorizonBars,
    ImmutableSortedDictionary<string, ConfluenceWeightEntry> Weights)
{
    public const string FileName = "confluence-weights.json";

    /// <summary>파일이 없을 때의 문서. 가중치는 전부 1.0이며 버전은 K2 기본값 그대로다.</summary>
    public static readonly ConfluenceWeightsDocument Default = new(ConfluenceAggregator.DefaultWeightsVersion,
        null, null, null, 0, ImmutableSortedDictionary<string, ConfluenceWeightEntry>.Empty);

    public bool IsDefault => ReferenceEquals(this, Default) || Weights.Count == 0;

    public ConfluenceWeights ToWeights() =>
        new(WeightsVersion, Weights.ToImmutableDictionary(x => x.Key, x => x.Value.W, StringComparer.Ordinal));

    public static string PathFor(string contentRoot) =>
        Path.Combine(contentRoot, "App_Data", FileName);

    /// <summary>측정 결과 → 문서. weightsVersion은 측정일과 결과 canonical JSON의 SHA-256 앞 8자다.</summary>
    public static ConfluenceWeightsDocument FromMeasurement(ConfluenceMeasurementReport report,
        DateTimeOffset measuredAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        var weights = report.Techniques.ToImmutableSortedDictionary(x => x.Technique,
            x => new ConfluenceWeightEntry(x.Weight, x.N, x.HitRate, x.Ci.Low, x.Ci.High, x.Brier, x.PValue,
                x.StatusText), StringComparer.Ordinal);
        var from = report.From.ToString("yyyy-MM-dd");
        var to = report.To.ToString("yyyy-MM-dd");
        var hash = StructureMath.Sha256Hex(Canonical(from, to, report.HorizonBars, weights))[..8];
        return new ConfluenceWeightsDocument($"w-{measuredAt:yyyyMMdd}-{hash}", measuredAt, from, to,
            report.HorizonBars, weights);
    }

    public string ToJson()
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["weightsVersion"] = WeightsVersion,
            ["measuredAt"] = MeasuredAt,
            ["window"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["from"] = WindowFrom,
                ["to"] = WindowTo
            },
            ["horizonBars"] = HorizonBars,
            ["weights"] = Weights.ToDictionary(x => x.Key, x => (object?)new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                ["w"] = x.Value.W,
                ["n"] = x.Value.N,
                ["hitRate"] = x.Value.HitRate,
                ["ci"] = new[] { Finite(x.Value.CiLow), Finite(x.Value.CiHigh) },
                ["brier"] = Finite(x.Value.Brier),
                ["pValue"] = Finite(x.Value.PValue),
                ["status"] = x.Value.Status
            }, StringComparer.Ordinal)
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>파일 파싱. 필수 필드가 없거나 가중치가 유한한 음이 아닌 수가 아니면 실패한다.</summary>
    public static bool TryRead(string? json, out ConfluenceWeightsDocument document)
    {
        document = Default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("weightsVersion", out var version) ||
                version.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(version.GetString())) return false;
            if (!root.TryGetProperty("weights", out var weights) || weights.ValueKind != JsonValueKind.Object)
                return false;

            var builder = ImmutableSortedDictionary.CreateBuilder<string, ConfluenceWeightEntry>(
                StringComparer.Ordinal);
            foreach (var entry in weights.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) return false;
                if (!entry.Value.TryGetProperty("w", out var w) || w.ValueKind != JsonValueKind.Number) return false;
                var value = w.GetDouble();
                if (!double.IsFinite(value) || value < 0) return false;
                builder[entry.Name] = new ConfluenceWeightEntry(value, Int(entry.Value, "n"),
                    Number(entry.Value, "hitRate"), Ci(entry.Value, 0), Ci(entry.Value, 1),
                    Number(entry.Value, "brier"), Number(entry.Value, "pValue"), Text(entry.Value, "status"));
            }

            var window = root.TryGetProperty("window", out var w2) && w2.ValueKind == JsonValueKind.Object ? w2
                : default;
            document = new ConfluenceWeightsDocument(version.GetString()!,
                root.TryGetProperty("measuredAt", out var at) && at.ValueKind == JsonValueKind.String &&
                at.TryGetDateTimeOffset(out var parsed) ? parsed : null,
                window.ValueKind == JsonValueKind.Object ? Text(window, "from") : null,
                window.ValueKind == JsonValueKind.Object ? Text(window, "to") : null,
                Int(root, "horizonBars"), builder.ToImmutable());
            return true;
        }
        catch (JsonException)
        {
            document = Default;
            return false;
        }
    }

    /// <summary>NaN·무한대는 JSON으로 쓸 수 없으므로 null로 남긴다(표본 없는 기법의 Brier).</summary>
    public static object? Finite(double value) => double.IsFinite(value) ? value : null;

    static string Canonical(string from, string to, int horizon,
        ImmutableSortedDictionary<string, ConfluenceWeightEntry> weights)
    {
        var builder = new StringBuilder(from).Append('|').Append(to).Append('|')
            .Append(horizon.ToString(CultureInfo.InvariantCulture));
        foreach (var (name, entry) in weights)
            builder.Append('|').Append(name).Append(':')
                .Append(entry.W.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.N.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.HitRate.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.Status);
        return builder.ToString();
    }

    static double Ci(JsonElement element, int index) =>
        element.TryGetProperty("ci", out var ci) && ci.ValueKind == JsonValueKind.Array &&
        ci.GetArrayLength() > index && ci[index].ValueKind == JsonValueKind.Number
            ? ci[index].GetDouble()
            : double.NaN;

    static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.NaN;

    static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>가중치 파일 읽기·쓰기 (C5, #169). 손상·결측은 예외 없이 기본 문서로 떨어진다.</summary>
public static class ConfluenceWeightsStore
{
    public static ConfluenceWeightsDocument Load(string contentRoot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(contentRoot);
        error = null;
        var path = ConfluenceWeightsDocument.PathFor(contentRoot);
        string json;
        try
        {
            if (!File.Exists(path)) return ConfluenceWeightsDocument.Default;
            json = File.ReadAllText(path);
        }
        catch (IOException exception)
        {
            error = exception.Message;
            return ConfluenceWeightsDocument.Default;
        }
        catch (UnauthorizedAccessException exception)
        {
            error = exception.Message;
            return ConfluenceWeightsDocument.Default;
        }

        if (ConfluenceWeightsDocument.TryRead(json, out var document)) return document;
        error = $"{ConfluenceWeightsDocument.FileName} 형식이 올바르지 않아 가중치를 전부 1.0으로 둔다.";
        return ConfluenceWeightsDocument.Default;
    }

    public static string Save(string contentRoot, ConfluenceWeightsDocument document)
    {
        ArgumentNullException.ThrowIfNull(contentRoot);
        ArgumentNullException.ThrowIfNull(document);
        var path = ConfluenceWeightsDocument.PathFor(contentRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, document.ToJson());
        return path;
    }
}
