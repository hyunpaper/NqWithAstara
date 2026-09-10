using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §5.1 입력 스냅샷 + §16B "snapshot factory가 NY 거래일 기준 현재 일봉을 제거한다" + DataQuality 조립.
/// </summary>
public sealed class StructureD3SnapshotTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static StructureSnapshotBuild Build(int barCount, int nowMinute, Candle[]? daily = null,
        double? quotePrice = 100.0, DateTimeOffset? quoteAt = null, MarketSession? session = null,
        StructureLiquidity? liquidity = null) =>
        StructureSnapshotFactory.Create(D3.Symbol, session ?? D3.Session, D3.Candles(barCount),
            daily ?? D3.Daily(), quotePrice, quoteAt ?? D3.At(nowMinute), D3.At(nowMinute), 7, P, liquidity);

    /// <summary>AnalysisAsOf는 계산에 사용한 마지막 완료 봉의 종료 시각이다(§5.1). 진행 중 봉은 들어가지 않는다.</summary>
    [Fact]
    public void AnalysisAsOfIsTheEndOfTheLastCompletedBarAndTheInProgressBarIsExcluded()
    {
        var build = Build(barCount: 46, nowMinute: 45);
        Assert.NotNull(build.Snapshot);
        Assert.Equal(D3.At(45), build.Snapshot!.AnalysisAsOf);
        Assert.Equal(D3.At(44), build.LastCompletedBarStart);
        Assert.Equal(45, build.Bars.Bars.Length);
        Assert.DoesNotContain(build.Bars.Bars, x => x.End > D3.At(45));
        Assert.Equal(StructureAnalysisStatus.Available, build.Status);
    }

    /// <summary>§16B: NY 거래일 기준 진행 중 일봉은 Domain에 전달하기 전에 제거한다.</summary>
    [Fact]
    public void CurrentTradingDayDailyBarIsRemovedBeforeReachingTheDomain()
    {
        var build = Build(barCount: 46, nowMinute: 45, daily: D3.Daily(5, includeCurrentTradingDay: true));
        var sessionDate = MarketRules.TradingDate(D3.SessionStart);

        Assert.Contains(StructureSnapshotFactory.WarningCurrentDailyRemoved, build.Warnings);
        Assert.Equal(5, build.DailyBars.Length);
        Assert.All(build.DailyBars, x => Assert.True(x.TradingDate < sessionDate));
        // 진행 중 일봉의 고가(130)가 어떤 형태로도 남지 않는다.
        Assert.DoesNotContain(build.DailyBars, x => x.High >= 130m);
    }

    [Fact]
    public void InvalidDailyBarsAreDroppedWithAnExplicitWarningInsteadOfBeingRepaired()
    {
        Candle[] daily =
        [
            new(D3.SessionStart.AddDays(-2), 99, 100, 98, 99.5, 1000),
            new(D3.SessionStart.AddDays(-1), 99, double.NaN, 98, 99.5, 1000),
            new(D3.SessionStart.AddDays(-3), 99, 97, 98, 99.5, 1000)
        ];
        var build = Build(barCount: 46, nowMinute: 45, daily: daily);
        Assert.Contains(StructureSnapshotFactory.WarningInvalidDaily, build.Warnings);
        Assert.Single(build.DailyBars);
    }

    [Fact]
    public void FiveMinuteBarsAreAggregatedOnlyUpToTheAnalysisCutoff()
    {
        var build = Build(barCount: 46, nowMinute: 45);
        Assert.Equal(9, build.FiveMinuteBars.Length);
        Assert.All(build.FiveMinuteBars, x => Assert.True(x.End <= build.Snapshot!.AnalysisAsOf));
    }

    [Fact]
    public void NoCompletedBarIsWarmupAndProducesNoSnapshot()
    {
        var build = Build(barCount: 1, nowMinute: 0);
        Assert.Null(build.Snapshot);
        Assert.Equal(StructureAnalysisStatus.Warmup, build.Status);
        Assert.Contains(StructureSnapshotFactory.WarningNoCompletedBar, build.Warnings);
    }

    /// <summary>초기 데이터 부족은 warmup 상태이며 0점으로 대체하지 않는다(§5.1).</summary>
    [Fact]
    public void FewerThanThirtyCompletedBarsStaysWarmupWithAnExplicitWarning()
    {
        var build = Build(barCount: 21, nowMinute: 20);
        Assert.NotNull(build.Snapshot);
        Assert.Equal(StructureAnalysisStatus.Warmup, build.Status);
        Assert.Contains(StructureSnapshotFactory.WarningInsufficientBars, build.Warnings);
    }

    [Fact]
    public void OutsideTheRegularSessionThereIsNoSnapshotAndNoComputation()
    {
        var closed = StructureSnapshotFactory.Create(D3.Symbol, new MarketSession(false, "휴장", null, null, null),
            D3.Candles(46), D3.Daily(), 100.0, D3.At(45), D3.At(45), 1, P);
        Assert.Null(closed.Snapshot);
        Assert.Equal(StructureAnalysisStatus.MarketClosed, closed.Status);
        Assert.Contains(StructureSnapshotFactory.WarningNoSession, closed.Warnings);

        var after = StructureSnapshotFactory.Create(D3.Symbol, D3.Session, D3.Candles(46), D3.Daily(), 100.0,
            D3.SessionEnd, D3.SessionEnd, 1, P);
        Assert.Null(after.Snapshot);
        Assert.Equal(StructureAnalysisStatus.MarketClosed, after.Status);
        Assert.Contains(StructureSnapshotFactory.WarningOutsideSession, after.Warnings);
    }

    [Fact]
    public void MissingOrStaleQuoteIsExposedButNeverStopsTheAnalysis()
    {
        var missing = Build(barCount: 46, nowMinute: 45, quotePrice: null);
        Assert.NotNull(missing.Snapshot);
        Assert.Null(missing.Snapshot!.QuotePrice);
        Assert.Contains(StructureSnapshotFactory.WarningMissingQuote, missing.Warnings);

        var stale = Build(barCount: 46, nowMinute: 45, quoteAt: D3.At(45).AddSeconds(-16));
        Assert.NotNull(stale.Snapshot);
        Assert.Contains(StructureSnapshotFactory.WarningStaleQuote, stale.Warnings);
        Assert.Equal("stale", stale.Quality.Sources.First(x => x.Source == "quote").Status.ToString().ToLowerInvariant());

        var future = Build(barCount: 46, nowMinute: 45, quoteAt: D3.At(45).AddSeconds(6));
        Assert.Contains(StructureSnapshotFactory.WarningQuoteInFuture, future.Warnings);
    }

    /// <summary>§9.1: 전체 관심종목에 호가 조회를 추가하지 않는다. 없으면 비용 불확실성만 노출한다.</summary>
    [Fact]
    public void MissingLiquidityIsRecordedAsCostUncertaintyNotAsAFailure()
    {
        var build = Build(barCount: 46, nowMinute: 45);
        Assert.Contains(StructureSnapshotFactory.WarningMissingLiquidity, build.Warnings);
        var source = build.Quality.Sources.First(x => x.Source == "liquidity");
        Assert.Equal(SourceStatus.Missing, source.Status);
        Assert.Null(source.CoverageRatio);
        Assert.Equal(StructureAnalysisStatus.Available, build.Status);
    }

    // ── DataQuality 조립 (§16B) ──

    [Fact]
    public void DataQualityCarriesPerSourceStatusCountCoverageAndGaps()
    {
        var build = Build(barCount: 46, nowMinute: 45,
            liquidity: new StructureLiquidity(99.99m, 100.01m, D3.At(45), 100, 100));
        var sources = build.Quality.Sources.ToDictionary(x => x.Source, StringComparer.Ordinal);

        Assert.Equal(SourceStatus.Available, sources["bars1m"].Status);
        Assert.Equal(45, sources["bars1m"].Count);
        Assert.Equal(45, sources["bars1m"].ExpectedCount);
        Assert.Equal(1.0, sources["bars1m"].CoverageRatio);
        Assert.Empty(sources["bars1m"].Gaps);
        Assert.Empty(sources["bars1m"].Conflicts);
        Assert.Equal(D3.At(0), sources["bars1m"].First);
        Assert.Equal(D3.At(45), sources["bars1m"].Last);

        Assert.Equal(9, sources["bars5m"].Count);
        Assert.Equal(9, sources["bars5m"].ExpectedCount);
        Assert.Equal(SourceStatus.Available, sources["daily"].Status);
        Assert.Equal(P.DailyLookbackSessions, sources["daily"].ExpectedCount);
        Assert.Equal(SourceStatus.Available, sources["quote"].Status);
        Assert.Equal(SourceStatus.Available, sources["liquidity"].Status);
    }

    /// <summary>§16B: 잘못된 OHLCV·분봉 공백/충돌은 신규 분석 차단, 일봉 결측은 경고만이다.</summary>
    [Fact]
    public void BarGapsAndConflictsBlockNewAnalysisWhileMissingDailyIsOnlyAWarning()
    {
        var withGap = D3.Candles(46).Where(x => x.Timestamp != D3.At(20)).ToArray();
        var gapped = StructureSnapshotFactory.Create(D3.Symbol, D3.Session, withGap, D3.Daily(), 100.0,
            D3.At(45), D3.At(45), 1, P);
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, gapped.Quality.BlockersForZone);
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, gapped.Quality.BlockersForCandidate);
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, gapped.Quality.BlockersForReady);
        Assert.Equal(SourceStatus.Approximate, gapped.Quality.Sources.First(x => x.Source == "bars1m").Status);

        var conflicting = D3.Candles(46).Append(new Candle(D3.At(10), 1, 2, .5, 1.5, 10)).ToArray();
        var conflicted = StructureSnapshotFactory.Create(D3.Symbol, D3.Session, conflicting, D3.Daily(), 100.0,
            D3.At(45), D3.At(45), 1, P);
        Assert.Contains(StructureSnapshotFactory.BlockerBarConflict, conflicted.Quality.BlockersForCandidate);

        var invalid = D3.Candles(46).Append(new Candle(D3.At(46), -1, 2, .5, 1.5, 10)).ToArray();
        var dropped = StructureSnapshotFactory.Create(D3.Symbol, D3.Session, invalid, D3.Daily(), 100.0,
            D3.At(47), D3.At(47), 1, P);
        Assert.Contains(StructureSnapshotFactory.BlockerInvalidBars, dropped.Quality.BlockersForCandidate);

        var noDaily = StructureSnapshotFactory.Create(D3.Symbol, D3.Session, D3.Candles(46), [], 100.0,
            D3.At(45), D3.At(45), 1, P);
        Assert.Contains(StructureSnapshotFactory.WarningMissingDaily, noDaily.Warnings);
        Assert.Empty(noDaily.Quality.BlockersForZone);
        Assert.Empty(noDaily.Quality.BlockersForReady);
    }

    /// <summary>§16B: 지표 결측은 Trend/READY 차단, 5m 구조 결측은 READY 차단, 프로파일 근사는 경고만이다.</summary>
    [Fact]
    public void CompleteAddsIndicatorAndFiveMinuteStructureBlockersAndProfileApproximation()
    {
        var build = Build(barCount: 46, nowMinute: 45);
        var trend = TrendEvaluator.Evaluate(TrendRequest.Create(D3.Symbol, D3.SessionStart,
            build.Snapshot!.AnalysisAsOf, build.Bars.Bars, build.FiveMinuteBars), P);
        var profile = ZoneBuilder.BuildVolumeProfile(build.Bars.Bars, .2, P);

        var completed = StructureSnapshotFactory.Complete(build.Quality, trend, profile, ["ZONE_WARN"], ["SPREAD_MISSING"]);
        var sources = completed.Sources.ToDictionary(x => x.Source, StringComparer.Ordinal);

        Assert.Equal(SourceStatus.Approximate, sources["volumeProfile"].Status);
        Assert.Contains("EstimatedVolumeProfile", sources["volumeProfile"].Warnings);
        Assert.Contains("ZONE_WARN", completed.Warnings);
        Assert.Contains("SPREAD_MISSING", completed.Warnings);
        Assert.True(sources.ContainsKey("indicators"));
        Assert.True(sources.ContainsKey("pivots5m"));
        Assert.Equal(trend.BlockersForReady.Order(StringComparer.Ordinal),
            completed.BlockersForReady.Where(x => trend.BlockersForReady.Contains(x)).Order(StringComparer.Ordinal));

        var unknown = TrendEvaluator.Evaluate(TrendRequest.Create(D3.Symbol, D3.SessionStart, D3.At(5),
            build.Bars.Bars, build.FiveMinuteBars), P);
        var blocked = StructureSnapshotFactory.Complete(build.Quality, unknown, profile, null, null);
        Assert.Contains(TrendEvaluator.BlockerTrendUnavailable, blocked.BlockersForTrend);
        Assert.Contains(TrendEvaluator.BlockerTrendUnavailable, blocked.BlockersForReady);
    }

    [Fact]
    public void SnapshotIsDeterministicForTheSameInput()
    {
        var a = Build(barCount: 46, nowMinute: 45);
        var b = Build(barCount: 46, nowMinute: 45);
        Assert.Equal(a.Snapshot!.AnalysisAsOf, b.Snapshot!.AnalysisAsOf);
        Assert.Equal(a.Bars.Bars.Length, b.Bars.Bars.Length);
        Assert.Equal(a.Warnings.ToArray(), b.Warnings.ToArray());
        Assert.Equal(a.DailyBars.Select(x => x.TradingDate), b.DailyBars.Select(x => x.TradingDate));
    }
}
