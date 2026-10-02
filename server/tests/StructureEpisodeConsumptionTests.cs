using System.Collections.Immutable;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureEpisodeConsumptionTests
{
    static readonly StructurePolicy P = D2.AllLongKinds;
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

    static SetupDetectionResult Detect(TrendState state)
    {
        var analysisAsOf = Fx.At(TriggerMinute + 1);
        return SetupDetector.Detect(SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            analysisAsOf, analysisAsOf, Bars(), [D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)],
            [D2.Episode("support-zone", 25, 28)], D2.Trend(state), .20, 100.00m, analysisAsOf,
            D2.Quote(99.99m, 100.01m, TriggerMinute + 1)), P);
    }

    [Fact]
    public void PullbackAndReboundOfTheSameEpisodeShareZoneAnchorInvalidationAndStop()
    {
        var pullback = Assert.Single(Detect(TrendState.Up).Candidates.Where(x => x.Kind == SetupKind.Pullback));
        var rebound = Assert.Single(Detect(TrendState.Range).Candidates.Where(x => x.Kind == SetupKind.Rebound));

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

    static StructuralLatch Fresh() => StructuralLatch.Empty(Fx.Symbol, Fx.SessionStart, P.PolicyHash);

    static EntryCandidate Ready(SetupKind kind) =>
        Detect(kind == SetupKind.Rebound ? TrendState.Range : TrendState.Up).Candidates
            .Single(x => x.Kind == kind && x.Disposition == CandidateDisposition.Ready);

    static StructuralLatch Commit(StructuralLatch latch, EntryCandidate candidate, bool consumeOnReady,
        IEnumerable<string>? entryBlocked = null) =>
        StructuralLifecycle.Commit(latch, Fx.At(TriggerMinute), [candidate], [], null, null, entryBlocked,
            consumeOnReady);

    static EntryCandidate Apply(StructuralLatch latch, EntryCandidate candidate) =>
        Assert.Single(StructuralLifecycle.ApplyLatch(latch, [candidate], allowNewTrigger: true, P));

    static EntryCandidate NextTrigger(EntryCandidate candidate) => candidate with
    {
        EventId = candidate.EventId + "-next",
        DuplicateGuardKey = candidate.DuplicateGuardKey + "-next"
    };

    [Fact]
    public void ActiveEnteredPullbackBlocksTheSameEpisodeRebound()
    {
        var latch = Commit(Fresh(), Ready(SetupKind.Pullback) with { Disposition = CandidateDisposition.Entered },
            consumeOnReady: false);

        var rebound = Apply(latch, NextTrigger(Ready(SetupKind.Rebound)));
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Contains(StructuralLifecycle.CodeEpisodeConsumed, rebound.RejectionCodes);
    }

    [Fact]
    public void ActiveReadyWithoutEntryDoesNotConsumeTheEpisode()
    {
        var latch = Commit(Fresh(), Ready(SetupKind.Pullback), consumeOnReady: false);

        Assert.Equal(CandidateDisposition.Ready, Apply(latch, NextTrigger(Ready(SetupKind.Rebound))).Disposition);
    }

    [Fact]
    public void ShadowReadyPullbackBlocksTheSameEpisodeReboundLikeActiveEntry()
    {
        var latch = Commit(Fresh(), Ready(SetupKind.Pullback), consumeOnReady: true);

        var rebound = Apply(latch, NextTrigger(Ready(SetupKind.Rebound)));
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Contains(StructuralLifecycle.CodeEpisodeConsumed, rebound.RejectionCodes);
    }

    [Fact]
    public void EntryBlockedReadyCandidateDoesNotConsumeTheEpisode()
    {
        var pullback = Ready(SetupKind.Pullback);
        var active = Commit(Fresh(), pullback, consumeOnReady: false, entryBlocked: [pullback.EventId]);
        var shadow = Commit(Fresh(), pullback, consumeOnReady: true, entryBlocked: [pullback.EventId]);

        Assert.Equal(CandidateDisposition.Ready, Apply(active, NextTrigger(Ready(SetupKind.Rebound))).Disposition);
        Assert.Equal(CandidateDisposition.Ready, Apply(shadow, NextTrigger(Ready(SetupKind.Rebound))).Disposition);
    }

    [Fact]
    public void ADifferentZoneOrEpisodeStaysArmed()
    {
        var latch = Commit(Fresh(), Ready(SetupKind.Pullback), consumeOnReady: true);
        var rebound = NextTrigger(Ready(SetupKind.Rebound));

        Assert.Equal(CandidateDisposition.Ready, Apply(latch, rebound with { ZoneId = "another-zone" }).Disposition);
        Assert.Equal(CandidateDisposition.Ready,
            Apply(latch, rebound with { EpisodeStartAt = Fx.At(28) }).Disposition);
    }

    [Fact]
    public void ConsumedEpisodeSurvivesALatchStorageRoundTrip()
    {
        var latch = Commit(Fresh(), Ready(SetupKind.Pullback) with { Disposition = CandidateDisposition.Entered },
            consumeOnReady: false);
        var restored = StructureLatchStorage.Parse(StructureLatchStorage.Serialize([latch])).Single();

        var rebound = Apply(restored, NextTrigger(Ready(SetupKind.Rebound)));
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Contains(StructuralLifecycle.CodeEpisodeConsumed, rebound.RejectionCodes);
    }
}
