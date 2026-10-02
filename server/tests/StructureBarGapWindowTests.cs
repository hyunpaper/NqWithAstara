using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureBarGapWindowTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static StructureSnapshotBuild Build(Candle[] bars, int nowMinute, StructurePolicy? policy = null) =>
        StructureSnapshotFactory.Create(D3.Symbol, D3.Session, bars, D3.Daily(), 100.0, D3.At(nowMinute),
            D3.At(nowMinute), 1, policy ?? P);

    static Candle[] WithoutMinutes(int count, params int[] missing) =>
        D3.Candles(count).Where(x => !missing.Contains((int)(x.Timestamp - D3.SessionStart).TotalMinutes)).ToArray();

    static DataSourceQuality Bars1m(StructureSnapshotBuild build) => Source(build, "bars1m");

    static DataSourceQuality Source(StructureSnapshotBuild build, string name) => build.Quality.Sources.First(x => x.Source == name);

    [Fact]
    public void 장중_4분_공백_직후_N봉_이내는_차단한다()
    {
        var bars = WithoutMinutes(41, 7, 8, 9, 10);

        var build = Build(bars, nowMinute: 40);

        Assert.Equal(29, build.Bars.Bars.Count(x => x.Start >= D3.At(11)));
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForTrend);
        Assert.Equal(StructureAnalysisStatus.Unavailable, build.Status);
    }

    [Fact]
    public void 장중_4분_공백_뒤_N봉이_완료되면_후보를_재개한다()
    {
        var bars = WithoutMinutes(42, 7, 8, 9, 10);

        var build = Build(bars, nowMinute: 41);

        Assert.Equal(30, build.Bars.Bars.Count(x => x.Start >= D3.At(11)));
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForTrend);
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForZone);
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForReady);
        Assert.Equal(StructureAnalysisStatus.Available, build.Status);
    }

    [Fact]
    public void 창_밖_공백은_품질_경고와_Gaps_기록으로만_남는다()
    {
        var build = Build(WithoutMinutes(42, 7, 8, 9, 10), nowMinute: 41);

        var source = Bars1m(build);
        Assert.Equal(SourceStatus.Approximate, source.Status);
        Assert.Single(source.Gaps);
        Assert.Contains("x4", source.Gaps[0]);
        Assert.Contains("BAR_GAP", build.Warnings);
        Assert.Empty(build.Quality.BlockersForCandidate);
    }

    [Fact]
    public void 창_안에_공백이_여러_번이면_차단한다()
    {
        var build = Build(WithoutMinutes(51, 15, 25), nowMinute: 50);

        Assert.Equal(2, Bars1m(build).Gaps.Length);
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
        Assert.Equal(StructureAnalysisStatus.Unavailable, build.Status);
    }

    [Fact]
    public void 과거_공백이_창을_벗어나도_새_공백이_창_안에_있으면_차단한다()
    {
        var build = Build(WithoutMinutes(56, 7, 8, 9, 10, 50), nowMinute: 55);

        Assert.Equal(2, Bars1m(build).Gaps.Length);
        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
    }

    [Fact]
    public void 공백이_없으면_차단도_Gaps도_없다()
    {
        var build = Build(D3.Candles(46), nowMinute: 45);

        Assert.Empty(Bars1m(build).Gaps);
        Assert.Equal(SourceStatus.Available, Bars1m(build).Status);
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
        Assert.Equal(StructureAnalysisStatus.Available, build.Status);
    }

    [Fact]
    public void 창_크기가_0이면_세션_전체_공백을_차단한다()
    {
        var build = Build(WithoutMinutes(42, 7, 8, 9, 10), nowMinute: 41, P with { BarGapBlockWindowBars = 0 });

        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, build.Quality.BlockersForCandidate);
    }

    [Fact]
    public void 창_크기를_줄이면_같은_공백이_더_빨리_풀린다()
    {
        var bars = WithoutMinutes(41, 7, 8, 9, 10);

        var wide = Build(bars, nowMinute: 40);
        var narrow = Build(bars, nowMinute: 40, P with { BarGapBlockWindowBars = 10 });

        Assert.Contains(StructureSnapshotFactory.BlockerBarGap, wide.Quality.BlockersForCandidate);
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, narrow.Quality.BlockersForCandidate);
    }

    [Fact]
    public void 다섯분봉_원천도_원천_주기로_창을_센다()
    {
        var start = Fx.At(0);
        var session = new MarketSession(true, "5분 fixture", null, start, start.AddHours(6.5));
        var bars = Enumerable.Range(0, 20).Where(i => i != 5)
            .Select(i => new Candle(start.AddMinutes(i * 5), 100, 100.20, 99.80, 100, 1000)).ToArray();
        var policy = StructurePolicy.Default with { Minimum1mBars = 6, BarGapBlockWindowBars = 10 };
        var now = start.AddMinutes(20 * 5);

        var result = StructureSnapshotFactory.Create("TEST", session, bars, [], 100, now, now, 1, policy,
            barDuration: TimeSpan.FromMinutes(5));

        Assert.Single(Source(result, "bars5m-native").Gaps);
        Assert.DoesNotContain(StructureSnapshotFactory.BlockerBarGap, result.Quality.BlockersForCandidate);
    }
}
