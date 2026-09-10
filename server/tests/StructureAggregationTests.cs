using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §5.1~§5.2. 세션 시작 정렬, 미완성 제외, 중간 결측, 조기 폐장, KST 자정, DST를 고정한다.</summary>
public sealed class StructureAggregationTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static NormalizedBars Normalize(IEnumerable<Candle> candles, int asOfMinute) =>
        BarAggregator.Normalize(candles, Fx.SessionStart, Fx.SessionEnd, Fx.At(asOfMinute));

    [Fact]
    public void NormalizeKeepsOnlyCompletedRegularSessionBars()
    {
        var candles = new[]
        {
            new Candle(Fx.At(-1), 100, 100.2, 99.8, 100, 500),   // 세션 전
            Fx.Candle(0, 100, 100.2, 99.8, 100),
            Fx.Candle(1, 100, 100.3, 99.9, 100.2),
            Fx.Candle(2, 100.2, 100.4, 100, 100.3)               // asOf 기준 미완성
        };
        var result = Normalize(candles, 2);
        Assert.Equal(2, result.Bars.Length);
        Assert.Equal(Fx.At(0), result.Bars[0].Start);
        Assert.Equal(Fx.At(2), result.Bars[1].End);
        Assert.Equal(1, result.DroppedIncomplete);
        Assert.Equal(1, result.DroppedOutOfSession);
    }

    [Fact]
    public void NormalizeSortsAndFoldsDuplicatesAndFlagsConflicts()
    {
        var candles = new[]
        {
            Fx.Candle(2, 100.2, 100.4, 100, 100.3),
            Fx.Candle(0, 100, 100.2, 99.8, 100),
            Fx.Candle(0, 100, 100.2, 99.8, 100),                 // 동일 값 중복
            Fx.Candle(1, 100, 100.3, 99.9, 100.2),
            Fx.Candle(1, 100, 100.9, 99.9, 100.8)                // 같은 시각 상충 값
        };
        var result = Normalize(candles, 3);
        Assert.Equal(new[] { Fx.At(0), Fx.At(1), Fx.At(2) }, result.Bars.Select(x => x.Start).ToArray());
        Assert.Equal(2, result.DroppedDuplicate);
        Assert.Single(result.Conflicts);
        Assert.Contains("BAR_CONFLICT", result.Warnings);
        Assert.Equal(100.3m, result.Bars[1].High);               // 첫 관측을 유지한다
    }

    [Fact]
    public void NormalizeRejectsInvalidOhlcv()
    {
        var candles = new[]
        {
            Fx.Candle(0, 100, 100.2, 99.8, 100),
            Fx.Candle(1, 100, double.NaN, 99.9, 100.2),
            Fx.Candle(2, 100, 100.4, 100.6, 100.3),              // Low > High
            new Candle(Fx.At(3), 100, 100.4, 100, 100.3, -5),    // 음수 거래량
            new Candle(Fx.At(4), 0, 100.4, 100, 100.3, 10)       // 가격 0
        };
        var result = Normalize(candles, 10);
        Assert.Single(result.Bars);
        Assert.Equal(4, result.DroppedInvalid);
        Assert.Contains("INVALID_BAR_VALUES", result.Warnings);
        Assert.Contains("INVALID_BAR_ORDERING", result.Warnings);
    }

    [Fact]
    public void NormalizeRecordsMissingMinutesAsGaps()
    {
        var candles = new[] { Fx.Candle(0, 100, 100.2, 99.8, 100), Fx.Candle(3, 100, 100.2, 99.8, 100) };
        var result = Normalize(candles, 10);
        Assert.Equal(2, result.Bars.Length);
        Assert.Single(result.Gaps);
        Assert.Contains("x2", result.Gaps[0]);
        Assert.Contains("BAR_GAP", result.Warnings);
    }

    [Fact]
    public void AggregationAlignsBucketsToSessionStart()
    {
        var candles = Enumerable.Range(0, 10).Select(i => Fx.Candle(i, 100 + i * .1, 100.5 + i * .1, 99.5 + i * .1, 100.2 + i * .1, 100 + i)).ToArray();
        var bars = Normalize(candles, 10).Bars;
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(10), P);

        Assert.Equal(2, five.Length);
        Assert.Equal(Fx.At(0), five[0].Start);
        Assert.Equal(Fx.At(5), five[0].End);
        Assert.Equal(Fx.At(5), five[1].Start);
        Assert.Equal(bars[0].Open, five[0].Open);
        Assert.Equal(bars.Take(5).Max(x => x.High), five[0].High);
        Assert.Equal(bars.Take(5).Min(x => x.Low), five[0].Low);
        Assert.Equal(bars[4].Close, five[0].Close);
        Assert.Equal(bars.Take(5).Sum(x => x.Volume), five[0].Volume);
    }

    [Fact]
    public void AggregationExcludesIncompleteBucketEvenWhenBarsExist()
    {
        var candles = Enumerable.Range(0, 8).Select(i => Fx.Candle(i, 100, 100.5, 99.5, 100.2)).ToArray();
        var bars = Normalize(candles, 8).Bars;
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(8), P);
        Assert.Single(five);
        Assert.Equal(Fx.At(5), five[0].End);
    }

    [Fact]
    public void AggregationSkipsBucketWithAMissingMinuteInsteadOfFillingIt()
    {
        var minutes = new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9 };      // 7분봉 결측
        var candles = minutes.Select(i => Fx.Candle(i, 100, 100.5, 99.5, 100.2)).ToArray();
        var five = BarAggregator.Aggregate(Normalize(candles, 10).Bars, Fx.SessionStart, Fx.At(10), P);
        Assert.Single(five);
        Assert.Equal(Fx.At(0), five[0].Start);
    }

    [Fact]
    public void EarlyCloseTailIsNotAggregatedIntoAPartialBucket()
    {
        var earlyClose = Fx.At(13);
        var candles = Enumerable.Range(0, 13).Select(i => Fx.Candle(i, 100, 100.5, 99.5, 100.2)).ToArray();
        var bars = BarAggregator.Normalize(candles, Fx.SessionStart, earlyClose, earlyClose).Bars;
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, earlyClose, P);
        Assert.Equal(2, five.Length);
        Assert.Equal(Fx.At(10), five[^1].End);
    }

    [Fact]
    public void AggregationIsUnaffectedByKoreanMidnightBoundary()
    {
        // 09:30 EDT = 22:30 KST. 90분 후 KST 자정을 넘는다. 거래일을 KST 날짜로 나누지 않는다(§5.1).
        var kst = TimeSpan.FromHours(9);
        var candles = Enumerable.Range(85, 10)
            .Select(i => new Candle(Fx.At(i).ToOffset(kst), 100, 100.5, 99.5, 100.2, 10))
            .ToArray();
        Assert.Contains(candles, x => x.Timestamp.ToOffset(kst).Day != Fx.SessionStart.ToOffset(kst).Day);

        var bars = BarAggregator.Normalize(candles, Fx.SessionStart, Fx.SessionEnd, Fx.At(95)).Bars;
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(95), P);
        Assert.Equal(2, five.Length);
        Assert.Equal(Fx.At(85), five[0].Start);
        Assert.Equal(Fx.At(90), five[1].Start);
    }

    [Fact]
    public void AggregationBucketsAreIdenticalAcrossTheDstBoundary()
    {
        ImmutableArray<StructureBar> Session(DateTimeOffset start)
        {
            var candles = Enumerable.Range(0, 10)
                .Select(i => new Candle(start.AddMinutes(i).ToUniversalTime(), 100, 100.5, 99.5, 100.2, 10)).ToArray();
            var bars = BarAggregator.Normalize(candles, start, start.AddHours(6.5), start.AddMinutes(10)).Bars;
            return BarAggregator.Aggregate(bars, start, start.AddMinutes(10), P);
        }

        var winter = DateTimeOffset.Parse("2026-03-06T09:30:00-05:00");   // EST
        var summer = DateTimeOffset.Parse("2026-03-09T09:30:00-04:00");   // EDT
        Assert.NotEqual(winter.Offset, summer.Offset);

        var a = Session(winter);
        var b = Session(summer);
        Assert.Equal(2, a.Length);
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a.Select(x => x.Start - winter), b.Select(x => x.Start - summer));
        Assert.Equal(a.Select(x => x.Close), b.Select(x => x.Close));
    }

    [Fact]
    public void SessionAtrUsesWilderAndIsNullBeforeSeed()
    {
        var bars = Enumerable.Range(0, 20).Select(i => Fx.Steady(i, 99.45m, 99.65m, 99.55m)).ToImmutableArray();
        var series = SessionAtr.Series(bars, P);
        for (var i = 0; i < 13; i++) Assert.Null(series[i]);
        Assert.Equal(.20, series[13]!.Value, 10);
        Assert.Equal(.20, series[19]!.Value, 10);
        Assert.Null(SessionAtr.At(bars, series, Fx.At(13)));            // 13봉까지는 seed 미달
        Assert.Equal(.20, SessionAtr.At(bars, series, Fx.At(14))!.Value, 10);
        Assert.Equal(.20, SessionAtr.At(bars, series, Fx.At(100))!.Value, 10);
    }

    [Fact]
    public void SessionAtrNeverLooksAtFutureBars()
    {
        var bars = Enumerable.Range(0, 20).Select(i => Fx.Steady(i, 99.45m, 99.65m, 99.55m)).ToList();
        var full = bars.ToImmutableArray();
        var prefix = bars.Take(15).ToImmutableArray();
        Assert.Equal(SessionAtr.At(prefix, SessionAtr.Series(prefix, P), Fx.At(15)),
            SessionAtr.At(full, SessionAtr.Series(full, P), Fx.At(15)));
    }
}
