using Astra.Server.Application;
using Astra.Server.Domain.News;
using Xunit;

namespace Astra.Server.Tests;

public sealed class NewsRelevancePolicyTests
{
    readonly NewsRelevancePolicy _policy = new();

    [Theory]
    [InlineData("Trump imposes tariffs on China, stocks fall", "macro_policy")]
    [InlineData("트럼프, 중국 관세 인상 발표…증시 하락", "macro_policy")]
    [InlineData("Iran missile strike threatens Strait of Hormuz oil supply", "geopolitical")]
    [InlineData("이란 미사일 공습으로 호르무즈 원유 공급 차질", "geopolitical")]
    [InlineData("Treasury yields rise after Fed holds interest rates", "monetary_policy")]
    [InlineData("연준 금리 동결 뒤 국채 금리 상승", "monetary_policy")]
    [InlineData("Saudi Arabia cuts crude supply and oil prices rise", "macro_policy")]
    [InlineData("후티가 예멘에서 선박을 공격해 유가가 상승", "geopolitical")]
    [InlineData("CPI beats forecast and bond yields surge", "macro_release")]
    public void 거시_지정학_대상과_사건동사가_함께_있으면_포함한다(string title, string kind)
    {
        var result = _policy.Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(kind, result.EventKind);
        Assert.NotEmpty(result.Action);
        Assert.NotEmpty(result.Targets);
        Assert.NotEmpty(result.EvidenceSpan);
    }

    [Theory]
    [InlineData("NVIDIA Corp signs a supply contract with Dell Technologies", "company_contract")]
    [InlineData("Samsung Electronics raises chip prices as demand expands", "company_action")]
    [InlineData("ACME Inc reports earnings and cuts guidance", "guidance_earnings")]
    [InlineData("Cloudflare Inc discloses cybersecurity breach", "cybersecurity")]
    [InlineData("MicroStrategy Inc announces convertible offering", "financing")]
    [InlineData("Tesla Inc faces regulator lawsuit", "regulatory")]
    public void 다양한_기업_행위자는_명시대상과_사건을_보존한다(string title, string kind)
    {
        var result = _policy.Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(kind, result.EventKind);
        Assert.All(result.Targets, target => Assert.Equal("direct", target.Relation));
    }

    [Theory]
    [InlineData("United striker scores winning goal in Premier League", "non_market_context")]
    [InlineData("배우가 새 영화에서 강렬한 연기를 선보였다", "non_market_context")]
    [InlineData("Workers strike during football match", "non_market_context")]
    [InlineData("Bond actor appears at film concert", "non_market_context")]
    public void 스포츠_연예와_다의어는_제외한다(string title, string reason)
    {
        var result = _policy.Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void 스포츠의_이적_시장은_거시_대상과_동사가_있어도_제외한다()
    {
        var result = _policy.Evaluate(Item("중국 축구 이적 시장 확대 발표"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }

    [Fact]
    public void 스포츠와_금융_사건이_혼재하면_강한_금융_근거로_포함한다()
    {
        var result = _policy.Evaluate(Item("월드컵 경기 중 연준 금리 인상 발표로 증시 하락"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("monetary_policy", result.EventKind);
    }

    [Fact]
    public void 스포츠_동사와_금융_명사를_서로_다른_문맥에서_결합하지_않는다()
    {
        var result = _policy.Evaluate(Item("Football players strike as bond investors watch the match"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }

    [Fact]
    public void 스포츠_기사안의_독립된_금융절은_포함한다()
    {
        var result = _policy.Evaluate(Item("World Cup match ends; Federal Reserve raises interest rates, stocks fall"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("monetary_policy", result.EventKind);
    }

    [Theory]
    [InlineData("Market reaction remains unclear")]
    [InlineData("Fed official gives a general interview")]
    [InlineData("ACME Inc considers its options")]
    public void 사건_동사나_시장경로가_불충분하면_review로_보류한다(string title)
    {
        var result = _policy.Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("insufficient_actor_action_target_context", result.Reason);
    }

    [Fact]
    public void 본문에_없는_ticker는_계약의_직접대상으로_승격하지_않는다()
    {
        var result = _policy.Evaluate(Item("Supplier signs multi-year contract") with { Tickers = ["NVDA"] });

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("unresolved", Assert.Single(result.Targets).Relation);
    }

    [Fact]
    public void 본문에서_사건과_연결된_ticker만_직접대상으로_승격한다()
    {
        var result = _policy.Evaluate(Item("NVDA signs multi-year contract") with { Tickers = ["NVDA", "AMD"] });

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        var target = Assert.Single(result.Targets);
        Assert.Equal("NVDA", target.Id);
        Assert.Equal("direct", target.Relation);
        Assert.StartsWith("text:", target.Evidence);
    }

    [Fact]
    public void 스포츠절의_strike와_다음절의_bond를_결합하지_않는다()
    {
        var result = _policy.Evaluate(Item("Football players strike during the final; bond investors watch quietly"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
    }

    [Fact]
    public void 선택된_사건절_밖의_기관을_actor로_사용하지_않는다()
    {
        var result = _policy.Evaluate(Item("SEC comments on policy. Iran attacks oil supply"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("Iran", result.Actor);
    }

    [Fact]
    public void 선택된_Fed_event는_같은_절의_별도_oil_target으로_덮지_않는다()
    {
        var result = _policy.Evaluate(Item("Fed raises interest rates as oil falls"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(NewsSymbols.Market, Assert.Single(result.Targets).Id);
        Assert.Equal("Fed", result.Actor);
    }

    [Fact]
    public void 같은_절의_먼_기관보다_event에_인접한_actor를_선택한다()
    {
        var result = _policy.Evaluate(Item("SEC observes while Federal Reserve raises interest rates"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("Federal Reserve", result.Actor);
    }

    [Fact]
    public void 스포츠절과_별도절의_금융명사만으로_review로_우회하지_않는다()
    {
        var result = _policy.Evaluate(Item("Football players strike during final. Bond investors watch"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
    }

    [Theory]
    [InlineData("US imposes sanctions on Iran and oil prices rise", "OIL", "asset")]
    [InlineData("OPEC cuts oil supply", "OIL", "asset")]
    [InlineData("NATO announces restrictions after Iran attack", "MARKET", "market")]
    [InlineData("SEC announces sanctions on Iran as markets fall", "MARKET", "market")]
    public void 기관_약어는_회사로_간주해_거시대상을_덮지_않는다(string title, string targetId, string targetKind)
    {
        var result = _policy.Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        var target = Assert.Single(result.Targets);
        Assert.Equal(targetId, target.Id);
        Assert.Equal(targetKind, target.Kind);
    }

    [Fact]
    public void 한국은행은_회사대상이_아니며_통화정책_시장대상으로_남긴다()
    {
        var result = _policy.Evaluate(Item("한국은행 금리 인상 발표"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("한국은행", result.Actor);
        var target = Assert.Single(result.Targets);
        Assert.Equal(NewsSymbols.Market, target.Id);
        Assert.Equal("market", target.Kind);
    }

    [Fact]
    public void 회사사건은_사건동사에_가까운_영향대상만_direct로_남긴다()
    {
        var result = _policy.Evaluate(Item("NVIDIA Corp comments as Tesla Inc recalls vehicles"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        var target = Assert.Single(result.Targets);
        Assert.Equal("TESLA INC", target.Id);
        Assert.Equal("direct", target.Relation);
    }

    static NewsFeedItem Item(string title) => new("id", title, "", "SBH", DateTimeOffset.UtcNow, []);
}

public sealed class NewsScoreCoreBridgeTests
{
    [Fact]
    public void 관련성_판정은_방향을_추정하지_않고_ScoreCore_unknown_shadow로_연결한다()
    {
        var now = new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);
        var relevance = new NewsRelevanceAssessment(NewsRelevancePolicy.CurrentVersion,
            NewsRelevanceDecisions.Include, "company_contract", "NVDA", "signs", [new("NVDA", "company", "direct", "NVDA")],
            "NVDA signs a contract", "company_event_confirmed");
        var record = new NewsRecord("one", "title", "SBH", now.AddMinutes(-1), [], [], ["NVDA"],
            NewsSentiments.Positive, 4, "감성 긍정", "model", 1, now, Relevance: relevance);

        var snapshot = Assert.Single(NewsScoreCoreBridge.Snapshots(record, now));

        Assert.Equal("insufficient_data", snapshot.Status);
        Assert.Equal(1, snapshot.UnknownCount);
        Assert.Equal(0, snapshot.IncludedCount);
        Assert.Contains(snapshot.ExcludedEvidence, x => x.Reason == "impact_direction_unknown");
    }

    [Fact]
    public void ScoreCore_연결은_간접과_미해결_관계를_직접으로_승격하지_않는다()
    {
        var now = new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);
        var relevance = Assessment([
            new("NVDA", "company", "indirect", ""),
            new("SOXX", "sector", "unresolved", "관계 미확인")]);

        var snapshots = NewsScoreCoreBridge.Snapshots(Record(now, relevance), now);

        Assert.Contains(snapshots.Single(x => x.TargetId == "NVDA").ExcludedEvidence,
            x => x.Reason == "missing_relation_evidence");
        Assert.Contains(snapshots.Single(x => x.TargetId == "SOXX").ExcludedEvidence,
            x => x.Reason == "unresolved_target");
    }

    [Fact]
    public void ScoreCore_연결은_알수없는_대상종류를_market으로_강등하지_않는다()
    {
        var now = new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero);
        var relevance = Assessment([new("MYSTERY", "unknown-kind", "direct", "본문")]);

        Assert.Empty(NewsScoreCoreBridge.Snapshots(Record(now, relevance), now));
    }

    static NewsRelevanceAssessment Assessment(IReadOnlyList<NewsEventTarget> targets) => new(
        NewsRelevancePolicy.CurrentVersion, NewsRelevanceDecisions.Include, "company_contract", "actor", "signs",
        targets, "actor signs a contract", "company_event_confirmed");

    static NewsRecord Record(DateTimeOffset now, NewsRelevanceAssessment relevance) => new(
        "one", "title", "SBH", now.AddMinutes(-1), [], [], ["NVDA"], NewsSentiments.Positive, 4,
        "감성 긍정", "model", 1, now, Relevance: relevance);
}
