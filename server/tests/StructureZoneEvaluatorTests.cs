using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §6.4~§6.5 및 §16B. episode 규칙, 역할 전이, 강도 공식, 구조 사용 자격, 숫자 fixture.</summary>
public sealed class StructureZoneEvaluatorTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    /// <summary>ATR을 0.20으로 수렴시키는 워밍업 14봉. 구간 [99.20,99.40]과 겹치지 않는다.</summary>
    static List<StructureBar> Warmup(int count = 14) =>
        Enumerable.Range(0, count).Select(i => Fx.Steady(i, 99.45m, 99.65m, 99.55m)).ToList();

    static PriceZone Support(int confirmedMinute = 14) =>
        Fx.Zone(99.20m, 99.40m, Fx.Pivot("support", 99.30m, confirmedMinute - 2, confirmedMinute));

    // ── episode 규칙 ──

    [Fact]
    public void ConsecutiveTouchingBarsAreASingleEpisode()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        bars.Add(Fx.Steady(15, 99.35m, 99.55m, 99.40m));
        bars.Add(Fx.Steady(16, 99.35m, 99.55m, 99.40m));
        bars.Add(Fx.Steady(17, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(18, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(19, 99.45m, 99.65m, 99.55m));

        var result = Fx.Evaluate(Support(), bars.ToImmutableArray(), 20);
        Assert.Single(result.Episodes);
        Assert.Equal(Fx.At(14), result.Episodes[0].StartAt);
        Assert.Equal(1, result.Zones[0].Strength!.CompletedEpisodes);
    }

    [Fact]
    public void RetouchAfterOnlyOneOutsideBarStaysInTheSameEpisode()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.40m));      // 접촉
        bars.Add(Fx.Steady(15, 99.45m, 99.65m, 99.50m));      // 완전 이탈 1봉
        bars.Add(Fx.Steady(16, 99.35m, 99.55m, 99.40m));      // 재접촉
        bars.Add(Fx.Steady(17, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(18, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(19, 99.45m, 99.65m, 99.55m));

        Assert.Single(Fx.Evaluate(Support(), bars.ToImmutableArray(), 20).Episodes);
    }

    [Fact]
    public void RetouchAfterTwoOutsideBarsIsAnIndependentEpisode()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.40m));
        bars.Add(Fx.Steady(15, 99.45m, 99.65m, 99.50m));
        bars.Add(Fx.Steady(16, 99.45m, 99.65m, 99.50m));      // 완전 이탈 2봉
        bars.Add(Fx.Steady(17, 99.35m, 99.55m, 99.40m));      // 새 episode
        bars.Add(Fx.Steady(18, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(19, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(20, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(21, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(22, 99.45m, 99.65m, 99.55m));

        var result = Fx.Evaluate(Support(), bars.ToImmutableArray(), 23);
        Assert.Equal(2, result.Episodes.Length);
        Assert.Equal(new[] { Fx.At(14), Fx.At(17) }, result.Episodes.Select(x => x.StartAt).ToArray());
    }

    [Fact]
    public void FormationBarsAreNotCountedAsARetouchEpisode()
    {
        // 피벗을 만든 봉과 우측 확인 봉은 구간과 겹치지만 재접촉 episode가 아니다(§16A/§16B).
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.30m, 99.50m, 99.35m));      // 형성 봉 (확정 At(17))
        bars.Add(Fx.Steady(15, 99.32m, 99.52m, 99.40m));
        bars.Add(Fx.Steady(16, 99.34m, 99.54m, 99.45m));
        bars.Add(Fx.Steady(17, 99.45m, 99.65m, 99.55m));
        bars.Add(Fx.Steady(18, 99.45m, 99.65m, 99.55m));

        var zone = Fx.Zone(99.20m, 99.40m, Fx.Pivot("late", 99.30m, 14, 17));
        var result = Fx.Evaluate(zone, bars.ToImmutableArray(), 19);
        Assert.Empty(result.Episodes);
        Assert.Null(result.Zones[0].Strength!.TouchEvidence);
        Assert.Contains("touchEvidence", result.Zones[0].Strength!.MissingComponents);
    }

    [Fact]
    public void PendingEpisodeIsExcludedFromTouchEvidence()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.40m));
        bars.Add(Fx.Steady(15, 99.35m, 99.55m, 99.40m));
        bars.Add(Fx.Steady(16, 99.35m, 99.55m, 99.40m));

        var result = Fx.Evaluate(Support(), bars.ToImmutableArray(), 17);
        var episode = Assert.Single(result.Episodes);
        Assert.Equal(EpisodeOutcome.Pending, episode.Outcome);
        var strength = result.Zones[0].Strength!;
        Assert.Equal(0, strength.CompletedEpisodes);
        Assert.Equal(1, strength.PendingEpisodes);
        Assert.Null(strength.TouchEvidence);
        Assert.Null(strength.ReactionEvidence);
    }

    [Fact]
    public void NeutralEpisodeCountsAsCompletedButNotAsAReaction()
    {
        var bars = Warmup();
        for (var i = 14; i <= 19; i++) bars.Add(Fx.Steady(i, 99.35m, 99.44m, 99.40m));
        var result = Fx.Evaluate(Support(), bars.ToImmutableArray(), 20);
        var episode = Assert.Single(result.Episodes);
        Assert.Equal(EpisodeOutcome.Neutral, episode.Outcome);
        var strength = result.Zones[0].Strength!;
        Assert.Equal(1, strength.CompletedEpisodes);
        Assert.Equal(0, strength.SuccessEpisodes);
        Assert.Equal(1 - Math.Exp(-.5), strength.TouchEvidence!.Value, 10);
        Assert.Null(strength.ReactionEvidence);              // neutral을 실패나 성공으로 꾸며내지 않는다
        Assert.Equal(1, strength.BreachPenalty);
    }

    [Fact]
    public void FailedHoldLowersStrengthComparedWithASuccessfulHold()
    {
        var held = Warmup();
        held.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        held.Add(Fx.Steady(15, 99.45m, 99.60m, 99.50m));
        var success = Fx.Evaluate(Support(), held.ToImmutableArray(), 16).Zones[0];

        var broke = Warmup();
        broke.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        broke.Add(Fx.Steady(15, 99.05m, 99.25m, 99.10m));    // 지지 하단 아래 마감
        var failed = Fx.Evaluate(Support(), broke.ToImmutableArray(), 16).Zones[0];

        Assert.Equal(EpisodeOutcome.Success, Fx.Evaluate(Support(), held.ToImmutableArray(), 16).Episodes[0].Outcome);
        Assert.Equal(1, success.Strength!.SuccessEpisodes);
        Assert.Equal(1, failed.Strength!.FailedEpisodes);
        Assert.Equal(Math.Exp(-1), failed.Strength.BreachPenalty, 10);
        Assert.True(failed.Strength.Value < success.Strength.Value,
            $"failed={failed.Strength.Value} success={success.Strength.Value}");
        Assert.Equal(ZoneRole.Broken, failed.Role);
        Assert.False(failed.Eligible);
        Assert.Contains(ZoneEvaluator.ReasonBroken, failed.RejectReasons);
    }

    [Fact]
    public void ManyTouchesDoNotOutweighRepeatedFailures()
    {
        var bars = Warmup();
        var minute = 14;
        for (var round = 0; round < 3; round++)
        {
            bars.Add(Fx.Steady(minute++, 99.35m, 99.55m, 99.40m));       // 접촉
            bars.Add(Fx.Steady(minute++, 99.05m, 99.25m, 99.10m));       // 실패
            bars.Add(Fx.Steady(minute++, 99.45m, 99.65m, 99.55m));
            bars.Add(Fx.Steady(minute++, 99.45m, 99.65m, 99.55m));
        }
        for (var i = 0; i < 5; i++) bars.Add(Fx.Steady(minute++, 99.45m, 99.65m, 99.55m));

        var zone = Fx.Evaluate(Support(), bars.ToImmutableArray(), minute).Zones[0];
        Assert.Equal(3, zone.Strength!.CompletedEpisodes);
        Assert.Equal(3, zone.Strength.FailedEpisodes);
        Assert.Equal(Math.Exp(-3), zone.Strength.BreachPenalty, 10);
        Assert.True(zone.Strength.Value < .1, $"strength={zone.Strength.Value}");
        Assert.False(zone.Eligible);
    }

    // ── 역할 전이 (§16B) ──

    [Fact]
    public void EqualCloseAtTheBoundaryIsNotABreach()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.10m, 99.55m, 99.20m));      // Close == Lower
        bars.Add(Fx.Steady(15, 99.10m, 99.55m, 99.20m));
        var zone = Fx.Evaluate(Support(), bars.ToImmutableArray(), 16).Zones[0];
        Assert.Equal(ZoneRole.Support, zone.Role);
        Assert.Empty(zone.RoleHistory);

        var resistance = Fx.Zone(99.80m, 100.00m, Fx.Pivot("res", 99.90m, 12, 14));
        var above = Warmup();
        above.Add(Fx.Steady(14, 99.90m, 100.10m, 100.00m));   // Close == Upper
        var evaluated = Fx.Evaluate(resistance, above.ToImmutableArray(), 15).Zones[0];
        Assert.Equal(ZoneRole.Resistance, evaluated.OriginalRole);
        Assert.Equal(ZoneRole.Resistance, evaluated.Role);
    }

    [Fact]
    public void UpwardBreakoutNeedsASeparateRetestBeforeItBecomesFlippedSupport()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.90m, 100.10m, 100.05m));    // 상향 돌파 마감
        bars.Add(Fx.Steady(15, 99.95m, 100.08m, 99.98m));     // 재접촉 · 구간 안 마감
        bars.Add(Fx.Steady(16, 99.95m, 100.15m, 100.10m));    // 재접촉 후 위에서 마감
        var zone = Fx.Zone(99.80m, 100.00m, Fx.Pivot("res", 99.90m, 12, 14));

        Assert.Equal(ZoneRole.Broken, Fx.Evaluate(zone, bars.ToImmutableArray(), 15).Zones[0].Role);
        Assert.Equal(ZoneRole.Unresolved, Fx.Evaluate(zone, bars.ToImmutableArray(), 16).Zones[0].Role);
        var flipped = Fx.Evaluate(zone, bars.ToImmutableArray(), 17).Zones[0];
        Assert.Equal(ZoneRole.FlippedSupport, flipped.Role);
        Assert.Contains(flipped.RoleHistory, x => x.To == ZoneRole.FlippedSupport && x.At == Fx.At(17));
    }

    [Fact]
    public void PriceBeingAboveALineIsNotEnoughToFlipTheRole()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.90m, 100.10m, 100.05m));    // 돌파
        bars.Add(Fx.Steady(15, 100.30m, 100.50m, 100.40m));   // 구간과 겹치지 않고 위에 머문다
        bars.Add(Fx.Steady(16, 100.30m, 100.50m, 100.40m));
        var zone = Fx.Zone(99.80m, 100.00m, Fx.Pivot("res", 99.90m, 12, 14));
        var evaluated = Fx.Evaluate(zone, bars.ToImmutableArray(), 17).Zones[0];
        Assert.Equal(ZoneRole.Broken, evaluated.Role);
        Assert.DoesNotContain(evaluated.RoleHistory, x => x.To is ZoneRole.FlippedSupport or ZoneRole.Support);
    }

    [Fact]
    public void BrokenSupportNeverReturnsToItsOriginalRole()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.05m, 99.25m, 99.10m));      // 지지 이탈
        bars.Add(Fx.Steady(15, 99.30m, 99.55m, 99.50m));      // 구간을 되찾고 위에서 마감
        bars.Add(Fx.Steady(16, 99.45m, 99.65m, 99.55m));
        var evaluated = Fx.Evaluate(Support(), bars.ToImmutableArray(), 17).Zones[0];
        Assert.Equal(ZoneRole.Broken, evaluated.Role);
        Assert.DoesNotContain(evaluated.RoleHistory, x => x.To == ZoneRole.Support);
        Assert.False(evaluated.Eligible);
    }

    [Fact]
    public void FlippedRoleThatBreaksAgainIsRetired()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.90m, 100.10m, 100.05m));    // 상향 돌파
        bars.Add(Fx.Steady(15, 99.95m, 100.15m, 100.10m));    // retest 후 위에서 마감 → FLIPPED_SUPPORT
        bars.Add(Fx.Steady(16, 99.50m, 99.75m, 99.60m));      // 다시 하향 이탈
        var zone = Fx.Zone(99.80m, 100.00m, Fx.Pivot("res", 99.90m, 12, 14));
        var result = ZoneEvaluator.Evaluate([zone],
            ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(17), bars.ToImmutableArray()), P);
        Assert.Equal(ZoneRole.Broken, result.Zones[0].Role);
        Assert.True(result.Zones[0].Retired);
        Assert.Contains(zone.Id, result.RetiredZoneIds);
        Assert.Contains(ZoneEvaluator.ReasonRetired, result.Zones[0].RejectReasons);
    }

    // ── §16B 숫자 fixture ──

    [Fact]
    public void FreshPivotOnlyZoneScores06273AndIsNotEligible()
    {
        var bars = Enumerable.Range(0, 5).Select(i => Fx.Steady(i, 99.45m, 99.65m, 99.55m)).ToImmutableArray();
        var zone = Fx.Zone(99.20m, 99.40m, Fx.Pivot("p", 99.30m, 3, 5));
        var evaluated = Fx.Evaluate(zone, bars, 5).Zones[0];
        var strength = evaluated.Strength!;

        Assert.Equal(1, strength.Recency!.Value, 10);
        Assert.Equal(.3935, strength.Confluence!.Value, 4);
        Assert.Null(strength.TouchEvidence);
        Assert.Null(strength.ReactionEvidence);
        Assert.Equal(.6273, strength.Value!.Value, 4);
        Assert.Equal(ZoneRole.Support, evaluated.Role);
        Assert.False(evaluated.Eligible);
        Assert.Equal(new[] { ZoneEvaluator.ReasonEvidence }, evaluated.RejectReasons.ToArray());
    }

    [Fact]
    public void PivotPlusDailyZoneScores07951AndIsEligibleOnTwoFamilies()
    {
        var bars = Enumerable.Range(0, 5).Select(i => Fx.Steady(i, 99.45m, 99.65m, 99.55m)).ToImmutableArray();
        var zone = Fx.Zone(99.20m, 99.40m, Fx.Pivot("p", 99.30m, 3, 5), Fx.Daily("prev-L", 99.30m));
        var evaluated = Fx.Evaluate(zone, bars, 5).Zones[0];
        var strength = evaluated.Strength!;

        Assert.Equal(1, strength.Recency!.Value, 10);
        Assert.Equal(.6321, strength.Confluence!.Value, 4);
        Assert.Equal(.7951, strength.Value!.Value, 4);
        Assert.Equal(2, strength.IndependentNonProfileFamilies);
        Assert.Equal(ZoneRole.Support, evaluated.Role);
        Assert.True(evaluated.Eligible);
        Assert.Empty(evaluated.RejectReasons);
    }

    [Fact]
    public void PivotOnlyWithOneSuccessfulOneAtrReactionScores05172AndIsEligible()
    {
        // recency=1인 "직후" 조건을 유지하려면 경과 시간을 무시하는 척도가 필요하다. 나머지 요소는 기본 정책과 같다.
        var policy = P with { RecencyTradingMinutes = 1e9 };
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));      // 접촉 · ATR 0.20
        bars.Add(Fx.Steady(15, 99.45m, 99.60m, 99.50m));      // 성공 · 유리 excursion 1 ATR
        var result = Fx.Evaluate(Support(), bars.ToImmutableArray(), 16, policy);
        var strength = result.Zones[0].Strength!;
        var episode = Assert.Single(result.Episodes);

        Assert.Equal(EpisodeOutcome.Success, episode.Outcome);
        Assert.Equal(.20, episode.AtrAtTouch!.Value, 6);
        Assert.Equal(1.0, episode.FavorableExcursionAtr!.Value, 6);
        Assert.Equal(.3935, strength.TouchEvidence!.Value, 4);
        Assert.Equal(.4621, strength.ReactionEvidence!.Value, 4);
        Assert.Equal(.3935, strength.Confluence!.Value, 4);
        Assert.Equal(1, strength.Recency!.Value, 6);
        Assert.Equal(.5172, strength.Value!.Value, 4);
        Assert.True(result.Zones[0].Eligible);
    }

    [Fact]
    public void EligibilityRequiresStrengthAtLeastThePolicyMinimum()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        bars.Add(Fx.Steady(15, 99.45m, 99.60m, 99.50m));
        var strict = P with { ZoneEligibilityStrength = .9 };
        var evaluated = Fx.Evaluate(Support(), bars.ToImmutableArray(), 16, strict).Zones[0];
        Assert.False(evaluated.Eligible);
        Assert.Contains(ZoneEvaluator.ReasonStrength, evaluated.RejectReasons);
    }

    // ── 수치 안전성·결정성·미래 누출 ──

    [Fact]
    public void StrengthNeverProducesNaNOrDividesByZero()
    {
        var flat = Enumerable.Range(0, 25)
            .Select(i => new StructureBar(Fx.At(i), Fx.At(i + 1), 99.30m, 99.30m, 99.30m, 99.30m, 0))
            .ToImmutableArray();
        var zone = Fx.Zone(99.30m, 99.30m, Fx.Pivot("p", 99.30m, 3, 5));   // 폭 0 · ATR 0 · 거래량 0
        var result = Fx.Evaluate(zone, flat, 25);
        var strength = result.Zones[0].Strength!;

        foreach (var component in new[] { strength.TouchEvidence, strength.ReactionEvidence, strength.Recency, strength.Confluence, strength.Value })
            Assert.True(component is null || double.IsFinite(component.Value), $"non-finite component {component}");
        Assert.True(double.IsFinite(strength.BreachPenalty));
        Assert.All(result.Episodes, e => Assert.True(e.FavorableExcursionAtr is null || double.IsFinite(e.FavorableExcursionAtr.Value)));
        Assert.Contains(result.Episodes, e => e.Notes.Contains("ATR_MISSING_AT_TOUCH") || e.AtrAtTouch is not null);
    }

    [Fact]
    public void IdenticalInputProducesIdenticalOutput()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        bars.Add(Fx.Steady(15, 99.45m, 99.60m, 99.50m));
        var input = bars.ToImmutableArray();

        var a = Fx.Evaluate(Support(), input, 16);
        var b = Fx.Evaluate(Support(), input, 16);
        Assert.Equal(a.Zones.Select(x => x.Fingerprint()).ToArray(), b.Zones.Select(x => x.Fingerprint()).ToArray());
        Assert.Equal(a.Episodes.Select(x => x.Fingerprint()).ToArray(), b.Episodes.Select(x => x.Fingerprint()).ToArray());
        Assert.Equal(a.Zones[0].Strength!.Value, b.Zones[0].Strength!.Value);
    }

    [Fact]
    public void FutureBarsAfterTheCutoffChangeNothing()
    {
        var upTo16 = Warmup();
        upTo16.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        upTo16.Add(Fx.Steady(15, 99.45m, 99.60m, 99.50m));

        var withFuture = upTo16.ToList();
        for (var i = 16; i < 40; i++) withFuture.Add(Fx.Steady(i, 98.00m, 98.20m, 98.10m));   // cutoff 이후 붕괴

        var atCutoff = Fx.Evaluate(Support(), upTo16.ToImmutableArray(), 16);
        var truncated = Fx.Evaluate(Support(), withFuture.ToImmutableArray(), 16);
        Assert.Equal(atCutoff.Zones.Select(x => x.Fingerprint()).ToArray(), truncated.Zones.Select(x => x.Fingerprint()).ToArray());
        Assert.Equal(atCutoff.Episodes.Select(x => x.Fingerprint()).ToArray(), truncated.Episodes.Select(x => x.Fingerprint()).ToArray());
        Assert.Equal(ZoneRole.Support, truncated.Zones[0].Role);
    }

    [Fact]
    public void SnapshotRevisionIncrementsOnlyWhenPublishedContentChanges()
    {
        var bars = Warmup();
        bars.Add(Fx.Steady(14, 99.35m, 99.55m, 99.45m));
        bars.Add(Fx.Steady(15, 99.45m, 99.60m, 99.50m));
        var input = bars.ToImmutableArray();

        var first = Fx.Evaluate(Support(), input, 16).Zones;
        var again = ZoneEvaluator.Evaluate(first, ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(16), input, first), P).Zones;
        Assert.Equal(first[0].SnapshotRevision, again[0].SnapshotRevision);

        var later = bars.ToList();
        later.Add(Fx.Steady(16, 99.05m, 99.25m, 99.10m));
        var changed = ZoneEvaluator.Evaluate(first,
            ZoneEvaluationRequest.Create(Fx.SessionStart, Fx.At(17), later.ToImmutableArray(), first), P).Zones;
        Assert.Equal(ZoneRole.Broken, changed[0].Role);
        Assert.Equal(first[0].SnapshotRevision + 1, changed[0].SnapshotRevision);
        Assert.Equal(first[0].BoundsRevision, changed[0].BoundsRevision);
    }
}
