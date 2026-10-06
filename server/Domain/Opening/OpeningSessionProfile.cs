using System.Collections.Immutable;
using Astra.Server.Domain;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Opening;

/// <summary>저장 세션 봉을 개장 30분 누적 곡선으로 바꾸는 순수 변환(#371 §1.2). 파일 I/O는 호출부(Application)가 한다.</summary>
public static class OpeningSessionProfile
{
    public const string StatusOk = "ok";
    public const string StatusLegacyShifted = "legacy-shifted";
    public const string StatusRejected = "rejected";
    public const string StatusNoOpen = "no-open";
    public const string StatusIncomplete = "incomplete";

    public const int WindowMinutes = 30;
    public const int MinimumMinutes = 25;

    /// <summary>개장 09:30~10:00 봉만 남겨 누적 곡선(길이 ≤ 30)을 만든다. 구 규약은 −1분 시프트, 규약 섞임·표본 부족은 null.</summary>
    public static SessionVolumeProfile? FromStoredSession(DateOnly date, DateTimeOffset open,
        ImmutableArray<IndicatorBar> bars, string convention, string? rejection, out string status)
    {
        if (rejection is not null) { status = StatusRejected; return null; }

        var legacy = string.Equals(convention, BarTimeConvention.Legacy, StringComparison.Ordinal);
        var windowEnd = open.AddMinutes(WindowMinutes);
        var shifted = new List<IndicatorBar>();
        foreach (var bar in bars)
        {
            var start = legacy ? TossBarTime.StartOf(bar.Start) : bar.Start;
            if (start < open || start >= windowEnd) continue;
            shifted.Add(bar with { Start = start, End = start.AddMinutes(1) });
        }

        if (shifted.All(x => x.Start != open)) { status = StatusNoOpen; return null; }
        if (shifted.Select(x => x.Start).Distinct().Count() < MinimumMinutes) { status = StatusIncomplete; return null; }

        status = legacy ? StatusLegacyShifted : StatusOk;
        return SessionVolumeProfile.FromBars(date, open, shifted);
    }
}
