using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Opening;

/// <summary>시간대 정규화 RVOL 결과(#371 §1.3). 전부 유한수이며 결측은 null로 반환한다.</summary>
public sealed record OpeningRvolResult(double Ratio, double BaselineMean, double BaselineMedian, int SampleCount);

/// <summary>개장 후 같은 경과 분의 과거 누적 대비 당일 누적 비율(#371 §1.3). DailyRelativeVolume은 건드리지 않는다.</summary>
public static class OpeningRvol
{
    public static OpeningRvolResult? Compute(decimal todayCumulative, int elapsedMinutes,
        IReadOnlyList<SessionVolumeProfile> previous, int lookbackSessions = 20, int minimumSessions = 5)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (lookbackSessions < 1 || minimumSessions < 1 || todayCumulative < 0) return null;
        var k = Math.Clamp(elapsedMinutes, 1, 30);
        if (elapsedMinutes < 1 || elapsedMinutes > 30) return null;

        var samples = previous
            .TakeLast(lookbackSessions)
            .Select(x => x.At(k))
            .Where(x => x is { } value && value > 0)
            .Select(x => (double)x!.Value)
            .ToArray();
        if (samples.Length < minimumSessions) return null;

        var mean = samples.Average();
        if (!(mean > 0) || !double.IsFinite(mean)) return null;
        var ratio = (double)todayCumulative / mean;
        if (!double.IsFinite(ratio)) return null;
        return new OpeningRvolResult(ratio, mean, Median(samples), samples.Length);
    }

    static double Median(double[] values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
