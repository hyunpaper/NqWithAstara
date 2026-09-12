using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>5m/15m 집계: 정규장 개장 기준 경계, 구성 1분봉이 전부 있는 완료 구간만 반환한다 (C2, #166).</summary>
public static class SessionTimeframe
{
    public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FifteenMinutes = TimeSpan.FromMinutes(15);

    public static ImmutableArray<IndicatorBar> Aggregate(IReadOnlyList<IndicatorBar> oneMinute,
        DateTimeOffset sessionOpen, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(oneMinute);
        if (span <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(span));

        var minute = TimeSpan.FromMinutes(1);
        if (span.Ticks % minute.Ticks != 0) throw new ArgumentOutOfRangeException(nameof(span));
        var slots = (int)(span.Ticks / minute.Ticks);

        var buckets = new SortedDictionary<DateTimeOffset, IndicatorBar?[]>();
        foreach (var bar in oneMinute)
        {
            if (bar.Start < sessionOpen) continue;
            if (bar.End - bar.Start != minute) continue;
            var index = (bar.Start - sessionOpen).Ticks / span.Ticks;
            var bucketStart = sessionOpen + TimeSpan.FromTicks(index * span.Ticks);
            var slot = (int)((bar.Start - bucketStart).Ticks / minute.Ticks);
            if (slot < 0 || slot >= slots) continue;
            if (!buckets.TryGetValue(bucketStart, out var members)) buckets[bucketStart] = members = new IndicatorBar?[slots];
            members[slot] ??= bar;
        }

        var result = ImmutableArray.CreateBuilder<IndicatorBar>();
        foreach (var (bucketStart, members) in buckets)
        {
            if (Array.Exists(members, x => x is null)) continue;
            var ordered = members!.Cast<IndicatorBar>().ToArray();
            result.Add(new IndicatorBar(bucketStart, bucketStart + span,
                ordered[0].Open, ordered.Max(x => x.High), ordered.Min(x => x.Low), ordered[^1].Close,
                ordered.Sum(x => x.Volume)));
        }
        return result.ToImmutable();
    }
}
