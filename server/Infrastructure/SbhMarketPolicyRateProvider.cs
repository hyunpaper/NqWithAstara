using System.Text.Json;
using System.Net;
using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

/// <summary>SBHNews 금융 페이지에 표시된 정책금리를 출처·갱신시각과 함께 읽는다(#304).</summary>
public sealed class SbhMarketPolicyRateProvider : IMarketPolicyRateProvider
{
    const string Url = "https://www.sbhnews.com/markets";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    MarketPolicyRateSnapshot? _cached;
    DateTimeOffset? _cachedAt;
    DateTimeOffset? _retryAfter;
    string? _etag;
    DateTimeOffset? _lastModified;

    public SbhMarketPolicyRateProvider(NewsOptions options, HttpClient? http = null)
    {
        _ = options;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<MarketPolicyRateSnapshot> GetAsync(DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && _cachedAt is not null && now - _cachedAt < TimeSpan.FromMinutes(15)) return _cached;
            if (_retryAfter is not null && now < _retryAfter) return DelayedCached(now, "Retry-After 대기 중입니다.");
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.TryAddWithoutValidation("User-Agent", "AstraNews/1.0 (local personal project)");
            if (!string.IsNullOrWhiteSpace(_etag)) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            if (_lastModified is not null) request.Headers.IfModifiedSince = _lastModified;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotModified && _cached is not null)
            {
                _cachedAt = now;
                return _cached;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _retryAfter = RetryAfter(response, now);
                return DelayedCached(now, "정책금리 제공자의 Retry-After를 따릅니다.");
            }
            if (!response.IsSuccessStatusCode) return Unavailable("SBHNews 표시값", "페이지를 조회하지 못했습니다.");
            _etag = response.Headers.ETag?.Tag;
            _lastModified = response.Content.Headers.LastModified ?? response.Headers.Date;
            var payload = Extract(await response.Content.ReadAsStringAsync(ct));
            if (payload is null || payload.CheckedAt is null) return Unavailable("SBHNews 표시값", "정책금리 표시 형식 또는 갱신시각을 확인하지 못했습니다.");
            var age = now - payload.CheckedAt.Value;
            var stale = age > TimeSpan.FromHours(24) || age < TimeSpan.FromMinutes(-5);
            var source = "SBHNews 표시값 (원천 미확인)";
            var rates = (payload.Rates ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Label))
                .Select(x => new MarketPolicyRate(x.Key!, x.Label!, x.Value, x.Previous, DateOnly.TryParse(x.AsOf, out var date) ? date : null,
                    x.Note, payload.CheckedAt.Value, source, stale ? "stale" : "fresh",
                    stale ? "표시 갱신시각이 24시간을 넘었거나 미래입니다." : null)).ToArray();
            _cached = new MarketPolicyRateSnapshot(stale ? "delayed" : "available", payload.CheckedAt, source,
                stale ? "표시값은 참고용이며 시장 분위기 점수에 반영하지 않습니다." : null, rates);
            _cachedAt = now;
            _retryAfter = null;
            return _cached;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return Unavailable("SBHNews 표시값", "페이지를 조회하지 못했습니다."); }
        catch (TaskCanceledException) { return Unavailable("SBHNews 표시값", "페이지 응답 시간이 초과되었습니다."); }
        catch (JsonException) { return Unavailable("SBHNews 표시값", "정책금리 표시 형식을 해석하지 못했습니다."); }
        finally { _gate.Release(); }
    }

    MarketPolicyRateSnapshot DelayedCached(DateTimeOffset now, string reason)
        => _cached is null ? Unavailable("SBHNews 표시값", reason) : _cached with
        {
            Status = "delayed",
            Reason = reason,
            Rates = _cached.Rates.Select(x => x with { DelayStatus = "stale", Reason = reason }).ToArray()
        };

    static DateTimeOffset RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        var date = response.Headers.RetryAfter?.Date;
        return date ?? now + (delta is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(15));
    }

    static MarketPolicyRateSnapshot Unavailable(string source, string reason)
        => new("unavailable", null, source, reason, []);

    static Payload? Extract(string html)
    {
        const string marker = "\\\"policyRates\\\":";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var normalized = html[start..].Replace("\\\"", "\"", StringComparison.Ordinal);
        var objectStart = normalized.IndexOf('{');
        var objectEnd = JsonObjectEnd(normalized, objectStart);
        return objectStart < 0 || objectEnd < 0 ? null : JsonSerializer.Deserialize<Payload>(normalized[objectStart..(objectEnd + 1)], Json);
    }

    static int JsonObjectEnd(string value, int start)
    {
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var ch = value[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') quoted = false;
                continue;
            }
            if (ch == '"') quoted = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return index;
        }
        return -1;
    }

    sealed record Payload(List<Rate>? Rates, DateTimeOffset? CheckedAt);
    sealed record Rate(string? Key, string? Label, double Value, double? Previous, string? AsOf, string? Note);
}
