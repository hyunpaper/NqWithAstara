using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §6.3 / §16B. 과병합 방지, 원천 보존, 중복 가산 방지, 안정 ID, revision 규칙.</summary>
public sealed class StructureZoneBuilderTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static ZoneBuildRequest Request(int cutoffMinute, ImmutableArray<PriceZone>? previous = null,
        ImmutableArray<string>? retired = null) =>
        ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, Fx.At(cutoffMinute),
            ImmutableArray<StructureBar>.Empty, ImmutableArray<StructureBar>.Empty, null, previous, retired);

    static ZoneCandidate Line(decimal price, decimal half, ZoneSource source) =>
        ZoneCandidate.FromLevel(price, half, source);

    [Fact]
    public void OverlappingCandidatesMergeAndKeepEverySourceAndConfirmationTime()
    {
        // ATR 0.20 → 반폭 0.03, 병합 간격 0.02, 병합 최대 폭 0.15
        var a = Fx.Pivot("a", 100.00m, 10, 12);
        var b = Fx.Pivot("b", 100.02m, 20, 22);
        var assembly = ZoneBuilder.Assemble([Line(100.00m, .03m, a), Line(100.02m, .03m, b)], Request(30), .20, P);

        var zone = Assert.Single(assembly.Zones);
        Assert.Equal(99.97m, zone.Lower);
        Assert.Equal(100.05m, zone.Upper);
        Assert.Equal(2, zone.Sources.Length);
        Assert.Contains(a.Id, zone.SourceIds);
        Assert.Contains(b.Id, zone.SourceIds);
        Assert.Equal(Fx.At(12), zone.FirstConfirmedAt);
        Assert.Equal(Fx.At(22), zone.LastConfirmedAt);
    }

    [Fact]
    public void ChainedCandidatesDoNotMergeBeyondTheWidthCap()
    {
        // 각 간격은 0.02(<=병합 간격)이지만 세 개를 모두 합치면 폭 0.22 > 0.15이므로 연결식 병합을 막는다.
        var candidates = new[]
        {
            Line(100.00m, .03m, Fx.Pivot("a", 100.00m, 10, 12)),
            Line(100.08m, .03m, Fx.Pivot("b", 100.08m, 20, 22)),
            Line(100.16m, .03m, Fx.Pivot("c", 100.16m, 30, 32))
        };
        var zones = ZoneBuilder.Assemble(candidates, Request(40), .20, P).Zones;

        Assert.Equal(2, zones.Length);
        Assert.Equal(99.97m, zones[0].Lower);
        Assert.Equal(100.11m, zones[0].Upper);
        Assert.Equal(.14m, zones[0].Width);
        Assert.Equal(100.13m, zones[1].Lower);
        Assert.All(zones, z => Assert.True(z.Width <= .15m));
        Assert.Equal(3, zones.Sum(x => x.Sources.Length));
    }

    [Fact]
    public void DistantCandidatesStayApart()
    {
        var zones = ZoneBuilder.Assemble(
        [
            Line(100.00m, .03m, Fx.Pivot("a", 100.00m, 10, 12)),
            Line(101.00m, .03m, Fx.Pivot("b", 101.00m, 20, 22))
        ], Request(30), .20, P).Zones;
        Assert.Equal(2, zones.Length);
        Assert.Equal(new[] { 99.97m, 100.97m }, zones.Select(x => x.Lower).ToArray());
    }

    [Fact]
    public void AWideProfileZoneIsNeitherNarrowedNorAbsorbingOthers()
    {
        var profile = ZoneCandidate.FromBounds(99.90m, 100.60m, Fx.ProfileSource("poc", 100.25m, 30), "EstimatedVolumeProfile");
        var pivot = Line(100.70m, .03m, Fx.Pivot("a", 100.70m, 10, 12));
        var zones = ZoneBuilder.Assemble([profile, pivot], Request(30), .20, P).Zones;

        Assert.Equal(2, zones.Length);
        Assert.Equal(.70m, zones[0].Width);                 // 원래 폭이 유지된다
        Assert.True(zones[0].ProfileOnly);
        Assert.False(zones[1].ProfileOnly);
    }

    [Fact]
    public void SameMovementFromOneAndFiveMinutePivotsCountsAsASingleFamily()
    {
        var oneMinute = Fx.Pivot("1m", 100.00m, 10, 12);
        var fiveMinute = Fx.Pivot("5m", 100.00m, 10, 15, BarTimeframe.FiveMinute);
        var zone = Assert.Single(ZoneBuilder.Assemble(
            [Line(100.00m, .03m, oneMinute), Line(100.00m, .03m, fiveMinute)], Request(30), .20, P).Zones);

        Assert.Equal(2, zone.Sources.Length);
        Assert.Single(zone.EvidenceGroups);                                       // 시간 구간을 공유하면 한 group
        Assert.Single(zone.Sources.Select(x => x.Family).Distinct());             // family는 pivot 하나
        var evaluated = ZoneEvaluator.Evaluate([zone],
            ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(30), ImmutableArray<StructureBar>.Empty), P).Zones[0];
        Assert.Equal(1, evaluated.Strength!.IndependentFamilies);
        Assert.Equal(1, evaluated.Strength.IndependentNonProfileFamilies);
    }

    [Fact]
    public void DifferentFamiliesInTheSameZoneCountAsIndependentConfluence()
    {
        var zone = Assert.Single(ZoneBuilder.Assemble(
        [
            Line(100.00m, .03m, Fx.Pivot("1m", 100.00m, 10, 12)),
            Line(100.01m, .03m, Fx.Daily("prev-L", 100.01m))
        ], Request(30), .20, P).Zones);
        Assert.Equal(2, zone.EvidenceGroups.Length);
        var evaluated = ZoneEvaluator.Evaluate([zone],
            ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(30), ImmutableArray<StructureBar>.Empty), P).Zones[0];
        Assert.Equal(2, evaluated.Strength!.IndependentFamilies);
        Assert.Equal(2, evaluated.Strength.IndependentNonProfileFamilies);
    }

    [Fact]
    public void ProfileSourcesNeverBecomeTheDurableRepresentativeId()
    {
        var pivot = Fx.Pivot("a", 100.00m, 10, 12);
        var profile = Fx.ProfileSource("node", 100.00m, 30);
        var zone = Assert.Single(ZoneBuilder.Assemble(
            [Line(100.00m, .03m, pivot), Line(100.01m, .01m, profile)], Request(30), .20, P).Zones);
        Assert.Equal(pivot.Id, zone.Id);
        Assert.False(zone.ProfileOnly);
        Assert.Contains(profile.Id, zone.SourceIds);
    }

    [Fact]
    public void ProfileOnlyZoneKeepsATemporaryIdAndIsNotEligible()
    {
        var profile = Fx.ProfileSource("node", 100.00m, 30);
        var zone = Assert.Single(ZoneBuilder.Assemble(
            [ZoneCandidate.FromBounds(99.95m, 100.05m, profile, "EstimatedVolumeProfile")], Request(30), .20, P).Zones);
        Assert.True(zone.ProfileOnly);
        Assert.NotEqual(profile.Id, zone.Id);
        Assert.Contains("ProfileOnlyTemporaryId", zone.ApproximationFlags);

        var evaluated = ZoneEvaluator.Evaluate([zone],
            ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(30), ImmutableArray<StructureBar>.Empty), P).Zones[0];
        Assert.False(evaluated.Eligible);
        Assert.Contains(ZoneEvaluator.ReasonProfileOnly, evaluated.RejectReasons);
    }

    [Fact]
    public void ZoneIdIsStableAcrossRecomputationAndIsNotReissuedPerEvaluation()
    {
        var pivot = Fx.Pivot("a", 100.00m, 10, 12);
        var first = ZoneBuilder.Assemble([Line(100.00m, .03m, pivot)], Request(30), .20, P).Zones;
        var second = ZoneBuilder.Assemble([Line(100.00m, .03m, pivot)], Request(40, first), .20, P).Zones;
        var third = ZoneBuilder.Assemble(
            [Line(100.00m, .03m, pivot), Line(100.02m, .03m, Fx.Pivot("b", 100.02m, 20, 22))],
            Request(50, second), .20, P).Zones;

        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].Id, third[0].Id);
        Assert.Equal(1, second[0].BoundsRevision);
        Assert.Equal(2, third[0].BoundsRevision);          // 경계가 실제로 바뀔 때만 증가
    }

    [Fact]
    public void MergingTwoLineagesKeepsTheEarliestIdAndPreservesTheOtherAsAnAlias()
    {
        var a = Fx.Pivot("a", 100.00m, 10, 12);
        var b = Fx.Pivot("b", 100.30m, 20, 22);
        var separate = ZoneBuilder.Assemble([Line(100.00m, .03m, a), Line(100.30m, .03m, b)], Request(30), .20, P).Zones;
        Assert.Equal(2, separate.Length);

        // 더 큰 ATR에서는 병합 허용 폭이 넓어져 두 lineage가 하나로 합쳐진다.
        var merged = Assert.Single(ZoneBuilder.Assemble(
            [Line(100.00m, .03m, a), Line(100.30m, .03m, b)], Request(40, separate), 4.0, P).Zones);
        Assert.Equal(a.Id, merged.Id);                     // FirstConfirmedAt이 이른 쪽이 대표
        Assert.Contains(b.Id, merged.Aliases);
        Assert.DoesNotContain(merged.Id, merged.Aliases);
    }

    [Fact]
    public void ZoneWithoutAnySurvivingDurableSourceIsRetired()
    {
        var stale = Fx.Pivot("stale", 100.00m, 10, 12);
        var previous = ZoneBuilder.Assemble([Line(100.00m, .03m, stale)], Request(30), .20, P).Zones;
        var next = ZoneBuilder.Build(
            ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, Fx.At(40),
                ImmutableArray<StructureBar>.Empty, ImmutableArray<StructureBar>.Empty, null, previous), P);
        Assert.Contains(previous[0].Id, next.RetiredZoneIds);
        Assert.Empty(next.Zones);
    }

    [Fact]
    public void RetiredIdsAreCarriedForwardAndStayIneligible()
    {
        var pivot = Fx.Pivot("a", 100.00m, 10, 12);
        var zone = Assert.Single(ZoneBuilder.Assemble([Line(100.00m, .03m, pivot)],
            Request(30, null, [pivot.Id]), .20, P).Zones);
        Assert.True(zone.Retired);
        var evaluated = ZoneEvaluator.Evaluate([zone],
            ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(30), ImmutableArray<StructureBar>.Empty, null, [pivot.Id]), P);
        Assert.False(evaluated.Zones[0].Eligible);
        Assert.Contains(ZoneEvaluator.ReasonRetired, evaluated.Zones[0].RejectReasons);
        Assert.Contains(pivot.Id, evaluated.RetiredZoneIds);
    }

    [Fact]
    public void ZoneOrderIsDeterministicByLowerUpperThenId()
    {
        var candidates = new[]
        {
            Line(101.00m, .03m, Fx.Pivot("c", 101.00m, 30, 32)),
            Line(100.00m, .03m, Fx.Pivot("a", 100.00m, 10, 12)),
            Line(100.50m, .03m, Fx.Pivot("b", 100.50m, 20, 22))
        };
        var forward = ZoneBuilder.Assemble(candidates, Request(40), .20, P).Zones;
        var reversed = ZoneBuilder.Assemble(candidates.Reverse(), Request(40), .20, P).Zones;
        Assert.Equal(forward.Select(x => x.Lower).ToArray(), reversed.Select(x => x.Lower).ToArray());
        Assert.Equal(forward.Select(x => x.Id).ToArray(), reversed.Select(x => x.Id).ToArray());
        Assert.Equal(new[] { 99.97m, 100.47m, 100.97m }, forward.Select(x => x.Lower).ToArray());
    }

    [Fact]
    public void WidthFallsBackToTheTickWhenNoAtrExistedAtConfirmation()
    {
        Assert.Equal(.01m, ZoneBuilder.HalfWidth(null, P));
        Assert.Equal(.01m, ZoneBuilder.HalfWidth(0, P));
        Assert.Equal(.03m, ZoneBuilder.HalfWidth(.20, P));

        var bars = Fx.Bars(
            Fx.Bar(0, 100.00m, 100.10m, 99.90m, 100.00m),
            Fx.Bar(1, 100.00m, 100.15m, 99.95m, 100.05m),
            Fx.Bar(2, 100.30m, 100.40m, 100.20m, 100.30m),      // 확정 고점 피벗 후보
            Fx.Bar(3, 100.10m, 100.20m, 100.00m, 100.10m),
            Fx.Bar(4, 100.08m, 100.18m, 99.98m, 100.08m),
            Fx.Bar(5, 100.06m, 100.16m, 99.96m, 100.06m));
        var result = ZoneBuilder.Build(ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd,
            Fx.At(6), bars, ImmutableArray<StructureBar>.Empty), P);
        Assert.Null(result.Atr1mAtCutoff);                       // 14봉 미만이면 ATR은 null이다
        var lineZones = result.Zones.Where(x => !x.ProfileOnly).ToArray();
        Assert.NotEmpty(lineZones);
        Assert.All(lineZones, z => Assert.Contains("WidthFromTickOnly", z.ApproximationFlags));
        Assert.Equal(100.39m, lineZones[0].Lower);
        Assert.Equal(100.41m, lineZones[0].Upper);
    }
}
