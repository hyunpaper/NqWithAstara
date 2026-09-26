using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Domain.ScoreCore;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ScoreCoreDomainTests
{
    static readonly DateTimeOffset AsOf = new(2026, 9, 27, 14, 0, 0, TimeSpan.Zero);

    public static TheoryData<ImpactDirection, ImpactTargetKind, ImpactHorizon, double?, double?, bool> Directions => new()
    {
        { ImpactDirection.Favorable, ImpactTargetKind.Company, ImpactHorizon.Intraday, 0.7, null, true },
        { ImpactDirection.Favorable, ImpactTargetKind.Sector, ImpactHorizon.MultiDay, 0.5, null, true },
        { ImpactDirection.Unfavorable, ImpactTargetKind.Asset, ImpactHorizon.Session, 0.6, null, true },
        { ImpactDirection.Unfavorable, ImpactTargetKind.Company, ImpactHorizon.MultiDay, 0.4, null, true },
        { ImpactDirection.Mixed, ImpactTargetKind.Company, ImpactHorizon.Session, 0.7, 0.5, true },
        { ImpactDirection.Mixed, ImpactTargetKind.Market, ImpactHorizon.MultiDay, 0.4, 0.8, true },
        { ImpactDirection.Neutral, ImpactTargetKind.Sector, ImpactHorizon.Intraday, 0, null, true },
        { ImpactDirection.Neutral, ImpactTargetKind.Asset, ImpactHorizon.Session, 0, null, true },
        { ImpactDirection.Unknown, ImpactTargetKind.Company, ImpactHorizon.Intraday, null, null, false },
        { ImpactDirection.Unknown, ImpactTargetKind.Market, ImpactHorizon.MultiDay, null, null, false },
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public void 방향_범주는_대상과_시간대별로_근거_단위를_보존한다(ImpactDirection direction,
        ImpactTargetKind targetKind, ImpactHorizon horizon, double? positiveOrSeverity, double? negative, bool included)
    {
        var evidence = Evidence("e1", "g1", direction, targetKind, horizon,
            severity: direction is ImpactDirection.Favorable or ImpactDirection.Unfavorable or ImpactDirection.Neutral ? positiveOrSeverity : null,
            positive: direction == ImpactDirection.Mixed ? positiveOrSeverity : null, negative: negative);

        var scored = EventImpactScorer.Score(evidence, AsOf);

        Assert.Equal(included, scored.Included);
        if (direction == ImpactDirection.Favorable) Assert.True(scored.PositiveUnit > 0);
        if (direction == ImpactDirection.Unfavorable) Assert.True(scored.NegativeUnit > 0);
        if (direction == ImpactDirection.Mixed) Assert.True(scored.PositiveUnit > 0 && scored.NegativeUnit > 0);
        if (direction == ImpactDirection.Neutral) Assert.Equal(0, scored.PositiveUnit + scored.NegativeUnit);
        if (direction == ImpactDirection.Unknown) Assert.Contains("impact_direction_unknown", scored.ExclusionReasons);
    }

    [Fact]
    public void 기사_감성을_반전해도_명시적_시장영향은_변하지_않는다()
    {
        var positiveTone = Evidence("e1", "g1", ImpactDirection.Unfavorable, sentiment: "positive");
        var negativeTone = positiveTone with { EvidenceId = "e2", Sentiment = "negative" };

        var first = EventImpactScorer.Score(positiveTone, AsOf);
        var second = EventImpactScorer.Score(negativeTone, AsOf);

        Assert.Equal(first.NegativeUnit, second.NegativeUnit);
        Assert.Equal(0, first.PositiveUnit);
        Assert.Equal(0, second.PositiveUnit);
    }

    [Fact]
    public void 감정적_문구와_기존_strength는_중대도를_대신하지_않는다()
    {
        var missing = Evidence("e1", "g1", ImpactDirection.Favorable, severity: null) with
        {
            EvidenceSpan = "극도로 강력한 역사적 호재"
        };

        var scored = EventImpactScorer.Score(missing, AsOf);

        Assert.False(scored.Included);
        Assert.Contains("unknown_materiality", scored.ExclusionReasons);
    }

    [Fact]
    public void 사실확인과_신뢰도_축은_raw_shadow_unit을_임의로_곱하지_않는다()
    {
        var verified = Evidence("e1", "g1", ImpactDirection.Favorable) with
        {
            FactVerification = FactVerification.Verified,
            Quality = new EvidenceQuality(1, 1, 1, 1)
        };
        var unverified = verified with
        {
            EvidenceId = "e2", FactVerification = FactVerification.Unverified,
            Quality = new EvidenceQuality(.2, .3, .4, .5)
        };

        var first = EventImpactScorer.Score(verified, AsOf);
        var second = EventImpactScorer.Score(unverified, AsOf);

        Assert.Equal(first.PositiveUnit, second.PositiveUnit);
        Assert.True(first.QualityEligible);
        Assert.False(second.QualityEligible);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public void 같은_사건의_재게시_수는_gross_event_mass를_늘리지_않는다(int copies)
    {
        var evidence = Enumerable.Range(1, copies)
            .Select(index => Evidence($"e{index}", "same-event", ImpactDirection.Favorable) with
            {
                SourceId = $"source-{index}", Source = $"출처 {index}"
            });

        var snapshot = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, evidence);
        var intraday = snapshot.Horizons.Single(x => x.Horizon == ImpactHorizon.Intraday);

        Assert.Equal(1, intraday.UniqueEventCount);
        Assert.Equal(EventImpactScorer.Score(Evidence("base", "same-event", ImpactDirection.Favorable), AsOf).PositiveUnit,
            intraday.PositiveMass);
        Assert.Equal(copies, intraday.EvidenceCount);
    }

    [Fact]
    public void 상반된_독립_근거는_중립과_다른_conflict를_만든다()
    {
        var favorable = Evidence("good", "contract", ImpactDirection.Favorable) with { Mechanism = "매출 증가" };
        var unfavorable = Evidence("bad", "contract", ImpactDirection.Unfavorable) with { Mechanism = "마진 감소" };

        var snapshot = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, [favorable, unfavorable]);
        var intraday = snapshot.Horizons.Single(x => x.Horizon == ImpactHorizon.Intraday);

        Assert.Equal(1, intraday.UniqueEventCount);
        Assert.True(intraday.Conflict > 0);
        Assert.NotNull(intraday.DirectionalLean);
        Assert.Equal(intraday.PositiveMass - intraday.NegativeMass, intraday.SignedEvidence);
    }

    [Fact]
    public void unknown은_분모에서_제외되고_neutral은_유효한_0점_근거로_남는다()
    {
        var unknown = Evidence("u", "unknown", ImpactDirection.Unknown, severity: null);
        var neutral = Evidence("n", "neutral", ImpactDirection.Neutral, severity: 0);

        var snapshot = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, [unknown, neutral]);
        var intraday = snapshot.Horizons.Single(x => x.Horizon == ImpactHorizon.Intraday);

        Assert.Equal(1, snapshot.IncludedCount);
        Assert.Equal(1, snapshot.UnknownCount);
        Assert.Equal(1, intraday.EvidenceCount);
        Assert.Equal(0, intraday.SignedEvidence);
        Assert.Contains(snapshot.ExcludedEvidence, x => x.EvidenceId == "u" && x.Reason == "impact_direction_unknown");
    }

    [Fact]
    public void 미래시각과_순서모순은_계산에서_제외한다()
    {
        var future = Evidence("future", "g1", ImpactDirection.Favorable) with { PublishedAt = AsOf.AddMinutes(1) };
        var reversed = Evidence("reversed", "g2", ImpactDirection.Favorable) with
        {
            PublishedAt = AsOf.AddMinutes(-5), CollectedAt = AsOf.AddMinutes(-10)
        };

        Assert.All(new[] { future, reversed }, item =>
            Assert.Contains("future_or_inconsistent_timestamp", EventImpactScorer.Score(item, AsOf).ExclusionReasons));
    }

    [Fact]
    public void 명시적_관계가_없는_간접_산업전파는_제외한다()
    {
        var unresolved = Evidence("musk", "demand", ImpactDirection.Favorable, ImpactTargetKind.Company) with
        {
            Target = new ImpactTarget("HPE", ImpactTargetKind.Company, TargetDirectness.Unresolved)
        };

        var scored = EventImpactScorer.Score(unresolved, AsOf);

        Assert.Contains("unresolved_target", scored.ExclusionReasons);
    }

    [Fact]
    public void 대상_id가_같아도_종류가_다르면_집계하지_않는다()
    {
        var company = Evidence("company", "g1", ImpactDirection.Favorable, ImpactTargetKind.Company);
        var sector = Evidence("sector", "g2", ImpactDirection.Favorable, ImpactTargetKind.Sector);

        var snapshot = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, [company, sector]);

        Assert.Equal(1, snapshot.InputCount);
        Assert.Equal(1, snapshot.IncludedCount);
    }

    [Fact]
    public void 간접_대상은_명시적_관계_근거가_필요하다()
    {
        var missing = Evidence("missing", "g1", ImpactDirection.Favorable) with
        {
            Target = new ImpactTarget("NVDA", ImpactTargetKind.Company, TargetDirectness.Indirect, " ")
        };
        var explained = missing with
        {
            EvidenceId = "explained", Target = missing.Target with { RelationEvidence = "공급 계약의 상대방으로 명시" }
        };

        Assert.Contains("missing_relation_evidence", EventImpactScorer.Score(missing, AsOf).ExclusionReasons);
        Assert.True(EventImpactScorer.Score(explained, AsOf).Included);
    }

    [Theory]
    [InlineData(FactVerification.Unverified, MarketExpectationStatus.InLine, 0.0, "neutral_fact_unverified")]
    [InlineData(FactVerification.Verified, MarketExpectationStatus.Unknown, 0.0, "neutral_expectation_not_inline")]
    [InlineData(FactVerification.Verified, MarketExpectationStatus.Above, 0.0, "neutral_expectation_not_inline")]
    [InlineData(FactVerification.Verified, MarketExpectationStatus.InLine, null, "neutral_materiality_unknown")]
    [InlineData(FactVerification.Verified, MarketExpectationStatus.InLine, 0.2, "neutral_materiality_not_low")]
    public void neutral은_확인된_인라인_저중대도_근거만_허용한다(FactVerification fact,
        MarketExpectationStatus expectation, double? severity, string reason)
    {
        var evidence = Evidence("neutral", "g1", ImpactDirection.Neutral, severity: severity) with
        {
            FactVerification = fact, MarketExpectationStatus = expectation
        };

        Assert.Contains(reason, EventImpactScorer.Score(evidence, AsOf).ExclusionReasons);
    }

    [Fact]
    public void 필수_식별자와_quality_축_결측은_제외하고_unknown_기대는_품질적격이_아니다()
    {
        var missing = Evidence(" ", " ", ImpactDirection.Favorable) with
        {
            SourceId = " ", Source = " ", ClassifierVersion = " ",
            Quality = new EvidenceQuality(null, null, null, null)
        };
        var unknownExpectation = Evidence("valid", "g1", ImpactDirection.Favorable) with
        {
            MarketExpectationStatus = MarketExpectationStatus.Unknown
        };

        var rejected = EventImpactScorer.Score(missing, AsOf);
        Assert.Contains("missing_evidence_id", rejected.ExclusionReasons);
        Assert.Contains("missing_source_reliability", rejected.ExclusionReasons);
        Assert.False(EventImpactScorer.Score(unknownExpectation, AsOf).QualityEligible);
    }

    [Fact]
    public void 같은_evidence_id의_서로_다른_사실은_입력순서와_무관하게_모두_제외한다()
    {
        var first = Evidence("collision", "g1", ImpactDirection.Favorable);
        var second = first with { EventGroupId = "g2", Mechanism = "반대 사실", ImpactDirection = ImpactDirection.Unfavorable };

        var a = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, [first, second]);
        var b = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, [second, first]);

        Assert.Equal(0, a.IncludedCount);
        Assert.Equal(2, a.ExcludedEvidence.Count(x => x.Reason == "duplicate_identity_conflict"));
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    [Fact]
    public void 같은_입력과_asOf는_동일한_snapshot을_만든다()
    {
        var rows = new[]
        {
            Evidence("b", "g2", ImpactDirection.Unfavorable),
            Evidence("a", "g1", ImpactDirection.Favorable),
        };

        var first = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, rows);
        var second = ScoreCoreAggregator.Aggregate("nvda", ImpactTargetKind.Company, AsOf, rows.Reverse());

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    static EventEvidence Evidence(string id, string group, ImpactDirection direction,
        ImpactTargetKind targetKind = ImpactTargetKind.Company, ImpactHorizon horizon = ImpactHorizon.Intraday,
        double? severity = .6, double? positive = null, double? negative = null, string sentiment = "neutral") =>
        new(id, group, "source-1", "검증 출처", "https://example.test/article", sentiment,
            "company_contract", new ImpactTarget("NVDA", targetKind, TargetDirectness.Direct), horizon,
            direction, "현금흐름 변화", "계약 조건 원문", FactVerification.Verified,
            direction == ImpactDirection.Neutral ? MarketExpectationStatus.InLine : MarketExpectationStatus.Above,
            severity, positive, negative, AsOf.AddMinutes(-30),
            AsOf.AddMinutes(-29), AsOf.AddMinutes(-28), direction == ImpactDirection.Mixed
                ? ["상승 경로", "하락 경로"] : ImmutableArray<string>.Empty,
            new EvidenceQuality(.8, .8, .9, .8), "fixture-v1");
}
