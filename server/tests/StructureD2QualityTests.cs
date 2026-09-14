using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §9.4 + §16B EntryQuality. 순위 지표이며 확률이 아니다.
/// 필수 요소 결측은 점수 null·READY 금지, 필수 0은 점수 0·READY 금지다.
/// </summary>
public sealed class StructureD2QualityTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static EntryQualityInput Input(SetupKind kind = SetupKind.Pullback, double? invalidation = .6,
        double? target = .7, decimal? netR = 1.4m, double? rv = 2, double? signedTrend = 40,
        decimal entry = 100m, decimal? anchor = 99.15m, double? atr = .2, decimal? triggerClose = 99.75m,
        decimal? supportUpper = 99.40m) =>
        new(kind, invalidation, target, netR, entry, anchor, atr, rv, signedTrend, triggerClose, supportUpper);

    // ── 필수 요소 구성 ──

    [Fact]
    public void PullbackAndBreakoutRequireAlignmentWhileReboundRequiresReclaim()
    {
        Assert.Equal(
            new[] { "invalidationQuality", "targetQuality", "extensionQuality", "triggerVolumeQuality", "alignmentQuality" },
            EntryQualityEvaluator.RequiredComponents(SetupKind.Pullback).ToArray());
        Assert.Equal(EntryQualityEvaluator.RequiredComponents(SetupKind.Pullback).ToArray(),
            EntryQualityEvaluator.RequiredComponents(SetupKind.Breakout).ToArray());
        Assert.Equal(
            new[] { "invalidationQuality", "targetQuality", "extensionQuality", "triggerVolumeQuality", "reclaimQuality" },
            EntryQualityEvaluator.RequiredComponents(SetupKind.Rebound).ToArray());
    }

    [Fact]
    public void ScoreIsTheGeometricMeanOfExactlyTheRequiredComponents()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(), P);

        var expected = new[]
        {
            .6,                                     // invalidationQuality
            .7,                                     // targetQuality
            Math.Exp(-(100 - 99.15) / .2 / 3),      // extensionQuality
            2.0 / 3,                                // triggerVolumeQuality
            (1 + 40.0 / 100) / 2                    // alignmentQuality
        };
        Assert.Equal(100 * StructureMath.GeometricMean(expected), result.Score!.Value, 10);
        Assert.Equal(5, result.UsedComponents.Length);
        Assert.DoesNotContain("roomQuality", result.UsedComponents);
        Assert.Empty(result.MissingRequired);
        Assert.True(result.ReadyAllowed);
    }

    /// <summary>§9.4: 원값·변환값·결측을 함께 저장하고 사용한 요소 배열을 남긴다.</summary>
    [Fact]
    public void RawAndTransformedValuesAreBothStoredWithTheRequiredFlag()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(), P);
        var room = result.Components.Single(x => x.Name == "roomQuality");
        var extension = result.Components.Single(x => x.Name == "extensionQuality");

        Assert.Equal(1.4, room.Raw!.Value, 12);
        Assert.Equal(1 - Math.Exp(-1.4 / 2), room.Value!.Value, 12);
        Assert.False(room.Required);
        Assert.Equal((100 - 99.15) / .2, extension.Raw!.Value, 12);
        Assert.True(result.Components.All(x => x.Value is null || (x.Value >= 0 && x.Value <= 1)));
    }

    // ── 결측·0 ──

    [Theory]
    [InlineData("invalidationQuality")]
    [InlineData("targetQuality")]
    [InlineData("extensionQuality")]
    [InlineData("triggerVolumeQuality")]
    [InlineData("alignmentQuality")]
    public void AnyMissingRequiredComponentMakesTheScoreNullAndForbidsReady(string missing)
    {
        var input = missing switch
        {
            "invalidationQuality" => Input(invalidation: null),
            "targetQuality" => Input(target: null),
            "extensionQuality" => Input(anchor: null),
            "triggerVolumeQuality" => Input(rv: null),
            _ => Input(signedTrend: null)
        };
        var result = EntryQualityEvaluator.Evaluate(input, P);

        Assert.Null(result.Score);
        Assert.False(result.ReadyAllowed);
        Assert.Contains(missing, result.MissingRequired);
        Assert.Contains(EntryQualityEvaluator.ReasonMissingRequiredComponent, result.Reasons);
    }

    /// <summary>§16A: 트리거 relativeVolume 분모가 0이면 null+NO_VOLUME_BASELINE이고 신규 진입은 보류한다.</summary>
    [Fact]
    public void MissingVolumeBaselineIsReportedAsNoVolumeBaseline()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(rv: null), P);

        Assert.Contains(EntryQualityEvaluator.ReasonNoVolumeBaseline, result.Reasons);
        Assert.Null(result.Score);
        // 결측을 0으로 대체하지 않는다.
        Assert.Null(result.Components.Single(x => x.Name == "triggerVolumeQuality").Value);
    }

    [Fact]
    public void ZeroRelativeVolumeIsAZeroScoreRatherThanAMissingComponent()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(rv: 0), P);

        Assert.Equal(0, result.Score);
        Assert.False(result.ReadyAllowed);
        Assert.Contains("triggerVolumeQuality", result.ZeroRequired);
        Assert.Contains(EntryQualityEvaluator.ReasonZeroRequiredComponent, result.Reasons);
        Assert.DoesNotContain(EntryQualityEvaluator.ReasonNoVolumeBaseline, result.Reasons);
    }

    [Fact]
    public void ZeroNetRoomIsStoredAsZeroRoomQualityButNoLongerZeroesTheScore()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(netR: 0m), P);

        Assert.Equal(0, result.Components.Single(x => x.Name == "roomQuality").Value);
        Assert.Empty(result.ZeroRequired);
        Assert.Equal(EntryQualityEvaluator.Evaluate(Input(netR: 3m), P).Score, result.Score);
        Assert.True(result.ReadyAllowed);
    }

    // ── 종류별 규칙 ──

    /// <summary>§9.4: REBOUND는 alignmentQuality를 제외하고 reclaimQuality를 쓴다.</summary>
    [Fact]
    public void ReboundUsesReclaimQualityAndKeepsAlignmentAsAReferenceOnly()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(SetupKind.Rebound), P);
        var reclaim = result.Components.Single(x => x.Name == "reclaimQuality");
        var alignment = result.Components.Single(x => x.Name == "alignmentQuality");

        Assert.True(reclaim.Required);
        Assert.False(alignment.Required);
        Assert.Equal(1 - Math.Exp(-(double)(99.75m - 99.40m) / .2), reclaim.Value!.Value, 12);
        Assert.DoesNotContain("alignmentQuality", result.UsedComponents);
        Assert.NotNull(result.Score);
    }

    /// <summary>§8: 반등은 상승 추세 점수가 부족하다는 이유만으로 거절되지 않는다.</summary>
    [Fact]
    public void ReboundIsNotRejectedJustBecauseTheTrendScoreIsWeak()
    {
        var weak = EntryQualityEvaluator.Evaluate(Input(SetupKind.Rebound, signedTrend: -60), P);
        var strong = EntryQualityEvaluator.Evaluate(Input(SetupKind.Rebound, signedTrend: 60), P);

        Assert.NotNull(weak.Score);
        Assert.True(weak.ReadyAllowed);
        Assert.Equal(strong.Score!.Value, weak.Score!.Value, 12);      // 추세는 점수에 들어가지 않는다
        Assert.Empty(weak.MissingRequired);

        // 같은 조건의 PULLBACK은 alignment 때문에 점수가 낮아진다(추세 동행 전략과 섞지 않는다).
        var pullback = EntryQualityEvaluator.Evaluate(Input(signedTrend: -60), P);
        Assert.True(pullback.Score < weak.Score);
    }

    [Fact]
    public void ReboundWithoutATrendNumberStillScores()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(SetupKind.Rebound, signedTrend: null), P);

        Assert.NotNull(result.Score);
        Assert.Null(result.Components.Single(x => x.Name == "alignmentQuality").Value);
        Assert.DoesNotContain(EntryQualityEvaluator.ReasonTrendUnavailable, result.Reasons);
    }

    // ── 위치 품질의 방향성 ──

    /// <summary>#209: netR은 자격 게이트가 소비한다 — 점수는 netR과 함께 움직이지 않는다.</summary>
    [Fact]
    public void NetRNoLongerMovesTheScoreAtAll()
    {
        var roomy = EntryQualityEvaluator.Evaluate(Input(netR: 3.0m, signedTrend: 80), P);
        var cramped = EntryQualityEvaluator.Evaluate(Input(netR: .3m, signedTrend: 80), P);

        Assert.Equal(roomy.Score, cramped.Score);
        Assert.True(cramped.Score > 0);
        Assert.NotEqual(roomy.Components.Single(x => x.Name == "roomQuality").Value,
            cramped.Components.Single(x => x.Name == "roomQuality").Value);
    }

    /// <summary>§9.4 extensionQuality: anchor에서 멀어질수록(추격) 품질이 낮아진다.</summary>
    [Fact]
    public void ExtensionQualityFallsAsEntryMovesAwayFromTheAnchor()
    {
        var tight = EntryQualityEvaluator.Evaluate(Input(entry: 99.50m), P);
        var extended = EntryQualityEvaluator.Evaluate(Input(entry: 101.00m), P);

        Assert.True(extended.Score < tight.Score);
        // Entry가 anchor 아래면 거리는 0으로 잘리고 1이 된다(음수 거리로 품질을 부풀리지 않는다).
        var below = EntryQualityEvaluator.Evaluate(Input(entry: 99.00m), P);
        Assert.Equal(1, below.Components.Single(x => x.Name == "extensionQuality").Value);
    }

    [Fact]
    public void ExtensionQualityDistanceSubtractsTheStopBufferFromTheChaseDistance()
    {
        var withoutBuffer = EntryQualityEvaluator.Evaluate(Input(), P);
        var withBuffer = EntryQualityEvaluator.Evaluate(Input() with { StopBuffer = .15m }, P);

        Assert.Equal((100 - 99.15) / .2, withoutBuffer.Components.Single(x => x.Name == "extensionQuality").Raw!.Value, 12);
        Assert.Equal((100 - 99.15 - .15) / .2, withBuffer.Components.Single(x => x.Name == "extensionQuality").Raw!.Value, 12);
        Assert.True(withBuffer.Score > withoutBuffer.Score);

        var overBuffer = EntryQualityEvaluator.Evaluate(Input() with { StopBuffer = 2.00m }, P);
        Assert.Equal(0, overBuffer.Components.Single(x => x.Name == "extensionQuality").Raw!.Value);
        Assert.Equal(1, overBuffer.Components.Single(x => x.Name == "extensionQuality").Value);
    }

    [Fact]
    public void NegativeNetRIsFlooredAtZeroRoomRatherThanThrowing()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(netR: -2.0m), P);

        Assert.Equal(0, result.Components.Single(x => x.Name == "roomQuality").Value);
        Assert.Equal(EntryQualityEvaluator.Evaluate(Input(), P).Score, result.Score);
    }

    [Fact]
    public void TheFiveComponentFingerprintKeepsRoomQualityAsAReferenceValue()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(), P);

        var room = result.Components.Single(x => x.Name == "roomQuality");
        Assert.False(room.Required);
        Assert.Equal(5, result.UsedComponents.Length);
        Assert.DoesNotContain("roomQuality", result.UsedComponents);
        Assert.Contains($"roomQuality:{StructureMath.Number(room.Raw)}:{StructureMath.Number(room.Value)}:o",
            result.Fingerprint());
        Assert.NotEqual(result.Fingerprint(), EntryQualityEvaluator.Evaluate(Input(netR: 1.5m), P).Fingerprint());
        Assert.Equal(result.Score, EntryQualityEvaluator.Evaluate(Input(netR: 1.5m), P).Score);
    }

    // ── 수치 안전과 결정성 ──

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonUsableAtrMakesAtrBasedComponentsMissingRatherThanFlooredOrNaN(double? atr)
    {
        var pullback = EntryQualityEvaluator.Evaluate(Input(atr: atr), P);

        Assert.Null(pullback.Score);
        Assert.False(pullback.ReadyAllowed);
        Assert.Null(pullback.Components.Single(x => x.Name == "extensionQuality").Value);
        Assert.Contains("extensionQuality", pullback.MissingRequired);
        Assert.Contains(EntryQualityEvaluator.ReasonAtrUnavailable, pullback.Reasons);
        Assert.All(pullback.Components, component =>
            Assert.True(component.Value is null || double.IsFinite(component.Value.Value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonUsableAtrMakesReclaimQualityNullInsteadOfAPerfectScore(double? atr)
    {
        var result = EntryQualityEvaluator.Evaluate(Input(SetupKind.Rebound, atr: atr), P);

        Assert.Null(result.Components.Single(x => x.Name == "reclaimQuality").Value);
        Assert.Null(result.Components.Single(x => x.Name == "reclaimQuality").Raw);
        Assert.Contains("reclaimQuality", result.MissingRequired);
        Assert.Null(result.Score);
        Assert.False(result.ReadyAllowed);
        Assert.Contains(EntryQualityEvaluator.ReasonAtrUnavailable, result.Reasons);
    }

    [Fact]
    public void UsableAtrKeepsTheIndicatorFloorOnlyAsTheNaNGuard()
    {
        var tiny = EntryQualityEvaluator.Evaluate(Input(SetupKind.Rebound, atr: 1e-9), P);

        Assert.Equal((double)(99.75m - 99.40m) / P.IndicatorFloor,
            tiny.Components.Single(x => x.Name == "reclaimQuality").Raw!.Value, 12);
        Assert.NotNull(tiny.Score);
        Assert.DoesNotContain(EntryQualityEvaluator.ReasonAtrUnavailable, tiny.Reasons);
    }

    [Fact]
    public void NonFiniteRelativeVolumeIsTreatedAsMissingNotAsAHugeScore()
    {
        var result = EntryQualityEvaluator.Evaluate(Input(rv: double.PositiveInfinity), P);

        Assert.Null(result.Score);
        Assert.Contains(EntryQualityEvaluator.ReasonNoVolumeBaseline, result.Reasons);
    }

    [Fact]
    public void TheSameInputAlwaysProducesTheSameResult()
    {
        Assert.Equal(EntryQualityEvaluator.Evaluate(Input(), P).Fingerprint(),
            EntryQualityEvaluator.Evaluate(Input(), P).Fingerprint());
    }

    /// <summary>첫 버전에서 이 점수에 BUY=70 같은 과거 기준을 붙이지 않는다(§9.4).</summary>
    [Fact]
    public void ScoreIsBoundedToZeroHundredWithoutALegacyBuyThreshold()
    {
        var best = EntryQualityEvaluator.Evaluate(Input(invalidation: 1, target: 1, netR: 100m, rv: 1e9,
            signedTrend: 100, entry: 99.15m), P);

        Assert.True(best.Score <= 100);
        Assert.True(best.Score > 90);
        Assert.True(best.ReadyAllowed);
    }
}
