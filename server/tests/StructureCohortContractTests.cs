using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>이슈 #65 — 코호트 오염·저장 계약 회귀.</summary>
public sealed class StructureCohortContractTests
{
    static readonly StructurePolicy P = StructurePolicy.Default with { ReboundMaxTrendAlignment = null, WindowBlockStartMinutesFromOpen = null, WindowBlockEndMinutesFromOpen = null, AdaptiveMinimumTargetFeeMultiple = null, MaxFeeToRiskRatio = null, ReboundMinRelativeStrengthPercent = null, MinDistanceToRecentHighPercent = null, RejectHigherHighHigherLow = null, HigherHighPivotK = null, HigherHighStructureWindowBars = null };
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const int TriggerMinute = 30;

    static StructureBar Bar(int minute, decimal low, decimal high, decimal open, decimal close, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), open, high, low, close, volume);

    static ImmutableArray<StructureBar> PullbackBars()
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

    static SetupDetectionRequest Request(ImmutableArray<PriceZone> zones, decimal? live = 100.00m,
        TrendState state = TrendState.Range)
    {
        var analysisAsOf = Fx.At(TriggerMinute + 1);
        var trend = D2.Trend(state, 40, structureDirection: null,
            readyBlockers: TrendEvaluator.BlockerMissing5mStructure);
        return SetupDetectionRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, analysisAsOf, analysisAsOf,
            PullbackBars(), zones, [D2.Episode("support-zone", 25, 28)], trend, .20, live, analysisAsOf,
            D2.Quote(99.99m, 100.01m, TriggerMinute + 1));
    }

    // ── ① V5_READY_WITHOUT_5M_STRUCTURE는 READY에 도달한 후보에만 붙는다 ──

    [Fact]
    public void TheStructureCohortNoteIsAttachedWhenTheReboundActuallyReachesReady()
    {
        var result = SetupDetector.Detect(
            Request([D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)]), P);

        var rebound = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Ready, rebound.Disposition);
        Assert.Contains(SetupDetector.NoteReadyWithout5mStructure, rebound.Notes);
    }

    [Fact]
    public void ARejectedReboundNeverCarriesTheStructureCohortNote()
    {
        var result = SetupDetector.Detect(Request([D2.Support(99.20m, 99.40m)]), P);

        var rebound = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Rejected, rebound.Disposition);
        Assert.Contains(StructuralPlanner.NoTargetStructure, rebound.RejectionCodes);
        Assert.DoesNotContain(TrendEvaluator.BlockerMissing5mStructure, rebound.RejectionCodes);
        Assert.DoesNotContain(SetupDetector.NoteReadyWithout5mStructure, rebound.Notes);
    }

    [Fact]
    public void AnInvalidatedReboundNeverCarriesTheStructureCohortNote()
    {
        var result = SetupDetector.Detect(
            Request([D2.Support(99.20m, 99.40m), D2.Resistance(101.80m, 102.10m)], live: 99.00m), P);

        var rebound = result.Candidates.Single(x => x.Kind == SetupKind.Rebound);
        Assert.Equal(CandidateDisposition.Invalidated, rebound.Disposition);
        Assert.DoesNotContain(SetupDetector.NoteReadyWithout5mStructure, rebound.Notes);
    }

    [Fact]
    public void TheStructureWaiverStillKeepsTheBlockerOutOfTheRejectionCodes()
    {
        var result = SetupDetector.Detect(Request([D2.Support(99.20m, 99.40m)], state: TrendState.Up), P);

        var pullback = result.Candidates.Single(x => x.Kind == SetupKind.Pullback);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, pullback.RejectionCodes);
        Assert.DoesNotContain(SetupDetector.NoteReadyWithout5mStructure, pullback.Notes);
    }

    // ── ② §11 Zone 저장 계약: lineage를 관측만으로 재구성할 수 있다 ──

    static PriceZone LineageZone()
    {
        var assembled = Assert.Single(ZoneBuilder.Assemble(
        [
            ZoneCandidate.FromLevel(100.00m, .03m, Fx.Pivot("lineage-1m", 100.00m, 10, 12)),
            ZoneCandidate.FromLevel(100.01m, .03m, Fx.Daily("lineage-prev-L", 100.01m))
        ], ZoneBuildRequest.Create(Fx.Symbol, Fx.SessionStart, Fx.SessionEnd, Fx.At(30),
            ImmutableArray<StructureBar>.Empty, ImmutableArray<StructureBar>.Empty, null, null, null),
            .20, P).Zones);

        return assembled with
        {
            Role = ZoneRole.Broken,
            OriginalRole = ZoneRole.Support,
            RoleHistory =
            [
                new ZoneRoleChange(Fx.At(8), ZoneRole.Unresolved, ZoneRole.Support, "FIRST_HOLD"),
                new ZoneRoleChange(Fx.At(40), ZoneRole.Support, ZoneRole.Broken, "CLOSE_BELOW_LOWER")
            ]
        };
    }

    [Fact]
    public void TheZoneDtoCarriesTheSourceIdsOfTheDesignStorageContract()
    {
        var zone = LineageZone();
        var dto = StructureViewMapper.Zone(zone);

        Assert.Equal(zone.SourceIds.ToArray(), dto.SourceIds);
        Assert.Equal(zone.Sources.Length, dto.SourceCount);
    }

    [Fact]
    public void TheZoneDtoCarriesEveryEvidenceGroupWithItsMembers()
    {
        var zone = LineageZone();
        var dto = StructureViewMapper.Zone(zone);

        Assert.Equal(2, dto.EvidenceGroups.Length);
        Assert.Equal(zone.EvidenceGroups.Select(x => x.Family.ToString()).ToArray(),
            dto.EvidenceGroups.Select(x => x.Family).ToArray());
        Assert.Equal(zone.EvidenceGroups.SelectMany(x => x.SourceIds).ToArray(),
            dto.EvidenceGroups.SelectMany(x => x.SourceIds).ToArray());
        Assert.All(dto.EvidenceGroups, group => Assert.Contains(group.SourceIds, dto.SourceIds.Contains));
    }

    [Fact]
    public void TheEvidenceGroupKeyIsRecomputableFromTheFieldsThatAreStored()
    {
        var zone = LineageZone();
        var dto = StructureViewMapper.Zone(zone);

        var recomputed = dto.EvidenceGroups
            .Select(x => StructureMath.SourceId("evidence", x.Family, StructureMath.Iso(x.From),
                StructureMath.Iso(x.To)))
            .ToArray();
        Assert.Equal(zone.EvidenceGroups.Select(x => x.Key).ToArray(), recomputed);
    }

    [Fact]
    public void TheRoleHistoryExplainsWhenAndWhyTheZoneBecameBroken()
    {
        var dto = StructureViewMapper.Zone(LineageZone());

        var broken = dto.RoleHistory.Single(x => x.To == "BROKEN");
        Assert.Equal("SUPPORT", broken.From);
        Assert.Equal(Fx.At(40), broken.At);
        Assert.Equal("CLOSE_BELOW_LOWER", broken.Reason);
    }

    [Fact]
    public void ZoneLineageSurvivesTheObservationJsonRoundTrip()
    {
        var dto = StructureViewMapper.Zone(LineageZone());
        var restored = JsonSerializer.Deserialize<StructureZoneDto>(JsonSerializer.Serialize(dto, Json), Json);

        Assert.NotNull(restored);
        Assert.Equal(dto.SourceIds, restored.SourceIds);
        Assert.Equal(dto.RoleHistory, restored.RoleHistory);
        Assert.Equal(dto.EvidenceGroups.Select(x => string.Join(',', x.SourceIds)).ToArray(),
            restored.EvidenceGroups.Select(x => string.Join(',', x.SourceIds)).ToArray());
    }

    // ── ③ 프로파일 실패 원인은 사후에 구분된다 ──

    [Fact]
    public void AZeroVolumeProfileReportsZeroInputVolumeRatherThanHidingTheCause()
    {
        var bars = Fx.Bars(
            new StructureBar(Fx.At(0), Fx.At(1), 100m, 100.5m, 99.5m, 100m, 0),
            new StructureBar(Fx.At(1), Fx.At(2), 100m, 100.5m, 99.5m, 100m, 0));
        var profile = ZoneBuilder.BuildVolumeProfile(bars, .20, P);

        Assert.Contains("ZERO_VOLUME_PROFILE", profile.Warnings);
        Assert.Equal(0, profile.InputVolume);
        Assert.False(profile.Coarsened);
        Assert.DoesNotContain("CoarsenedProfile", profile.Warnings);
    }

    [Fact]
    public void ACoarsenedEmptyProfileFlagsCoarseningOnTheFieldAndNotOnlyInTheWarning()
    {
        var bars = Fx.Bars(
            new StructureBar(Fx.At(0), Fx.At(1), 100m, 100m, 100m, 100m, 0),
            new StructureBar(Fx.At(1), Fx.At(2), 100m, 140m, 100m, 140m, 0));
        var profile = ZoneBuilder.BuildVolumeProfile(bars, null, P);

        Assert.Contains("ZERO_VOLUME_PROFILE", profile.Warnings);
        Assert.Contains("CoarsenedProfile", profile.Warnings);
        Assert.True(profile.Coarsened);
        Assert.True(profile.BinWidthMultiple > 1);
        Assert.Equal(P.PriceTick * profile.BinWidthMultiple, profile.BinWidth);
    }

    [Fact]
    public void AnEmptyProfileNeverOverwritesTheRealInputVolumeWithZero()
    {
        var profile = VolumeProfile.Empty(.25m, 4, 12345.5, true, "PROFILE_BIN_LIMIT_UNRESOLVED");

        Assert.Equal(12345.5, profile.InputVolume);
        Assert.Equal(0, profile.AllocatedVolume);
        Assert.True(profile.Coarsened);
        Assert.Equal(4, profile.BinWidthMultiple);
        Assert.Empty(profile.Bins);
    }

    [Fact]
    public void TheTwoEmptyProfileCausesAreDistinguishableAfterTheFact()
    {
        var noVolume = VolumeProfile.Empty(.25m, 1, 0, false, "ZERO_VOLUME_PROFILE");
        var binLimit = VolumeProfile.Empty(.25m, 8, 99000, true, "PROFILE_BIN_LIMIT_UNRESOLVED");

        Assert.Empty(noVolume.Bins);
        Assert.Empty(binLimit.Bins);
        Assert.NotEqual(noVolume.InputVolume, binLimit.InputVolume);
        Assert.NotEqual(noVolume.Coarsened, binLimit.Coarsened);
    }

    // ── ④ structureDirection의 두 원값은 합으로 뭉개지지 않는다 ──

    static ImmutableArray<StructureBar> FromBuckets(IReadOnlyList<decimal> centers)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var k = 0; k < centers.Count; k++)
            for (var i = 0; i < 5; i++)
            {
                var minute = k * 5 + i;
                bars.Add(Fx.Bar(minute, centers[k], centers[k] + .10m, centers[k] - .10m, centers[k]));
            }
        return bars.ToImmutable();
    }

    static TrendAssessment Trend(ImmutableArray<StructureBar> bars, DateTimeOffset cutoff) =>
        TrendEvaluator.Evaluate(TrendRequest.Create(Fx.Symbol, Fx.SessionStart, cutoff, bars,
            BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, P)), P);

    static readonly decimal[] RisingHighsFallingLows =
        [100.0m, 100.2m, 100.9m, 100.3m, 100.0m, 100.5m, 101.2m, 100.6m, 99.6m, 100.2m, 100.4m];

    [Fact]
    public void EachStructureDeltaIsStoredAsItsOwnRawValue()
    {
        var bars = FromBuckets(RisingHighsFallingLows);
        var trend = Trend(bars, Fx.At(55));
        Assert.NotNull(trend.StructureDirection);

        var high = trend.Components.Single(x => x.Name == "structureDeltaHigh");
        var low = trend.Components.Single(x => x.Name == "structureDeltaLow");

        Assert.NotNull(high.Raw);
        Assert.NotNull(low.Raw);
        Assert.True(high.Raw > 0, $"deltaHigh={high.Raw}");
        Assert.True(low.Raw < 0, $"deltaLow={low.Raw}");
        Assert.Equal(Math.Tanh(high.Raw!.Value), high.Value!.Value, 12);
        Assert.Equal(Math.Tanh(low.Raw!.Value), low.Value!.Value, 12);
    }

    [Fact]
    public void StructureDirectionIsRecomputableFromTheStoredDeltas()
    {
        var trend = Trend(FromBuckets(RisingHighsFallingLows), Fx.At(55));

        var high = trend.Components.Single(x => x.Name == "structureDeltaHigh").Raw!.Value;
        var low = trend.Components.Single(x => x.Name == "structureDeltaLow").Raw!.Value;
        Assert.Equal((Math.Tanh(high) + Math.Tanh(low)) / 2, trend.StructureDirection!.Value, 12);
    }

    [Fact]
    public void StructureDirectionKeepsNoCollapsedSumAsItsRawValue()
    {
        var trend = Trend(FromBuckets(RisingHighsFallingLows), Fx.At(55));

        var direction = trend.Components.Single(x => x.Name == "structureDirection");
        Assert.Null(direction.Raw);
        Assert.Equal(trend.StructureDirection, direction.Value);
    }

    [Fact]
    public void MissingFiveMinuteStructureLeavesBothDeltasMissingRatherThanZero()
    {
        var trend = Trend(FromBuckets([100.0m, 100.1m, 100.2m, 100.3m, 100.4m, 100.5m, 100.6m]), Fx.At(35));
        Assert.Null(trend.StructureDirection);

        foreach (var name in new[] { "structureDirection", "structureDeltaHigh", "structureDeltaLow" })
        {
            var component = trend.Components.Single(x => x.Name == name);
            Assert.Null(component.Raw);
            Assert.Null(component.Value);
        }
    }
}
