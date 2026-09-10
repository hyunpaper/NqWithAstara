using System.Collections.Immutable;
using System.Globalization;
using Astra.Server;

namespace Astra.Server.Domain.Structure;

/// <summary>
/// 설계 §5.1~§5.2. 완료 1분봉을 정규화하고 세션 시작에 정렬된 5분봉을 로컬 집계한다.
/// 추가 API를 가정하지 않으며 결측·진행 중·세션 밖 봉을 채우거나 전일 봉과 섞지 않는다.
/// </summary>
public static class BarAggregator
{
    /// <summary>
    /// 원본 1분 Candle을 구조 엔진 봉으로 정규화한다.
    /// 정규장 [SessionStart,SessionEnd) 안, 종료&lt;=analysisAsOf인 완료 봉만 남기고 시각 순으로 정렬한다.
    /// 잘못된 OHLCV는 제거하고, 동일 시각 중복은 값이 같으면 하나로 접고 다르면 첫 관측을 유지하되 충돌을 기록한다.
    /// </summary>
    public static NormalizedBars Normalize(IEnumerable<Candle> raw, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
        DateTimeOffset analysisAsOf, TimeSpan? barDuration = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var duration = barDuration ?? TimeSpan.FromMinutes(1);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(barDuration));

        var kept = new List<StructureBar>();
        var seen = new Dictionary<DateTimeOffset, StructureBar>();
        var conflicts = new SortedSet<string>(StringComparer.Ordinal);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        int invalid = 0, duplicate = 0, incomplete = 0, outOfSession = 0;

        foreach (var candle in raw)
        {
            if (candle is null) { invalid++; continue; }
            if (!IsFinitePositive(candle)) { invalid++; warnings.Add("INVALID_BAR_VALUES"); continue; }
            if (candle.Low > candle.Open || candle.Low > candle.Close || candle.High < candle.Open || candle.High < candle.Close || candle.Low > candle.High)
            { invalid++; warnings.Add("INVALID_BAR_ORDERING"); continue; }
            if (candle.Timestamp < sessionStart || candle.Timestamp >= sessionEnd) { outOfSession++; continue; }
            var end = candle.Timestamp + duration;
            if (end > analysisAsOf) { incomplete++; continue; }
            if (end > sessionEnd) { outOfSession++; warnings.Add("BAR_CROSSES_SESSION_END"); continue; }

            var bar = new StructureBar(candle.Timestamp, end, (decimal)candle.Open, (decimal)candle.High,
                (decimal)candle.Low, (decimal)candle.Close, candle.Volume);
            if (seen.TryGetValue(bar.Start, out var existing))
            {
                duplicate++;
                if (existing != bar) conflicts.Add($"BAR_CONFLICT@{StructureMath.Iso(bar.Start)}");
                continue;
            }
            seen.Add(bar.Start, bar);
            kept.Add(bar);
        }

        var bars = kept.OrderBy(x => x.Start).ToImmutableArray();
        var gaps = new List<string>();
        for (var i = 1; i < bars.Length; i++)
        {
            var expected = bars[i - 1].Start + duration;
            if (bars[i].Start == expected) continue;
            var missing = (int)((bars[i].Start - expected).Ticks / duration.Ticks);
            gaps.Add($"BAR_GAP@{StructureMath.Iso(expected)}x{missing.ToString(CultureInfo.InvariantCulture)}");
        }
        if (gaps.Count > 0) warnings.Add("BAR_GAP");
        if (conflicts.Count > 0) warnings.Add("BAR_CONFLICT");

        return new NormalizedBars(bars, gaps.ToImmutableArray(), conflicts.ToImmutableArray(),
            warnings.ToImmutableArray(), invalid, duplicate, incomplete, outOfSession);
    }

    /// <summary>
    /// §5.2 5분봉 집계. bucket=SessionStart+floor((bar.Start-SessionStart)/5분)*5분이며
    /// 해당 bucket의 5개 정확한 시작 시각이 모두 있어야 하고 bucket 종료&lt;=AnalysisAsOf여야 완료다.
    /// </summary>
    public static ImmutableArray<StructureBar> Aggregate(IReadOnlyList<StructureBar> oneMinute, DateTimeOffset sessionStart,
        DateTimeOffset analysisAsOf, StructurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(oneMinute);
        ArgumentNullException.ThrowIfNull(policy);
        var span = policy.AggregationSpan();
        var slots = policy.AggregationMinutes;
        if (slots < 1) throw new ArgumentOutOfRangeException(nameof(policy));

        var buckets = new SortedDictionary<DateTimeOffset, StructureBar?[]>();
        foreach (var bar in oneMinute)
        {
            if (bar.Start < sessionStart) continue;
            var offset = bar.Start - sessionStart;
            var index = offset.Ticks / span.Ticks;
            var bucketStart = sessionStart + TimeSpan.FromTicks(index * span.Ticks);
            var slot = (int)((bar.Start - bucketStart).Ticks / bar.Duration.Ticks);
            if (slot < 0 || slot >= slots) continue;
            if (!buckets.TryGetValue(bucketStart, out var members)) buckets[bucketStart] = members = new StructureBar?[slots];
            // 동일 slot 중복은 정규화 단계에서 이미 제거된다. 남아 있으면 첫 관측을 유지한다.
            members[slot] ??= bar;
        }

        var result = ImmutableArray.CreateBuilder<StructureBar>();
        foreach (var (bucketStart, members) in buckets)
        {
            var end = bucketStart + span;
            if (end > analysisAsOf) continue;                     // 진행 중 bucket 제외
            if (members.Any(x => x is null)) continue;            // 결측 1분봉을 채우지 않는다
            var expectedStart = bucketStart;
            var ordered = new StructureBar[slots];
            var aligned = true;
            for (var i = 0; i < slots; i++)
            {
                var member = members[i]!;
                if (member.Start != expectedStart) { aligned = false; break; }
                ordered[i] = member;
                expectedStart += member.Duration;
            }
            if (!aligned || ordered[^1].End != end) continue;
            result.Add(new StructureBar(bucketStart, end, ordered[0].Open, ordered.Max(x => x.High),
                ordered.Min(x => x.Low), ordered[^1].Close, ordered.Sum(x => x.Volume)));
        }
        return result.ToImmutable();
    }

    static bool IsFinitePositive(Candle c) =>
        double.IsFinite(c.Open) && double.IsFinite(c.High) && double.IsFinite(c.Low) && double.IsFinite(c.Close) &&
        double.IsFinite(c.Volume) && c.Open > 0 && c.High > 0 && c.Low > 0 && c.Close > 0 && c.Volume >= 0;
}

/// <summary>
/// 설계 §16B 세션 초기화 ATR. 첫 봉 TR=High-Low, 이후 TR=max(H-L,|H-prevClose|,|L-prevClose|).
/// 최초 period개 TR 평균을 seed로 하고 이후 Wilder ((period-1)*prev+TR)/period를 쓴다. period봉 미만은 null이다.
/// D2 추세 평가기도 같은 계열을 재사용한다(ATR은 D1이 소유).
/// </summary>
public static class SessionAtr
{
    public static ImmutableArray<double?> Series(IReadOnlyList<StructureBar> bars, StructurePolicy policy)
        => Series(bars, policy.AtrPeriod);

    public static ImmutableArray<double?> Series(IReadOnlyList<StructureBar> bars, int period)
    {
        ArgumentNullException.ThrowIfNull(bars);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        var values = new double?[bars.Count];
        if (bars.Count == 0) return values.ToImmutableArray();

        var tr = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            double high = (double)bars[i].High, low = (double)bars[i].Low;
            tr[i] = i == 0
                ? high - low
                : Math.Max(high - low, Math.Max(Math.Abs(high - (double)bars[i - 1].Close), Math.Abs(low - (double)bars[i - 1].Close)));
        }

        double atr = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            if (i < period - 1) { values[i] = null; continue; }
            if (i == period - 1)
            {
                double sum = 0;
                for (var k = 0; k < period; k++) sum += tr[k];
                atr = sum / period;
            }
            else atr = ((period - 1) * atr + tr[i]) / period;
            values[i] = double.IsFinite(atr) ? atr : null;
        }
        return values.ToImmutableArray();
    }

    /// <summary>주어진 시각에 사용할 수 있는 마지막 완료 봉의 ATR. 미래 봉은 절대 보지 않는다.</summary>
    public static double? At(IReadOnlyList<StructureBar> bars, ImmutableArray<double?> series, DateTimeOffset at)
    {
        double? value = null;
        for (var i = 0; i < bars.Count && i < series.Length; i++)
        {
            if (bars[i].End > at) break;
            value = series[i];
        }
        return value;
    }
}
