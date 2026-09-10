using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 집계 → 피벗 → Zone → 평가 전체 경로. 설계 §15의 prefix invariance와 트리거 봉 격리를 고정한다.
/// </summary>
public sealed class StructurePipelineTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    /// <summary>결정적이고 구조가 생기는 합성 봉. 난수를 쓰지 않으므로 같은 입력은 항상 같은 결과다.</summary>
    static StructureBar Bar(int i)
    {
        var mid = 100m + (i % 7 - 3) * .05m + (i % 11 - 5) * .03m;
        return new StructureBar(Fx.At(i), Fx.At(i + 1), mid, mid + .12m, mid - .11m,
            mid + (i % 3 - 1) * .04m, 1000 + i % 13 * 100);
    }

    static ImmutableArray<StructureBar> Series(int count) => Enumerable.Range(0, count).Select(Bar).ToImmutableArray();

    static (ZoneBuildResult Build, ZoneEvaluationResult Evaluated) Run(ImmutableArray<StructureBar> bars, int cutoffMinute,
        ImmutableArray<StructureDailyBar>? daily = null)
    {
        var cutoff = Fx.At(cutoffMinute);
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, P);
        var build = ZoneBuilder.Build(ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, cutoff,
            bars, five, daily), P);
        var evaluated = ZoneEvaluator.Evaluate(build.Zones,
            ZoneEvaluationRequest.Create(Fx.SessionStart, cutoff, bars), P);
        return (build, evaluated);
    }

    [Fact]
    public void PrefixInvarianceHoldsForAggregationPivotsZonesAndStrength()
    {
        var full = Run(Series(60), 40);
        var prefix = Run(Series(40), 40);

        Assert.NotEmpty(full.Build.Zones);
        Assert.NotEmpty(full.Build.Pivots1m);
        Assert.NotEmpty(full.Build.Pivots5m);

        Assert.Equal(full.Build.Atr1mAtCutoff, prefix.Build.Atr1mAtCutoff);
        Assert.Equal(full.Build.Pivots1m.Select(x => x.SourceId), prefix.Build.Pivots1m.Select(x => x.SourceId));
        Assert.Equal(full.Build.Pivots5m.Select(x => x.SourceId), prefix.Build.Pivots5m.Select(x => x.SourceId));
        Assert.Equal(full.Build.Profile.BinWidth, prefix.Build.Profile.BinWidth);
        Assert.Equal(full.Build.Profile.InputVolume, prefix.Build.Profile.InputVolume);
        Assert.Equal(full.Build.Profile.Bins.Select(x => (x.Index, x.Volume)), prefix.Build.Profile.Bins.Select(x => (x.Index, x.Volume)));
        Assert.Equal(full.Build.Profile.Nodes.Select(x => x.StartIndex), prefix.Build.Profile.Nodes.Select(x => x.StartIndex));
        Assert.Equal(full.Build.Zones.Select(x => x.Fingerprint()), prefix.Build.Zones.Select(x => x.Fingerprint()));
        Assert.Equal(full.Evaluated.Zones.Select(x => x.Fingerprint()), prefix.Evaluated.Zones.Select(x => x.Fingerprint()));
        Assert.Equal(full.Evaluated.Episodes.Select(x => x.Fingerprint()), prefix.Evaluated.Episodes.Select(x => x.Fingerprint()));
    }

    [Fact]
    public void RepeatedRunsOnTheSameSnapshotAreByteIdentical()
    {
        var bars = Series(60);
        var a = Run(bars, 45);
        var b = Run(bars, 45);
        Assert.Equal(a.Evaluated.Zones.Select(x => x.Fingerprint()), b.Evaluated.Zones.Select(x => x.Fingerprint()));
        Assert.Equal(a.Build.Warnings.ToArray(), b.Build.Warnings.ToArray());
    }

    [Fact]
    public void EveryStructureSourceIsConfirmedAtOrBeforeTheCutoff()
    {
        var run = Run(Series(60), 40);
        Assert.All(run.Build.Zones.SelectMany(x => x.Sources), source =>
            Assert.True(source.ConfirmedAt <= Fx.At(40), $"{source.Kind} confirmed at {source.ConfirmedAt}"));
        Assert.All(run.Build.Pivots1m, pivot => Assert.True(pivot.ConfirmedAt <= Fx.At(40)));
        Assert.All(run.Evaluated.Episodes, episode => Assert.True(episode.ResolvedAt is null || episode.ResolvedAt <= Fx.At(40)));
    }

    /// <summary>§16B: structureCutoff=TriggerBarStart. 트리거 봉의 거래량·고가는 그 구조의 근거가 될 수 없다.</summary>
    [Fact]
    public void TriggerBarIsNeverItsOwnStructureEvidence()
    {
        var priorBars = Series(40);
        var trigger = new StructureBar(Fx.At(40), Fx.At(41), 100m, 120m, 99.90m, 119m, 999_999);
        var bars = priorBars.Add(trigger);
        var structureCutoff = Fx.At(40);

        var atTrigger = ZoneBuilder.Build(ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            structureCutoff, bars, BarAggregator.Aggregate(bars, Fx.SessionStart, structureCutoff, P)), P);

        Assert.Equal(priorBars.Sum(x => x.Volume), atTrigger.Profile.InputVolume, 6);
        Assert.All(atTrigger.Profile.Bins, bin => Assert.True(bin.Upper <= 105m, $"bin {bin.Lower}-{bin.Upper}"));
        Assert.All(atTrigger.Zones, zone => Assert.True(zone.Upper < 105m, $"zone {zone.Lower}-{zone.Upper}"));
        Assert.DoesNotContain(atTrigger.Pivots1m, x => x.OccurredAt >= structureCutoff);
        Assert.Contains("FUTURE_BAR_INPUT_REJECTED", atTrigger.Warnings);

        // 대조: 트리거 봉이 완료 봉이 된 이후에는 실제로 거래량이 늘어난다.
        var afterTrigger = ZoneBuilder.Build(ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            Fx.At(41), bars, BarAggregator.Aggregate(bars, Fx.SessionStart, Fx.At(41), P)), P);
        Assert.True(afterTrigger.Profile.InputVolume > atTrigger.Profile.InputVolume);
    }

    [Fact]
    public void IneligibleZonesAlwaysCarryAnExplicitReasonAndNoScoreIsFabricated()
    {
        var run = Run(Series(60), 55);
        Assert.NotEmpty(run.Evaluated.Zones);
        foreach (var zone in run.Evaluated.Zones)
        {
            if (zone.Eligible) Assert.Empty(zone.RejectReasons);
            else Assert.NotEmpty(zone.RejectReasons);
            if (zone.ProfileOnly) Assert.False(zone.Eligible);
            Assert.True(zone.Strength!.Value is null || double.IsFinite(zone.Strength.Value.Value));
            Assert.True(zone.Lower <= zone.Upper);
        }
    }

    [Fact]
    public void CurrentTradingDayDailyBarIsRejectedByTheDomainToo()
    {
        var days = ImmutableArray.Create(
            new StructureDailyBar(new DateOnly(2026, 9, 8), 99m, 100m, 98m, 99.5m, 1000),
            new StructureDailyBar(new DateOnly(2026, 9, 9), 99.5m, 101m, 99m, 100.5m, 1000));   // 진행 중 거래일
        var run = Run(ImmutableArray<StructureBar>.Empty, 10, days);

        Assert.Contains("CURRENT_DAILY_BAR_REJECTED", run.Build.Warnings);
        var daily = run.Build.Zones.SelectMany(x => x.Sources).Where(x => x.Kind.StartsWith("daily-")).ToArray();
        Assert.NotEmpty(daily);
        Assert.All(daily, source => Assert.NotEqual(
            StructureMath.SourceId("daily", Fx.Symbol, "2026-09-09", "H"), source.Id));
    }

    [Fact]
    public void TwentyDayExtremaReuseTheOriginDailyBarIdWithoutDoubleCounting()
    {
        StructureDailyBar Day(int i, decimal high) =>
            new(new DateOnly(2026, 8, 10).AddDays(i), 99m, high, 98m, 99.5m, 1000);

        // 20일 최고가가 과거 일봉에 있는 경우: 해당 일봉의 H ID가 별도 원천으로 재사용된다.
        var earlier = Run(ImmutableArray<StructureBar>.Empty, 10,
            Enumerable.Range(0, 20).Select(i => Day(i, i == 5 ? 105m : 100m)).ToImmutableArray());
        var earlierDaily = earlier.Build.Zones.SelectMany(x => x.Sources).Where(x => x.Kind.StartsWith("daily-")).ToArray();
        Assert.Equal(4, earlierDaily.Length);
        Assert.Equal(4, earlierDaily.Select(x => x.Id).Distinct().Count());
        Assert.Equal(2, earlierDaily.Count(x => x.Kind == "daily-H"));

        // 20일 최고가가 전일인 경우: 같은 일봉을 두 독립 증거로 세지 않는다(§16B).
        var previous = Run(ImmutableArray<StructureBar>.Empty, 10,
            Enumerable.Range(0, 20).Select(i => Day(i, i == 19 ? 105m : 100m)).ToImmutableArray());
        var previousDaily = previous.Build.Zones.SelectMany(x => x.Sources).Where(x => x.Kind.StartsWith("daily-")).ToArray();
        Assert.Equal(3, previousDaily.Length);
        Assert.Single(previousDaily, x => x.Kind == "daily-H");
    }

    [Fact]
    public void OpeningRangeBecomesASourceOnlyAfterTheFifteenMinutesComplete()
    {
        var bars = Series(15);
        var before = Run(bars, 14).Build.Zones.SelectMany(x => x.Sources).Select(x => x.Kind).ToArray();
        Assert.DoesNotContain("orb15-H", before);
        Assert.DoesNotContain("orb15-L", before);

        var after = Run(bars, 15).Build;
        var kinds = after.Zones.SelectMany(x => x.Sources).Select(x => x.Kind).ToArray();
        Assert.Contains("orb15-H", kinds);
        Assert.Contains("orb15-L", kinds);
        var orbHigh = after.Zones.SelectMany(x => x.Sources).First(x => x.Kind == "orb15-H");
        Assert.Equal(bars.Max(x => x.High), orbHigh.Price);
        Assert.Equal(Fx.At(15), orbHigh.ConfirmedAt);
    }

    [Fact]
    public void OpeningRangeIsSkippedWhenAMinuteIsMissing()
    {
        var bars = Series(16).Where(x => x.Start != Fx.At(7)).ToImmutableArray();
        var kinds = Run(bars, 16).Build.Zones.SelectMany(x => x.Sources).Select(x => x.Kind).ToArray();
        Assert.DoesNotContain("orb15-H", kinds);
    }

    [Fact]
    public void FiveMinuteStructureContributesItsOwnPivotSources()
    {
        var run = Run(Series(60), 50);
        var kinds = run.Build.Zones.SelectMany(x => x.Sources).Select(x => x.Kind).Distinct().ToArray();
        Assert.Contains(kinds, x => x.StartsWith("pivot-5m-"));
        Assert.Contains(kinds, x => x.StartsWith("pivot-1m-"));
    }
}
