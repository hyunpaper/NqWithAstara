using System.Collections.Concurrent;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Opening;

namespace Astra.Server.Application.Opening;

public sealed record OpeningProfileStats(int Sessions, int LegacyShifted, int Rejected, int Incomplete);

public sealed record OpeningProfiles(IReadOnlyList<SessionVolumeProfile> Profiles, OpeningProfileStats Stats);

/// <summary>운영 저장 봉(<see cref="IBarStore"/>)에서 심볼별 개장 프로파일을 적재·캐시한다(#371 §1.2). Toss 호출은 하지 않는다.</summary>
public sealed class OpeningVolumeProfileSource(IBarStore store)
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    readonly ConcurrentDictionary<string, OpeningProfiles> _cache = new(StringComparer.Ordinal);

    public static DateTimeOffset OpenOf(DateOnly date)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, 9, 30, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local));
    }

    public async Task<OpeningProfiles> GetAsync(string symbol, DateOnly sessionDate, int lookbackSessions, CancellationToken ct)
    {
        var key = $"{symbol.ToUpperInvariant()}|{sessionDate:yyyy-MM-dd}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var days = (await store.ListDaysAsync(ct))
            .Where(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", out var d) && d < sessionDate)
            .OrderByDescending(x => x, StringComparer.Ordinal)
            .ToArray();

        var collected = new List<SessionVolumeProfile>();
        var legacy = 0; var rejected = 0; var incomplete = 0;
        foreach (var day in days)
        {
            if (collected.Count >= lookbackSessions) break;
            ct.ThrowIfCancellationRequested();
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", out var date)) continue;
            var lines = await store.ReadLinesAsync(day, symbol, ct);
            if (lines.Count == 0) continue;
            var session = StoredBarLine.ParseSession(lines);
            var profile = OpeningSessionProfile.FromStoredSession(date, OpenOf(date), session.Bars, session.Convention, session.Rejection, out var status);
            switch (status)
            {
                case OpeningSessionProfile.StatusRejected: rejected++; break;
                case OpeningSessionProfile.StatusNoOpen:
                case OpeningSessionProfile.StatusIncomplete: incomplete++; break;
                case OpeningSessionProfile.StatusLegacyShifted: legacy++; break;
            }
            if (profile is not null) collected.Add(profile);
        }

        collected.Reverse();
        var result = new OpeningProfiles(collected, new OpeningProfileStats(collected.Count, legacy, rejected, incomplete));
        _cache[key] = result;
        return result;
    }

    /// <summary>세션이 바뀌면 이전 세션 키를 비운다 — 다음 세션 첫 폴링에서 다시 적재한다.</summary>
    public void ResetBefore(DateOnly sessionDate)
    {
        foreach (var key in _cache.Keys)
        {
            var parts = key.Split('|');
            if (parts.Length == 2 && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var d) && d != sessionDate)
                _cache.TryRemove(key, out _);
        }
    }
}
