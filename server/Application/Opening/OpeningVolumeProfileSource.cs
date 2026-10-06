using System.Collections.Concurrent;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Opening;

namespace Astra.Server.Application.Opening;

public sealed record OpeningProfileStats(int Sessions, int LegacyShifted, int Rejected, int Incomplete,
    int TossSessions = 0, int TossFailures = 0, string Source = "bars", DateTimeOffset? LoadedAt = null);

public sealed record OpeningProfiles(IReadOnlyList<SessionVolumeProfile> Profiles, OpeningProfileStats Stats);

/// <summary>
/// 심볼별 개장 프로파일을 적재·캐시한다(#371 §1.2, #375). Toss 과거 분봉(<see cref="OpeningTossProfileSource"/>)을 1순위로,
/// 저장 봉(<see cref="IBarStore"/>)을 폴백으로 쓰며 더 많은 세션을 확보한 쪽을 고른다. 세션당 1회 적재한다.
/// </summary>
public sealed class OpeningVolumeProfileSource(IBarStore store, OpeningTossProfileSource? toss = null)
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

        OpeningTossLoad? tossLoad = null;
        if (toss is not null)
        {
            try { tossLoad = await toss.GetAsync(symbol, sessionDate, lookbackSessions, ct); }
            catch (OperationCanceledException) { throw; }
            catch { tossLoad = null; }
        }

        var bars = await LoadFromBarsAsync(symbol, sessionDate, lookbackSessions, ct);

        OpeningProfiles result;
        if (tossLoad is { } t && t.Sessions > 0 && t.Sessions >= bars.Profiles.Count)
            result = new OpeningProfiles(t.Profiles, new OpeningProfileStats(t.Sessions, t.LegacyShifted, t.Rejected,
                t.Incomplete, t.Sessions, t.Failures, "toss", t.LoadedAt));
        else
            result = new OpeningProfiles(bars.Profiles, bars.Stats with
            {
                TossSessions = tossLoad?.Sessions ?? 0,
                TossFailures = tossLoad?.Failures ?? 0,
                Source = "bars",
                LoadedAt = tossLoad?.LoadedAt,
            });

        _cache[key] = result;
        return result;
    }

    async Task<OpeningProfiles> LoadFromBarsAsync(string symbol, DateOnly sessionDate, int lookbackSessions, CancellationToken ct)
    {
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
        return new OpeningProfiles(collected, new OpeningProfileStats(collected.Count, legacy, rejected, incomplete));
    }

    /// <summary>세션이 바뀌면 이전 세션 키를 비운다 — 다음 세션 첫 폴링에서 다시 적재한다.</summary>
    public void ResetBefore(DateOnly sessionDate)
    {
        toss?.ResetBefore(sessionDate);
        foreach (var key in _cache.Keys)
        {
            var parts = key.Split('|');
            if (parts.Length == 2 && DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", out var d) && d != sessionDate)
                _cache.TryRemove(key, out _);
        }
    }
}
