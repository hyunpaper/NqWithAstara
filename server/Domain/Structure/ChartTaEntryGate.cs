using Astra.Server.Domain.Structure;

namespace Astra.Server;

/// <summary>
/// #245 차트 TA 진입 게이트. 진입 직전 완료봉까지의 종목·벤치마크 봉만 보고(룩어헤드 없음) 세 가설을 적용한다.
/// H1 REBOUND 상대강도 하한, H2 직전 60봉 고점 거리 하한(BREAKOUT 제외), H3 HH_HL 구조 거절(전 유형).
/// 지표 정의는 diagnostics/chart-ta/ta_features.py와 1:1로 맞춘다. 실시간·replay가 같은 함수를 호출한다.
/// </summary>
public sealed record ChartTaEntryGateResult(bool Allowed, string Reason);

public static class ChartTaEntryGate
{
    public const int DefaultPivotK = 3;
    public const int DefaultStructureWindowBars = 60;
    public const int RecentHighWindowBars = 60;
    public const string NotRequired = "TA_NOT_REQUIRED";
    public const string AllowedReason = "TA_ALLOWED";
    public const string RejectedRelativeStrength = "TA_REBOUND_RELATIVE_STRENGTH_BELOW_MIN";
    public const string RejectedNearRecentHigh = "TA_NEAR_RECENT_HIGH";
    public const string RejectedHigherHighHigherLow = "TA_HIGHER_HIGH_HIGHER_LOW";

    const string ReboundKind = "REBOUND";
    const string PullbackKind = "PULLBACK";

    /// <param name="confirmationEnd">확인 봉의 종료 시각. 마지막 완료봉은 그 1분 전에 시작한 봉이며 BenchmarkEntryGate와 같은 컷오프다.</param>
    public static ChartTaEntryGateResult Evaluate(StructurePolicy policy, string? kind, TradeSide side,
        IReadOnlyList<Candle>? symbolBars, IReadOnlyList<Candle>? benchmarkBars, DateTimeOffset confirmationEnd)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var h1On = policy.ReboundMinRelativeStrengthPercent is not null;
        var h2On = policy.MinDistanceToRecentHighPercent is not null;
        var h3On = policy.RejectHigherHighHigherLow == true;
        if (side != TradeSide.Long || (!h1On && !h2On && !h3On)) return new(true, NotRequired);

        var cutoff = confirmationEnd - TimeSpan.FromMinutes(1);
        var bars = symbolBars?.Where(x => x.Timestamp <= cutoff).OrderBy(x => x.Timestamp).ToArray();
        if (bars is null || bars.Length == 0) return new(true, NotRequired);

        if (h3On)
        {
            var k = policy.HigherHighPivotK ?? DefaultPivotK;
            var window = policy.HigherHighStructureWindowBars ?? DefaultStructureWindowBars;
            if (IsHigherHighHigherLow(bars, k, window)) return new(false, RejectedHigherHighHigherLow);
        }

        if (h2On && (string.Equals(kind, ReboundKind, StringComparison.Ordinal) ||
                     string.Equals(kind, PullbackKind, StringComparison.Ordinal)) &&
            DistanceToRecentHighPercent(bars, RecentHighWindowBars) is { } distance &&
            distance < policy.MinDistanceToRecentHighPercent!.Value)
            return new(false, RejectedNearRecentHigh);

        if (h1On && string.Equals(kind, ReboundKind, StringComparison.Ordinal) &&
            RelativeStrengthOpenPercent(bars, benchmarkBars, confirmationEnd) is { } rs &&
            rs < policy.ReboundMinRelativeStrengthPercent!.Value)
            return new(false, RejectedRelativeStrength);

        return new(true, AllowedReason);
    }

    /// <summary>직전 60봉(lc−59..lc) 최고가 대비 마지막 완료봉 종가 거리(%). ta_features.res60_dist_pct와 동일.</summary>
    public static double? DistanceToRecentHighPercent(IReadOnlyList<Candle> bars, int window)
    {
        if (bars.Count == 0) return null;
        var lc = bars.Count - 1;
        var from = Math.Max(0, lc - (window - 1));
        var high = double.NegativeInfinity;
        for (var i = from; i <= lc; i++)
            if (bars[i].High > high) high = bars[i].High;
        var close = bars[lc].Close;
        if (!double.IsFinite(close) || close <= 0 || !double.IsFinite(high)) return null;
        return (high - close) / close * 100.0;
    }

    /// <summary>(종목 시가대비 % − 벤치마크 시가대비 %). ta_features.rs_open과 동일. 데이터 없으면 null(fail-open).</summary>
    public static double? RelativeStrengthOpenPercent(IReadOnlyList<Candle> bars,
        IReadOnlyList<Candle>? benchmarkBars, DateTimeOffset confirmationEnd)
    {
        if (bars.Count == 0) return null;
        var open = bars[0].Open;
        var close = bars[^1].Close;
        if (!double.IsFinite(open) || open <= 0 || !double.IsFinite(close) || close <= 0) return null;
        var benchmarkReturn = BenchmarkEntryGate.SessionReturnPercent(benchmarkBars, confirmationEnd);
        if (benchmarkReturn is not { } benchmark) return null;
        var symbolReturn = (close / open - 1.0) * 100.0;
        return symbolReturn - benchmark;
    }

    /// <summary>
    /// lc−window..lc 창에서 좌우 k봉 비최대/비최소가 아닌 피벗(동일 고저 plateau 허용, ta_features.pivots와 동일)의
    /// 마지막 두 고점·두 저점이 둘 다 상승하면 HH_HL이다.
    /// </summary>
    public static bool IsHigherHighHigherLow(IReadOnlyList<Candle> bars, int k, int window)
    {
        if (k < 1 || bars.Count == 0) return false;
        var lc = bars.Count - 1;
        var lo = Math.Max(0, lc - window);
        var n = lc - lo + 1;
        if (n < 2 * k + 1) return false;

        var highs = new List<double>();
        var lows = new List<double>();
        for (var i = k; i < n - k; i++)
        {
            var center = lo + i;
            var high = bars[center].High;
            var low = bars[center].Low;
            var isHigh = true;
            var isLow = true;
            for (var j = i - k; j <= i + k; j++)
            {
                var other = bars[lo + j];
                if (other.High > high) isHigh = false;
                if (other.Low < low) isLow = false;
            }
            if (isHigh) highs.Add(high);
            if (isLow) lows.Add(low);
        }

        if (highs.Count < 2 || lows.Count < 2) return false;
        return highs[^1] > highs[^2] && lows[^1] > lows[^2];
    }
}
