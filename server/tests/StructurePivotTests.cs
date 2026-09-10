using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §5.3 / §14 예시 E / §16B. 확정 전 피벗은 존재하지 않고 plateau는 피벗이 아니다.</summary>
public sealed class StructurePivotTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static ImmutableArray<StructureBar> Series(params (int Minute, decimal High, decimal Low)[] shape) =>
        shape.Select(x => new StructureBar(Fx.At(x.Minute), Fx.At(x.Minute + 1),
            (x.High + x.Low) / 2m, x.High, x.Low, (x.High + x.Low) / 2m, 1000)).ToImmutableArray();

    static ImmutableArray<ConfirmedPivot> Detect(ImmutableArray<StructureBar> bars, int cutoffMinute) =>
        PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.OneMinute, bars, Fx.At(cutoffMinute), P);

    /// <summary>설계 예시 E: 시작 10:00 봉(=세션 시작 +30분)의 확인 시각은 10:03이며 10:01/10:02 평가에는 없다.</summary>
    [Fact]
    public void PivotIsInvisibleUntilTheSecondRightBarCloses()
    {
        var bars = Series(
            (28, 100.4m, 100.0m), (29, 100.5m, 100.1m),
            (30, 101.0m, 100.6m),                                  // 10:00 후보 고점
            (31, 100.7m, 100.3m), (32, 100.6m, 100.2m), (33, 100.5m, 100.1m));

        Assert.Empty(Detect(bars, 31));
        Assert.Empty(Detect(bars, 32));                            // 10:01/10:02 평가에는 없다
        var confirmed = Detect(bars, 33);                          // 10:03 = 우측 두 번째 봉 종료
        var pivot = Assert.Single(confirmed);
        Assert.Equal(PivotKind.High, pivot.Kind);
        Assert.Equal(101.0m, pivot.Price);
        Assert.Equal(Fx.At(30), pivot.OccurredAt);
        Assert.Equal(Fx.At(33), pivot.ConfirmedAt);
        Assert.Equal(pivot.ConfirmedAt, bars[4].End);
    }

    [Fact]
    public void FutureBarsNeverBackdateAConfirmedPivot()
    {
        var bars = Series((0, 100.4m, 100.0m), (1, 100.5m, 100.1m), (2, 101.0m, 100.6m),
            (3, 100.7m, 100.3m), (4, 100.6m, 100.2m), (5, 100.5m, 100.1m), (6, 100.4m, 100.0m));
        var atFive = Detect(bars, 5);
        var atSeven = Detect(bars, 7);
        Assert.Single(atFive);
        Assert.Equal(atFive[0].SourceId, atSeven[0].SourceId);
        Assert.Equal(atFive[0].ConfirmedAt, atSeven[0].ConfirmedAt);
    }

    [Fact]
    public void EqualHighPlateauIsNotAPivot()
    {
        var bars = Series((0, 100.4m, 100.0m), (1, 101.0m, 100.6m), (2, 101.0m, 100.6m),
            (3, 101.0m, 100.6m), (4, 100.6m, 100.2m), (5, 100.5m, 100.1m));
        Assert.Empty(Detect(bars, 6).Where(x => x.Kind == PivotKind.High));
    }

    [Fact]
    public void EqualLowPlateauIsNotAPivot()
    {
        var bars = Series((0, 100.4m, 100.2m), (1, 100.3m, 99.0m), (2, 100.3m, 99.0m),
            (3, 100.3m, 99.0m), (4, 100.4m, 100.1m), (5, 100.5m, 100.2m));
        Assert.Empty(Detect(bars, 6).Where(x => x.Kind == PivotKind.Low));
    }

    [Fact]
    public void StrictComparisonAgainstBothSidesIsRequired()
    {
        // 우측 두 번째 봉이 후보와 같은 고점이면 확정 피벗이 아니다.
        var bars = Series((0, 100.4m, 100.0m), (1, 100.5m, 100.1m), (2, 101.0m, 100.6m),
            (3, 100.7m, 100.3m), (4, 101.0m, 100.5m));
        Assert.Empty(Detect(bars, 5).Where(x => x.Kind == PivotKind.High));
    }

    [Fact]
    public void UnorderedAndDuplicatedInputProducesTheSamePivots()
    {
        var shape = new[]
        {
            (0, 100.4, 100.0), (1, 100.5, 100.1), (2, 101.0, 100.6),
            (3, 100.7, 100.3), (4, 100.6, 100.2), (5, 100.5, 100.1)
        };
        Candle Make((int Minute, double High, double Low) x) =>
            new(Fx.At(x.Minute), (x.High + x.Low) / 2, x.High, x.Low, (x.High + x.Low) / 2, 1000);

        var ordered = shape.Select(Make).ToArray();
        var shuffled = new[] { Make(shape[3]), Make(shape[0]), Make(shape[5]), Make(shape[2]), Make(shape[2]), Make(shape[1]), Make(shape[4]) };

        var a = Detect(BarAggregator.Normalize(ordered, Fx.SessionStart, Fx.SessionEnd, Fx.At(6)).Bars, 6);
        var b = Detect(BarAggregator.Normalize(shuffled, Fx.SessionStart, Fx.SessionEnd, Fx.At(6)).Bars, 6);
        Assert.Equal(a.ToArray(), b.ToArray());
        Assert.Single(a);
    }

    [Fact]
    public void PivotWindowRequiresContiguousBarsInsteadOfIndexNeighbours()
    {
        // 1분봉 하나가 결측이면 그 구간을 인접 봉으로 위장해 피벗을 만들지 않는다.
        var bars = Series((0, 100.4m, 100.0m), (1, 100.5m, 100.1m), (2, 101.0m, 100.6m),
            (4, 100.7m, 100.3m), (5, 100.6m, 100.2m));
        Assert.Empty(Detect(bars, 6));
    }

    [Fact]
    public void PivotSourceIdIsStableAndDistinguishesKindAndTime()
    {
        var bars = Series((0, 100.4m, 100.0m), (1, 100.5m, 100.1m), (2, 101.0m, 100.6m),
            (3, 100.7m, 100.3m), (4, 100.6m, 100.2m), (5, 100.5m, 100.1m), (6, 99.0m, 98.5m),
            (7, 99.2m, 98.7m), (8, 99.4m, 98.9m));
        var first = Detect(bars, 9);
        var second = Detect(bars, 9);
        Assert.NotEmpty(first);
        Assert.Equal(first.Select(x => x.SourceId), second.Select(x => x.SourceId));
        Assert.Equal(first.Length, first.Select(x => x.SourceId).Distinct().Count());
        Assert.All(first, x => Assert.Equal(64, x.SourceId.Length));
    }

    [Fact]
    public void FiveMinutePivotsUseTheirOwnConfirmationDelay()
    {
        var minutes = Enumerable.Range(0, 30).ToArray();
        var candles = minutes.Select(i =>
        {
            var bump = i is >= 10 and < 15 ? 1.0 : 0.0;
            return Fx.Candle(i, 100 + bump, 100.5 + bump, 99.5 + bump, 100.2 + bump);
        }).ToArray();
        var bars = BarAggregator.Normalize(candles, Fx.SessionStart, Fx.SessionEnd, Fx.At(30)).Bars;
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(30), P);

        // 5분 피벗 후보는 bucket 2(09:40~09:45)이고 우측 확인에 10분이 더 필요하다(§5.3).
        Assert.Empty(PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.FiveMinute, five, Fx.At(24), P));
        var confirmed = PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.FiveMinute, five, Fx.At(25), P);
        var pivot = Assert.Single(confirmed.Where(x => x.Kind == PivotKind.High));
        Assert.Equal(Fx.At(10), pivot.OccurredAt);
        Assert.Equal(Fx.At(25), pivot.ConfirmedAt);
        Assert.Equal(BarTimeframe.FiveMinute, pivot.Timeframe);
    }

    [Fact]
    public void TimeframeAndKindArePartOfTheCanonicalPivotSourceId()
    {
        var occurred = StructureMath.Iso(Fx.At(2));
        var oneMinuteHigh = StructureMath.SourceId("pivot", Fx.Symbol, "2026-09-09", "1m", "H", occurred);
        var fiveMinuteHigh = StructureMath.SourceId("pivot", Fx.Symbol, "2026-09-09", "5m", "H", occurred);
        var oneMinuteLow = StructureMath.SourceId("pivot", Fx.Symbol, "2026-09-09", "1m", "L", occurred);
        Assert.NotEqual(oneMinuteHigh, fiveMinuteHigh);
        Assert.NotEqual(oneMinuteHigh, oneMinuteLow);

        var bars = Series((0, 100.4m, 100.0m), (1, 100.5m, 100.1m), (2, 101.0m, 100.6m),
            (3, 100.7m, 100.3m), (4, 100.6m, 100.2m));
        var detected = Assert.Single(PivotDetector.Detect(Fx.Symbol, Fx.SessionStart, BarTimeframe.OneMinute, bars, Fx.At(5), P));
        Assert.Equal(oneMinuteHigh, detected.SourceId);
    }
}
