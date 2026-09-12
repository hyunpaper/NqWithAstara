using System.Collections.Immutable;

namespace Astra.Server.Domain.Indicators;

/// <summary>한 세션의 경과 분별 누적 거래량 곡선 (C3-2, #170). 인덱스 k는 개장 후 k+1분까지의 누적이다.</summary>
public sealed record SessionVolumeProfile(DateOnly Date, ImmutableArray<decimal> Cumulative)
{
    /// <summary>개장 후 <paramref name="elapsedMinutes"/>분까지의 누적 거래량. 곡선이 더 짧으면 마지막 값이다.</summary>
    public decimal? At(int elapsedMinutes)
    {
        if (Cumulative.IsDefaultOrEmpty || elapsedMinutes < 1) return null;
        var index = Math.Min(elapsedMinutes, Cumulative.Length) - 1;
        return Cumulative[index];
    }

    /// <summary>완료 1분봉에서 곡선을 만든다. 빠진 분은 직전 누적을 이어 쓴다.</summary>
    public static SessionVolumeProfile FromBars(DateOnly date, DateTimeOffset sessionOpen,
        IReadOnlyList<IndicatorBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        var ordered = bars.Where(x => x.End > sessionOpen).OrderBy(x => x.End).ToArray();
        if (ordered.Length == 0) return new SessionVolumeProfile(date, ImmutableArray<decimal>.Empty);

        var length = Elapsed(ordered[^1].End, sessionOpen);
        if (length < 1) return new SessionVolumeProfile(date, ImmutableArray<decimal>.Empty);

        var cumulative = new decimal[length];
        decimal running = 0;
        var next = 0;
        foreach (var bar in ordered)
        {
            var index = Elapsed(bar.End, sessionOpen) - 1;
            if (index < 0 || index >= length) continue;
            while (next < index) cumulative[next++] = running;
            running += bar.Volume;
            cumulative[index] = running;
            next = index + 1;
        }
        while (next < length) cumulative[next++] = running;
        return new SessionVolumeProfile(date, [..cumulative]);
    }

    static int Elapsed(DateTimeOffset barEnd, DateTimeOffset sessionOpen) =>
        (int)Math.Round((barEnd - sessionOpen).TotalMinutes, MidpointRounding.AwayFromZero);
}

/// <summary>일 단위 상대거래량 (C3-2, #170): 당일 누적 / 과거 세션들의 같은 시각 누적 평균.</summary>
public static class DailyRelativeVolume
{
    public const int DefaultLookbackSessions = 20;

    /// <summary>표본 세션이 모자라거나 평균이 0이면 null이며 호출부는 warmup으로 남긴다.</summary>
    public static double? Compute(decimal todayCumulative, int elapsedMinutes,
        IReadOnlyList<SessionVolumeProfile> previous, int lookbackSessions = DefaultLookbackSessions)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (lookbackSessions < 1 || elapsedMinutes < 1 || todayCumulative < 0) return null;

        var samples = previous
            .TakeLast(lookbackSessions)
            .Select(x => x.At(elapsedMinutes))
            .Where(x => x is { } value && value > 0)
            .Select(x => (double)x!.Value)
            .ToArray();
        if (samples.Length < lookbackSessions) return null;

        var average = samples.Average();
        return average > 0 ? (double)todayCumulative / average : null;
    }
}
