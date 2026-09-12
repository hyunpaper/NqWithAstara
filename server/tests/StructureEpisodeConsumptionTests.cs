using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureEpisodeConsumptionTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;
    const int TriggerMinute = 30;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    static ImmutableArray<StructureBar> Bars()
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var m = 0; m < 25; m++) bars.Add(Bar(m, 99.90m, 100.10m, 100m, 100m));
        bars.Add(Bar(25, 99.30m, 99.90m, 99.85m, 99.35m));
        bars.Add(Bar(26, 99.15m, 99.45m, 99.35m, 99.25m));
        bars.Add(Bar(27, 99.35m, 99.55m, 99.30m, 99.50m));
        bars.Add(Bar(28, 99.50m, 99.70m, 99.50m, 99.60m));
        bars.Add(Bar(29, 99.55m, 99.65m, 99.60m, 99.60m));
        bars.Add(Bar(TriggerMinute, 99.58m, 99.85m, 99.60m, 99.80m, 2000));
        return bars.ToImmutable();
    }

    static SetupDetectionResult Detect()
    {
        var analysisAsOf = Fx.At(TriggerMinute + 1);
        return SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, Bars(), [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)],
            [D2.Episode("support-zone", 25, 28)], D2.Trend(), .20, 100.00m, analysisAsOf,
            D2.Quote(99.99m, 100.01m, TriggerMinute + 1)), P);
    }

    [Fact]
    public void PullbackAndReboundOfTheSameEpisodeShareZoneAnchorInvalidationAndStop()
    {
        var candidates = Detect().Candidates;
        var pullback = Assert.Single(candidates.Where(x => x.Kind == SetupKind.Pullback));
        var rebound = Assert.Single(candidates.Where(x => x.Kind == SetupKind.Rebound));

        Assert.Equal(pullback.ZoneId, rebound.ZoneId);
        Assert.Equal(pullback.EpisodeStartAt, rebound.EpisodeStartAt);
        Assert.Equal(pullback.InvalidationAnchor, rebound.InvalidationAnchor);
        Assert.Equal(pullback.Planning.Anchor, rebound.Planning.Anchor);
        Assert.Equal(pullback.Planning.Stop, rebound.Planning.Stop);
        Assert.Equal(pullback.Planning.InvalidationZone?.Id, rebound.Planning.InvalidationZone?.Id);
        Assert.Equal(pullback.Planning.InvalidationZone?.Lower, rebound.Planning.InvalidationZone?.Lower);
        Assert.Equal(pullback.Planning.InvalidationZone?.Upper, rebound.Planning.InvalidationZone?.Upper);
        Assert.Equal(pullback.Plan?.Stop, rebound.Plan?.Stop);
        Assert.Equal(pullback.Plan?.InvalidationAnchor, rebound.Plan?.InvalidationAnchor);
    }
}
