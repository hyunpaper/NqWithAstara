using System.Collections.Immutable;
using Astra.Server;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>PULLBACK 롱 전용 진입 게이트 계약(§9.3, #245 H-PB1 ATR 상한·H4 갭다운 상한).</summary>
public sealed class StructurePullbackGateTests
{
    const int TriggerMinute = 30;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    static ImmutableArray<StructureBar> PullbackBars(decimal firstOpen = 100m, decimal episodeLow = 99.15m)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        bars.Add(Bar(0, firstOpen - .10m, firstOpen + .10m, firstOpen, firstOpen));
        for (var m = 1; m < 25; m++) bars.Add(Bar(m, 99.90m, 100.10m, 100m, 100m));
        bars.Add(Bar(25, 99.30m, 99.90m, 99.85m, 99.35m));
        bars.Add(Bar(26, episodeLow, 99.45m, 99.35m, 99.25m));
        bars.Add(Bar(27, 99.35m, 99.55m, 99.30m, 99.50m));
        bars.Add(Bar(28, 99.50m, 99.70m, 99.50m, 99.60m));
        bars.Add(Bar(29, 99.55m, 99.65m, 99.60m, 99.60m));
        bars.Add(Bar(TriggerMinute, 99.58m, 99.85m, 99.60m, 99.80m, 2000));
        return bars.ToImmutable();
    }

    static ImmutableArray<StructureBar> BreakoutBars(decimal resistanceUpper, decimal triggerClose)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < TriggerMinute; m++)
            bars.Add(Bar(m, resistanceUpper - .40m, resistanceUpper - .05m, resistanceUpper - .30m, resistanceUpper - .10m));
        bars.Add(Bar(TriggerMinute, resistanceUpper - .10m, triggerClose + .05m, resistanceUpper - .05m,
            triggerClose, 2000));
        return bars.ToImmutable();
    }

    static ImmutableArray<PriceZone> PullbackZones() =>
        [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)];

    static ImmutableArray<TouchEpisode> PullbackEpisodes() =>
        [D2.Episode("support-zone", 25, 28)];

    static SetupDetectionResult Detect(StructurePolicy policy, ImmutableArray<StructureBar> bars,
        ImmutableArray<PriceZone> zones, ImmutableArray<TouchEpisode> episodes, TrendAssessment? trend = null,
        double? atr = .20, decimal? previousDailyClose = null, decimal? live = 100.00m,
        StructureLiquidity? liquidity = null)
    {
        var analysisAsOf = Fx.At(TriggerMinute + 1);
        var request = SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, analysisAsOf,
            analysisAsOf, bars, zones, episodes, trend ?? D2.Trend(), atr, live, analysisAsOf,
            liquidity ?? D2.Quote(99.99m, 100.01m, TriggerMinute + 1), previousDailyClose: previousDailyClose);
        return SetupDetector.Detect(request, policy);
    }

    static EntryCandidate Pullback(SetupDetectionResult result) =>
        result.Candidates.Single(x => x.Kind == SetupKind.Pullback && x.Side == TradeSide.Long);

    static readonly StructurePolicy GatesOff =
        StructurePolicy.Default with { PullbackMaxAtrPercent = null, PullbackMaxGapDownPercent = null };

    [Fact]
    public void DefaultCarriesBothGatesAndNullingThemRemovesThemFromCanonicalJson()
    {
        Assert.Equal(.25, StructurePolicy.Default.PullbackMaxAtrPercent);
        Assert.Equal(5.0, StructurePolicy.Default.PullbackMaxGapDownPercent);
        var defaultJson = StructurePolicy.Default.CanonicalJson;
        Assert.Contains("\"PullbackMaxAtrPercent\":0.25", defaultJson);
        Assert.Contains("\"PullbackMaxGapDownPercent\":5", defaultJson);
        var offJson = GatesOff.CanonicalJson;
        Assert.DoesNotContain("PullbackMaxAtrPercent", offJson);
        Assert.DoesNotContain("PullbackMaxGapDownPercent", offJson);
    }

    [Fact]
    public void SettingAGateChangesTheHash()
    {
        var baseline = GatesOff.PolicyHash;
        var atr = GatesOff with { PullbackMaxAtrPercent = .25 };
        var gap = GatesOff with { PullbackMaxGapDownPercent = 5 };
        Assert.NotEqual(baseline, atr.PolicyHash);
        Assert.NotEqual(baseline, gap.PolicyHash);
        Assert.NotEqual(atr.PolicyHash, gap.PolicyHash);
        Assert.Contains("\"PullbackMaxAtrPercent\":0.25", atr.CanonicalJson);
        Assert.Contains("\"PullbackMaxGapDownPercent\":5", gap.CanonicalJson);
    }

    [Fact]
    public void NullGatesProduceNoPullbackRejection()
    {
        var pullback = Pullback(Detect(GatesOff, PullbackBars(), PullbackZones(), PullbackEpisodes(),
            previousDailyClose: 100m));
        Assert.DoesNotContain(SetupDetector.CodePullbackAtrTooHigh, pullback.RejectionCodes);
        Assert.DoesNotContain(SetupDetector.CodePullbackGapDown, pullback.RejectionCodes);
        Assert.Equal(CandidateDisposition.Ready, pullback.Disposition);
    }

    [Theory]
    [InlineData(.20, true)]
    [InlineData(.15, true)]
    [InlineData(.25, false)]
    public void AtrGateRejectsAtOrAboveTheThreshold(double threshold, bool rejected)
    {
        var policy = StructurePolicy.Default with { PullbackMaxAtrPercent = threshold };
        var pullback = Pullback(Detect(policy, PullbackBars(), PullbackZones(), PullbackEpisodes()));
        Assert.Equal(rejected, pullback.RejectionCodes.Contains(SetupDetector.CodePullbackAtrTooHigh));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    public void GapDownGateRejectsAtOrBeyondTheThreshold(double threshold, bool rejected)
    {
        var policy = StructurePolicy.Default with { PullbackMaxGapDownPercent = threshold };
        var pullback = Pullback(Detect(policy, PullbackBars(firstOpen: 95m), PullbackZones(), PullbackEpisodes(),
            previousDailyClose: 100m));
        Assert.Equal(rejected, pullback.RejectionCodes.Contains(SetupDetector.CodePullbackGapDown));
    }

    [Fact]
    public void GapDownGateWithoutPreviousCloseDoesNotReject()
    {
        var policy = StructurePolicy.Default with { PullbackMaxGapDownPercent = 1 };
        var pullback = Pullback(Detect(policy, PullbackBars(firstOpen: 95m), PullbackZones(), PullbackEpisodes()));
        Assert.DoesNotContain(SetupDetector.CodePullbackGapDown, pullback.RejectionCodes);
    }

    [Fact]
    public void GapUpSessionIsNotRejectedByTheGapDownGate()
    {
        var policy = StructurePolicy.Default with { PullbackMaxGapDownPercent = 1 };
        var pullback = Pullback(Detect(policy, PullbackBars(firstOpen: 105m), PullbackZones(), PullbackEpisodes(),
            previousDailyClose: 100m));
        Assert.DoesNotContain(SetupDetector.CodePullbackGapDown, pullback.RejectionCodes);
    }

    [Fact]
    public void ReboundIsUnaffectedByBothPullbackGates()
    {
        var policy = StructurePolicy.Default with { PullbackMaxAtrPercent = .01, PullbackMaxGapDownPercent = .01 };
        var rebound = Detect(policy, PullbackBars(firstOpen: 95m, episodeLow: 99.10m), PullbackZones(),
                PullbackEpisodes(), D2.Trend(TrendState.Range, -20), previousDailyClose: 100m)
            .Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.DoesNotContain(SetupDetector.CodePullbackAtrTooHigh, rebound.RejectionCodes);
        Assert.DoesNotContain(SetupDetector.CodePullbackGapDown, rebound.RejectionCodes);
    }

    [Fact]
    public void BreakoutIsUnaffectedByBothPullbackGates()
    {
        var policy = StructurePolicy.Default with { PullbackMaxAtrPercent = .01, PullbackMaxGapDownPercent = .01 };
        var zones = ImmutableArray.Create(D2.Resistance(99.90m, 100.10m, id: "breakout-zone"),
            D2.Resistance(101.80m, 102.10m, id: "target-zone"));
        var breakout = Detect(policy, BreakoutBars(100.10m, 100.30m), zones, ImmutableArray<TouchEpisode>.Empty,
                previousDailyClose: 100m, live: 100.30m, liquidity: D2.Quote(100.29m, 100.31m, TriggerMinute + 1))
            .Candidates.Single(x => x.Kind == SetupKind.Breakout);
        Assert.DoesNotContain(SetupDetector.CodePullbackAtrTooHigh, breakout.RejectionCodes);
        Assert.DoesNotContain(SetupDetector.CodePullbackGapDown, breakout.RejectionCodes);
    }
}
