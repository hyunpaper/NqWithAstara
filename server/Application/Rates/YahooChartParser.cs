using System.Text.Json;

namespace Astra.Server.Application.Rates;

public sealed record YahooChartQuote(double Value, double? PreviousClose, DateTimeOffset AsOf);

/// <summary>Yahoo Finance 차트 응답(비공식 API) 파서. meta.regularMarketPrice를 우선하고 없으면 마지막 유효 close를 쓴다(#316, #325).</summary>
public static class YahooChartParser
{
    public static IntradayRateQuote Parse(string json, TreasuryTenor tenor, string symbol, string source)
    {
        var quote = ParseQuote(json);
        return new IntradayRateQuote(tenor, symbol, quote.Value, quote.PreviousClose, quote.AsOf, source);
    }

    public static EtfProxyQuote ParseEtf(string json, EtfProxyDefinition proxy, string source)
    {
        var quote = ParseQuote(json);
        return new EtfProxyQuote(proxy.Tenor, proxy.Symbol, quote.Value, quote.PreviousClose, quote.AsOf, source);
    }

    public static YahooChartQuote ParseQuote(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("chart", out var chart))
            throw new FormatException("chart 노드가 없습니다.");
        if (chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var description = error.TryGetProperty("description", out var text) ? text.GetString() : null;
            throw new InvalidDataException(description ?? "출처가 오류를 돌려줬습니다.");
        }
        if (!chart.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
            throw new FormatException("result 배열이 비어 있습니다.");
        var result = results[0];
        if (!result.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            throw new FormatException("meta 노드가 없습니다.");

        var value = Number(meta, "regularMarketPrice");
        var asOf = Seconds(meta, "regularMarketTime");
        if (value is null)
        {
            var last = LastClose(result);
            value = last?.Value;
            asOf ??= last?.AsOf;
        }
        if (value is null || !double.IsFinite(value.Value) || value <= 0)
            throw new FormatException("유효한 값이 없습니다.");
        var previous = Number(meta, "chartPreviousClose") ?? Number(meta, "previousClose");
        if (previous is not null && (!double.IsFinite(previous.Value) || previous <= 0)) previous = null;
        return new YahooChartQuote(Math.Round(value.Value, 4), previous,
            asOf ?? throw new FormatException("시각 정보가 없습니다."));
    }

    static (double Value, DateTimeOffset AsOf)? LastClose(JsonElement result)
    {
        if (!result.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
            return null;
        if (!result.TryGetProperty("indicators", out var indicators) ||
            !indicators.TryGetProperty("quote", out var quotes) || quotes.ValueKind != JsonValueKind.Array ||
            quotes.GetArrayLength() == 0 || !quotes[0].TryGetProperty("close", out var closes) ||
            closes.ValueKind != JsonValueKind.Array)
            return null;
        var count = Math.Min(timestamps.GetArrayLength(), closes.GetArrayLength());
        for (var i = count - 1; i >= 0; i--)
        {
            var close = closes[i];
            if (close.ValueKind != JsonValueKind.Number || !close.TryGetDouble(out var value) || !double.IsFinite(value))
                continue;
            var stamp = timestamps[i];
            if (stamp.ValueKind != JsonValueKind.Number || !stamp.TryGetInt64(out var seconds)) continue;
            return (value, DateTimeOffset.FromUnixTimeSeconds(seconds));
        }
        return null;
    }

    static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out var number) ? number : null;

    static DateTimeOffset? Seconds(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var seconds) && seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
