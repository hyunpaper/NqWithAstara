using System.Collections.Concurrent;
using System.Collections.Immutable;
using Astra.Server.Application.Backtest;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Application;

/// <summary>RVOL_DAILY(#170 C3-2)가 읽는 과거 세션의 누적 거래량 곡선. 당일은 절대 들어가지 않는다(C6).</summary>
public interface IIntradayVolumeProfileSource
{
    ImmutableArray<SessionVolumeProfile> Profiles(string symbol, DateOnly sessionDate);
}

/// <summary>
/// K0 저장 봉(`App_Data/bars`)에서 과거 세션 곡선을 만들어 캐시한다 (C3-2, #170).
/// 폴링 경로를 막지 않으려고 첫 요청에서 적재를 시작하고 끝날 때까지는 빈 목록을 돌려준다(기법은 warmup).
/// </summary>
public sealed class BarStoreVolumeProfiles(IBarStore store, int lookbackSessions = 20)
    : IIntradayVolumeProfileSource
{
    readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImmutableArray<SessionVolumeProfile> Profiles(string symbol, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var entry = _cache.GetOrAdd(symbol, _ => new Entry(sessionDate));
        if (entry.Session != sessionDate)
        {
            entry = new Entry(sessionDate);
            _cache[symbol] = entry;
        }
        if (entry.Started.TrySetResult(true)) _ = LoadAsync(symbol, sessionDate, entry);
        return entry.Profiles;
    }

    async Task LoadAsync(string symbol, DateOnly sessionDate, Entry entry)
    {
        try
        {
            var days = (await store.ListDaysAsync(CancellationToken.None))
                .Select(x => DateOnly.TryParse(x, out var parsed) ? parsed : (DateOnly?)null)
                .Where(x => x is { } value && value < sessionDate)
                .Select(x => x!.Value)
                .OrderBy(x => x)
                .TakeLast(lookbackSessions)
                .ToArray();

            var profiles = ImmutableArray.CreateBuilder<SessionVolumeProfile>(days.Length);
            foreach (var day in days)
            {
                var bars = ConfluenceReplay.Parse(
                    await store.ReadLinesAsync(day.ToString("yyyy-MM-dd"), symbol, CancellationToken.None));
                if (bars.Length == 0) continue;
                profiles.Add(SessionVolumeProfile.FromBars(day, bars[0].Start, bars));
            }
            entry.Profiles = profiles.ToImmutable();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or FormatException or InvalidOperationException)
        {
            entry.Profiles = ImmutableArray<SessionVolumeProfile>.Empty;
        }
    }

    sealed class Entry(DateOnly session)
    {
        public DateOnly Session { get; } = session;
        public TaskCompletionSource<bool> Started { get; } = new();
        ImmutableArray<SessionVolumeProfile> _profiles = ImmutableArray<SessionVolumeProfile>.Empty;

        public ImmutableArray<SessionVolumeProfile> Profiles
        {
            get => ImmutableInterlocked.InterlockedCompareExchange(ref _profiles, default, default);
            set => ImmutableInterlocked.InterlockedExchange(ref _profiles, value);
        }
    }
}
