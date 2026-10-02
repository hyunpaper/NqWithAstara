using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §16A 정책·수치 계약. 정책 수치가 바뀌면 hash가 바뀌고, 실행 컨텍스트는 hash에 들어가지 않는다.</summary>
public sealed class StructurePolicyTests
{
    const string PolicyHashBeforeReboundLongVwapGate =
        "65281f3f5c25b48e469bd9d5c8674f8926bdb9328dd672d10d5a00d4583e39b5";

    [Fact]
    public void PolicyHashIsDeterministicForTheSamePolicy()
    {
        var a = StructurePolicy.Default;
        var b = new StructurePolicy();
        var c = StructurePolicy.Default with { };
        Assert.Equal(a.PolicyHash, b.PolicyHash);
        Assert.Equal(a.PolicyHash, c.PolicyHash);
        Assert.Equal(a.PolicyHash, a.PolicyHash);
        Assert.Equal(64, a.PolicyHash.Length);
        Assert.All(a.PolicyHash, ch => Assert.True(char.IsAsciiDigit(ch) || (ch >= 'a' && ch <= 'f')));
    }

    [Fact]
    public void EveryPolicyNumberChangeChangesTheHash()
    {
        var baseline = StructurePolicy.Default.PolicyHash;
        var variants = new[]
        {
            StructurePolicy.Default with { ZoneEligibilityStrength = .36 },
            StructurePolicy.Default with { PriceTick = .05m },
            StructurePolicy.Default with { PriceTickUnknownWarningPolls = 4 },
            StructurePolicy.Default with { ProfileMaxBins = 401 },
            StructurePolicy.Default with { ReactionWindowBars = 6 },
            StructurePolicy.Default with { MinimumNetR = 1.3 },
            StructurePolicy.Default with { RecencyTradingMinutes = 391 },
            StructurePolicy.Default with { Version = "v5-structure.2" },
            StructurePolicy.Default with { ReboundLongVwapGateVersion = "reject-positive-distance.2" },
            StructurePolicy.Default with { BreakoutConfirmationGateVersion = "hold-breakout-boundary.2" },
            StructurePolicy.Default with { AllowTransitionPullback = true },
            StructurePolicy.Default with { ObservationDailyByteLimit = 1 },
            StructurePolicy.Default with { RequirePositiveBenchmarkForRebound = true },
            StructurePolicy.Default with { RequirePullbackNearVwap = true },
            StructurePolicy.Default with { PullbackMaximumVwapDistanceAtr = 2.0 },
            StructurePolicy.Default with { RequireBreakoutNearVwap = true },
            StructurePolicy.Default with { BreakoutMaximumVwapDistanceAtr = 4.9 },
            StructurePolicy.Default with { RequireMinimumReboundEntryQuality = true },
            StructurePolicy.Default with { MinimumReboundEntryQuality = 49.9 },
            StructurePolicy.Default with { RequireBreakoutAboveVwap = true },
            StructurePolicy.Default with { RequireMaximumReboundNetR = true },
            StructurePolicy.Default with { MaximumReboundNetR = 1.8 },
            StructurePolicy.Default with { ExemptBreakoutFromPositiveBenchmarkHalfRStop = true }
        };
        var hashes = variants.Select(x => x.PolicyHash).ToArray();
        Assert.DoesNotContain(baseline, hashes);
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void CanonicalJsonIsKeySortedAndFreeOfRuntimeContext()
    {
        var json = StructurePolicy.Default.CanonicalJson;
        var keys = System.Text.RegularExpressions.Regex.Matches(json, "[{,]\"(\\w+)\":")
            .Select(x => x.Groups[1].Value).ToArray();
        Assert.Equal(keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), keys);
        Assert.DoesNotContain("PolicyHash", keys);
        Assert.DoesNotContain("CanonicalJson", keys);
        Assert.Contains("\"ZoneEligibilityStrength\":0.35", json);
        Assert.Contains("\"PriceTick\":0.01", json);
        Assert.Contains("\"PivotLeft\":2", json);
        Assert.Contains("\"AllowTransitionPullback\":false", json);
        Assert.Contains("\"RequirePullbackNearVwap\":false", json);
        Assert.Contains("\"PullbackMaximumVwapDistanceAtr\":2.1", json);
        Assert.Contains("\"RequireBreakoutNearVwap\":false", json);
        Assert.Contains("\"BreakoutMaximumVwapDistanceAtr\":5", json);
        Assert.Contains("\"RequireMinimumReboundEntryQuality\":false", json);
        Assert.Contains("\"MinimumReboundEntryQuality\":50", json);
        Assert.Contains("\"RequireBreakoutAboveVwap\":false", json);
        Assert.Contains("\"RequireMaximumReboundNetR\":false", json);
        Assert.Contains("\"MaximumReboundNetR\":1.9", json);
        Assert.DoesNotContain("2026", json);           // 실행 시각/경로가 들어가면 hash가 재현되지 않는다
    }

    [Fact]
    public void TransitionPullbackIsBlockedByDefaultAndExplicitEnablementStartsANewPolicyLineage()
    {
        var policy = StructurePolicy.Default;

        Assert.False(policy.AllowTransitionPullback);
        var enabled = policy with { AllowTransitionPullback = true };
        Assert.NotEqual(policy.PolicyHash, enabled.PolicyHash);

        var current = StructuralLatch.Empty(Fx.Symbol, Fx.SessionStart, policy.PolicyHash);
        Assert.True(current.Matches(Fx.SessionStart, policy.PolicyHash));
        Assert.False(current.Matches(Fx.SessionStart, enabled.PolicyHash));
    }

    [Fact]
    public void BreakoutConfirmationGateHasItsOwnPolicyLineage()
    {
        var policy = StructurePolicy.Default;

        Assert.Equal("hold-breakout-boundary.1", policy.BreakoutConfirmationGateVersion);
        Assert.Contains("\"BreakoutConfirmationGateVersion\":\"hold-breakout-boundary.1\"",
            policy.CanonicalJson, StringComparison.Ordinal);
        Assert.NotEqual(policy.PolicyHash,
            (policy with { BreakoutConfirmationGateVersion = "hold-breakout-boundary.2" }).PolicyHash);
    }

    [Fact]
    public void ReboundLongVwapGateHasItsOwnPolicyLineageWithoutChangingTheEngineVersion()
    {
        var policy = StructurePolicy.Default;

        Assert.Equal("reject-positive-distance.1", policy.ReboundLongVwapGateVersion);
        Assert.Contains("\"ReboundLongVwapGateVersion\":\"reject-positive-distance.1\"", policy.CanonicalJson,
            StringComparison.Ordinal);
        Assert.NotEqual(PolicyHashBeforeReboundLongVwapGate, policy.PolicyHash);
        Assert.NotEqual(policy.PolicyHash,
            (policy with { ReboundLongVwapGateVersion = "reject-positive-distance.2" }).PolicyHash);
        Assert.Equal("v5-structure.1", policy.Version);
    }

    [Fact]
    public void ReboundLongVwapPolicyHashChangeStartsANewLatchLineage()
    {
        var policy = StructurePolicy.Default;
        var previous = StructuralLatch.Empty(Fx.Symbol, Fx.SessionStart, PolicyHashBeforeReboundLongVwapGate)
            with { Seeded = true, WatermarkBarStart = Fx.At(30) };

        Assert.False(previous.Matches(Fx.SessionStart, policy.PolicyHash));
        var fresh = StructuralLatch.Empty(Fx.Symbol, Fx.SessionStart, policy.PolicyHash);
        Assert.False(fresh.Seeded);
        Assert.Null(fresh.WatermarkBarStart);
        Assert.True(fresh.Matches(Fx.SessionStart, policy.PolicyHash));
    }

    [Fact]
    public void PolicyCarriesTheDesignTableValues()
    {
        var p = StructurePolicy.Default;
        Assert.Equal(2, p.PivotLeft);
        Assert.Equal(2, p.PivotRight);
        Assert.Equal(30, p.Minimum1mBars);
        Assert.Equal(15, p.NewEntryQuoteMaxAgeSeconds);
        Assert.Equal(5, p.QuoteFutureToleranceSeconds);
        Assert.Equal(.35, p.ZoneEligibilityStrength);
        Assert.Equal(2.0, p.MaxRiskPercent);
        Assert.Equal(1.2, p.MinimumNetR);
        Assert.Equal(40, p.EntryCutoffBeforeCloseMinutes);
        Assert.Equal(5, p.CandidateTtlMinutes);
        Assert.Equal(30, p.BreakoutCooldownMinutes);
        Assert.Equal(20L * 1024 * 1024, p.ObservationDailyByteLimit);
        Assert.Equal(.01m, p.PriceTick);
        Assert.Equal(3, p.PriceTickUnknownWarningPolls);
        Assert.Equal(400, p.ProfileMaxBins);
        Assert.Equal(.15, p.ZoneHalfWidthAtrFactor);
        Assert.Equal(.5, p.ReactionAtrScale);
        Assert.Equal(390, p.RecencyTradingMinutes);
        Assert.False(p.RequirePositiveBenchmarkForRebound);
    }

    [Fact]
    public void GeometricMeanFollowsTheContract()
    {
        Assert.Equal(6, StructureMath.GeometricMean([4, 9]), 10);
        Assert.Equal(0, StructureMath.GeometricMean([0, 9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureMath.GeometricMean([-1, 9]));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureMath.GeometricMean([double.NaN, 9]));
        Assert.Throws<ArgumentException>(() => StructureMath.GeometricMean([]));
    }

    [Fact]
    public void GeometricMeanSkipsMissingComponentsAndNeverSubstitutesZero()
    {
        Assert.Equal(6, StructureMath.GeometricMeanOfAvailable([4, null, 9])!.Value, 10);
        Assert.Null(StructureMath.GeometricMeanOfAvailable([null, null]));
        // 결측을 0으로 대체하면 아래가 0이 된다. 그렇게 하지 않는다(§16A).
        Assert.NotEqual(0, StructureMath.GeometricMeanOfAvailable([4, null])!.Value);
    }

    [Theory]
    [InlineData(0, .9508, true)]
    [InlineData(1, .7784, true)]
    [InlineData(2, .6373, true)]
    [InlineData(3, .5218, true)]
    [InlineData(5, .3498, false)]
    public void BreachPenaltyIsAWeakeningStrengthComponent(int failedEpisodes, double expectedStrength, bool expectedEligible)
    {
        var confluence = 1 - Math.Exp(-3d / StructurePolicy.Default.ConfluenceScale);
        var breachPenalty = Math.Exp(-failedEpisodes);

        var strength = StructureMath.GeometricMeanOfAvailable([1, 1, 1, confluence, breachPenalty])!.Value;

        Assert.Equal(expectedStrength, strength, 4);
        Assert.Equal(expectedEligible, strength >= StructurePolicy.Default.ZoneEligibilityStrength);
    }

    [Fact]
    public void FloorToCentStaysOnTheDecimalPath()
    {
        Assert.Equal(99.12m, StructureMath.FloorToCent(99.129m));
        Assert.Equal(99.12m, StructureMath.FloorToCent(99.15m - 0.03m));    // 설계 예시 A
        Assert.Equal(101.78m, StructureMath.FloorToCent(101.80m - 0.02m));  // 설계 예시 A 목표
        Assert.Equal(-0.02m, StructureMath.FloorToCent(-0.011m));
        Assert.Equal(.03m, StructureMath.RoundToCent(.0300000000000017m));
    }

    [Fact]
    public void IndicatorToPriceConversionRejectsNonFiniteAndNegative()
    {
        Assert.Null(StructureMath.ToPriceDelta(double.NaN));
        Assert.Null(StructureMath.ToPriceDelta(double.PositiveInfinity));
        Assert.Null(StructureMath.ToPriceDelta(-1));
        Assert.Null(StructureMath.ToPriceDelta(null));
        Assert.Equal(.2m, StructureMath.ToPriceDelta(.2));
        Assert.Equal(.03m, StructureMath.ScaledFloor(.01m, .15, .2));
        Assert.Equal(.01m, StructureMath.ScaledFloor(.01m, .15, null));
        Assert.Equal(.01m, StructureMath.ScaledFloor(.01m, .15, 0));
        Assert.Equal(.01m, StructureMath.ScaledFloor(.01m, .15, double.NaN));
    }

    [Fact]
    public void SourceIdIsAStableHashRatherThanAPriceString()
    {
        var id = StructureMath.SourceId("pivot", "TEST", "2026-09-09", "1m", "H", "2026-09-09T14:00:00.0000000Z");
        Assert.Equal(64, id.Length);
        Assert.DoesNotContain("2026", id);   // 가격/시각 문자열 자체를 ID로 쓰지 않는다(§6.3)
        Assert.Equal(id, StructureMath.SourceId("pivot", "TEST", "2026-09-09", "1m", "H", "2026-09-09T14:00:00.0000000Z"));
        Assert.NotEqual(id, StructureMath.SourceId("pivot", "TEST", "2026-09-09", "1m", "L", "2026-09-09T14:00:00.0000000Z"));
    }
}
