using System.Collections.Concurrent;
using System.Collections.Immutable;
using Astra.Server.Application.Backtest;
using Astra.Server.Domain;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Opening;

namespace Astra.Server.Application.Opening;

/// <summary>한 심볼의 Toss 과거 분봉 개장 프로파일 적재 결과(#375). 실패 수와 마지막 갱신 시각을 health로 노출한다.</summary>
public sealed record OpeningTossLoad(IReadOnlyList<SessionVolumeProfile> Profiles, int Sessions, int Failures,
    int LegacyShifted, int Rejected, int Incomplete, DateTimeOffset LoadedAt);

/// <summary>
/// 서버 자기 토큰의 Toss 과거 1분봉 경로(<see cref="IHistoricalBarSource"/>, #332 정규화)로 최근 N세션 09:30~10:00
/// 프로파일을 심볼별로 하루 1회 적재한다(#375). 세션 단위 실패를 격리하고, 토큰은 새로 발급하지 않는다.
/// </summary>
public sealed class OpeningTossProfileSource(IHistoricalBarSource source, TimeProvider clock, IMonitorDiagnostics diagnostics)
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public const int MaxCalendarDaysToScan = 45;

    readonly ConcurrentDictionary<string, (DateOnly SessionDate, OpeningTossLoad Load)> _cache = new(StringComparer.Ordinal);

    /// <summary>심볼별 하루 1회 적재. 같은 세션 날짜는 캐시를 재사용한다(지연 로드·하루 1회 갱신).</summary>
    public async Task<OpeningTossLoad> GetAsync(string symbol, DateOnly sessionDate, int lookbackSessions, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var key = symbol.ToUpperInvariant();
        if (_cache.TryGetValue(key, out var cached) && cached.SessionDate == sessionDate) return cached.Load;

        var load = await LoadAsync(key, sessionDate, Math.Max(1, lookbackSessions), ct);
        _cache[key] = (sessionDate, load);
        return load;
    }

    async Task<OpeningTossLoad> LoadAsync(string symbol, DateOnly sessionDate, int lookbackSessions, CancellationToken ct)
    {
        var collected = new List<SessionVolumeProfile>();
        var failures = 0; var legacy = 0; var rejected = 0; var incomplete = 0;
        var scanned = 0;
        for (var date = sessionDate.AddDays(-1); scanned < MaxCalendarDaysToScan && collected.Count < lookbackSessions; date = date.AddDays(-1))
        {
            ct.ThrowIfCancellationRequested();
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            scanned++;
            var open = OpeningVolumeProfileSource.OpenOf(date);
            try
            {
                var read = await source.ReadAsync(symbol, open, open.AddMinutes(OpeningSessionProfile.WindowMinutes), ct);
                var bars = ToIndicatorBars(read.Bars);
                if (bars.Length == 0) continue;
                var profile = OpeningSessionProfile.FromStoredSession(date, open, bars, BarTimeConvention.Current, null, out var status);
                switch (status)
                {
                    case OpeningSessionProfile.StatusRejected: rejected++; break;
                    case OpeningSessionProfile.StatusNoOpen:
                    case OpeningSessionProfile.StatusIncomplete: incomplete++; break;
                    case OpeningSessionProfile.StatusLegacyShifted: legacy++; break;
                }
                if (profile is not null) collected.Add(profile);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures++;
                diagnostics.MarketDataFailed(symbol, "opening-scan-toss", ex);
            }
        }

        collected.Reverse();
        return new OpeningTossLoad(collected, collected.Count, failures, legacy, rejected, incomplete, clock.GetUtcNow());
    }

    static ImmutableArray<IndicatorBar> ToIndicatorBars(IReadOnlyList<Candle> bars)
    {
        var builder = ImmutableArray.CreateBuilder<IndicatorBar>(bars.Count);
        foreach (var bar in bars)
            builder.Add(new IndicatorBar(bar.Timestamp, bar.Timestamp.AddMinutes(1), (decimal)bar.Open, (decimal)bar.High,
                (decimal)bar.Low, (decimal)bar.Close, (decimal)bar.Volume));
        return builder.ToImmutable();
    }

    /// <summary>세션이 바뀌면 이전 날짜 캐시를 비워 다음 세션 개장 전에 다시 적재하게 한다.</summary>
    public void ResetBefore(DateOnly sessionDate)
    {
        foreach (var (key, value) in _cache)
            if (value.SessionDate != sessionDate) _cache.TryRemove(key, out _);
    }
}
