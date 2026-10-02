using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Domain;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Application;

/// <summary>저장 1분봉 한 줄 `{t,o,h,l,c,v[,tc]}`의 직렬화·규약 판독(#332). 운영 저장·replay 데이터셋·마이그레이션이 공유한다.</summary>
public static class StoredBarLine
{
    public static string Serialize(Candle bar, string? convention) => convention is null
        ? JsonSerializer.Serialize(new { t = bar.Timestamp.UtcDateTime, o = bar.Open, h = bar.High, l = bar.Low, c = bar.Close, v = bar.Volume })
        : JsonSerializer.Serialize(new { t = bar.Timestamp.UtcDateTime, o = bar.Open, h = bar.High, l = bar.Low, c = bar.Close, v = bar.Volume, tc = convention });

    /// <summary>한 세션 파일의 줄들을 봉으로 바꾼다. 규약이 섞여 있으면 봉을 비우고 사유를 돌려준다.</summary>
    public static StoredBarSession ParseSession(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = ImmutableArray.CreateBuilder<IndicatorBar>();
        var conventions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var root = JsonDocument.Parse(line).RootElement;
                var start = root.GetProperty("t").GetDateTimeOffset();
                result.Add(new IndicatorBar(start, start.AddMinutes(1), Decimal(root, "o"), Decimal(root, "h"),
                    Decimal(root, "l"), Decimal(root, "c"), Decimal(root, "v")));
                conventions.Add(BarTimeConvention.Of(root));
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException
                                                  or InvalidOperationException or OverflowException)
            {
            }
        }
        if (conventions.Count > 1)
            return new(ImmutableArray<IndicatorBar>.Empty, string.Join('+', conventions),
                $"{BarTimeConvention.MixedRejection}: 한 세션에 봉 시각 규약 {string.Join(", ", conventions)}이(가) 섞여 있어 다시 수집하거나 무시해야 한다.");
        result.Sort((a, b) => a.End.CompareTo(b.End));
        return new(result.ToImmutable(), conventions.Count == 0 ? BarTimeConvention.Legacy : conventions.Single(), null);
    }

    static decimal Decimal(JsonElement root, string name) => (decimal)root.GetProperty(name).GetDouble();
}

public sealed record StoredBarSession(ImmutableArray<IndicatorBar> Bars, string Convention, string? Rejection);
