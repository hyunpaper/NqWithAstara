using System.Collections.Immutable;
using Astra.Server;

namespace Astra.Server.Domain.Structure;

/// <summary>
/// 설계 §5.3 / §16B 확정 피벗. 좌우 각 2봉과 엄격 비교하며 동일 고저 plateau는 피벗으로 만들지 않는다.
/// ConfirmedAt은 우측 두 번째 봉의 종료 시각이고 ConfirmedAt&lt;=cutoff인 피벗만 노출한다.
/// </summary>
public static class PivotDetector
{
    /// <param name="cutoff">평가 cutoff. 후보 봉이 이미 지나갔어도 확인 시각이 여기보다 늦으면 노출하지 않는다.</param>
    public static ImmutableArray<ConfirmedPivot> Detect(string symbol, DateTimeOffset sessionStart, BarTimeframe timeframe,
        IReadOnlyList<StructureBar> bars, DateTimeOffset cutoff, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(policy);
        var left = policy.PivotLeft;
        var right = policy.PivotRight;
        if (left < 1 || right < 1) throw new ArgumentOutOfRangeException(nameof(policy));

        var session = MarketRules.TradingDate(sessionStart).ToString("yyyy-MM-dd");
        var frame = timeframe == BarTimeframe.OneMinute ? "1m" : "5m";
        var result = ImmutableArray.CreateBuilder<ConfirmedPivot>();

        for (var i = left; i + right < bars.Count; i++)
        {
            if (!IsContiguous(bars, i - left, i + right)) continue;      // 결측 구간을 인접 봉으로 위장하지 않는다
            var confirmedAt = bars[i + right].End;
            if (confirmedAt > cutoff) continue;                          // §14 예시 E: 확인 전에는 존재하지 않는다

            var isHigh = true;
            var isLow = true;
            for (var k = i - left; k <= i + right; k++)
            {
                if (k == i) continue;
                if (bars[k].High >= bars[i].High) isHigh = false;        // 엄격 비교: 동일 고점 plateau 제외
                if (bars[k].Low <= bars[i].Low) isLow = false;
            }

            if (isHigh) result.Add(Make(symbol, session, frame, PivotKind.High, timeframe, bars[i].High, bars[i].Start, confirmedAt));
            if (isLow) result.Add(Make(symbol, session, frame, PivotKind.Low, timeframe, bars[i].Low, bars[i].Start, confirmedAt));
        }

        return result
            .OrderBy(x => x.ConfirmedAt)
            .ThenBy(x => x.OccurredAt)
            .ThenBy(x => x.Kind)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    static ConfirmedPivot Make(string symbol, string session, string frame, PivotKind kind, BarTimeframe timeframe,
        decimal price, DateTimeOffset occurredAt, DateTimeOffset confirmedAt)
    {
        // §16B 원천 ID = canonical 문자열의 SHA-256: symbol/NY session/timeframe/high-or-low/OccurredAt
        var id = StructureMath.SourceId("pivot", symbol, session, frame, kind == PivotKind.High ? "H" : "L",
            StructureMath.Iso(occurredAt));
        return new ConfirmedPivot(id, kind, timeframe, price, occurredAt, confirmedAt);
    }

    static bool IsContiguous(IReadOnlyList<StructureBar> bars, int from, int to)
    {
        for (var k = from; k < to; k++)
            if (bars[k].End != bars[k + 1].Start) return false;
        return true;
    }
}
