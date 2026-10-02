using Astra.Server.Application;
using Astra.Server.Domain.News;
using Xunit;

namespace Astra.Server.Tests;

public sealed class NewsRelevanceLexiconTests
{
    readonly NewsRelevancePolicy _policy = new();

    [Theory]
    [InlineData("Trump announces new tariffs on imports")]
    [InlineData("트럼프 관세 부과 발표")]
    [InlineData("China restricts rare earth exports")]
    [InlineData("중국 희토류 수출 제한")]
    [InlineData("Iran launches missiles at tanker")]
    [InlineData("이란 유조선 공격")]
    [InlineData("Strait of Hormuz closed to tankers")]
    [InlineData("호르무즈 해협 봉쇄")]
    [InlineData("Oil prices surge")]
    [InlineData("유가 급등")]
    [InlineData("Crude inventories fall")]
    [InlineData("원유 재고 하락")]
    [InlineData("Treasury yields rise")]
    [InlineData("국채 금리 상승")]
    [InlineData("Bond prices drop")]
    [InlineData("채권금리 상승")]
    [InlineData("Central bank cuts interest rates")]
    [InlineData("기준금리 인하")]
    [InlineData("Fed holds rates steady")]
    [InlineData("연준 금리 동결")]
    [InlineData("Officials signal rate cut ahead")]
    [InlineData("Saudi Arabia raises output")]
    [InlineData("사우디 증산 발표")]
    [InlineData("Houthis attack ships in Red Sea")]
    [InlineData("후티 반군 선박 공격")]
    [InlineData("Yemen port attacked")]
    [InlineData("예멘 항구 공습")]
    [InlineData("Missile launched toward port")]
    [InlineData("미사일 발사")]
    [InlineData("Airstrikes disrupt oil exports")]
    [InlineData("Drone strikes disrupt refinery output")]
    [InlineData("공습으로 정유시설 타격")]
    public void 이슈_키워드는_사건동사와_결합하면_포함한다(string title)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("macro_event_confirmed", result.Reason);
        Assert.NotEmpty(result.Action);
        Assert.NotEmpty(result.Targets);
    }

    [Theory]
    [InlineData("Trump")]
    [InlineData("트럼프 인터뷰")]
    [InlineData("China outlook")]
    [InlineData("중국 경제 전망")]
    [InlineData("Iran")]
    [InlineData("이란 대표단")]
    [InlineData("Strait of Hormuz")]
    [InlineData("호르무즈 해협")]
    [InlineData("Oil")]
    [InlineData("유가 동향")]
    [InlineData("Crude")]
    [InlineData("원유")]
    [InlineData("Treasury")]
    [InlineData("국채")]
    [InlineData("Bond")]
    [InlineData("채권금리")]
    [InlineData("Interest rate")]
    [InlineData("금리")]
    [InlineData("Fed")]
    [InlineData("연준 인사")]
    [InlineData("rate")]
    [InlineData("Saudi Arabia")]
    [InlineData("사우디")]
    [InlineData("Houthis")]
    [InlineData("후티")]
    [InlineData("Yemen")]
    [InlineData("예멘")]
    [InlineData("missile")]
    [InlineData("미사일")]
    [InlineData("airstrike")]
    [InlineData("strike")]
    [InlineData("공습")]
    public void 이슈_키워드만_있고_사건동사가_없으면_포함하지_않는다(string title)
    {
        var result = Evaluate(title);

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Contains(result.Decision, new[] { NewsRelevanceDecisions.Review, NewsRelevanceDecisions.Exclude });
    }

    [Fact]
    public void fed_up_관용구는_연준_거시사건으로_판정하지_않는다()
    {
        var result = Evaluate("Investors are fed up as stocks fall");

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
        Assert.NotEqual("Fed", result.Actor);
    }

    [Fact]
    public void FED_UP_대문자_관용구도_연준으로_보지_않는다()
    {
        var result = Evaluate("INVESTORS FED UP AS STOCKS FALL");

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Theory]
    [InlineData("Iran attacks oil tanker within two sec of launch", "Iran")]
    [InlineData("The firm secured a deal as oil prices rise", "oil")]
    public void 소문자_sec와_secured는_SEC_actor로_오인하지_않는다(string title, string actor)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(actor, result.Actor);
    }

    [Fact]
    public void 대문자_SEC는_기관_actor로_인식한다()
    {
        var result = Evaluate("SEC announces sanctions on Iran as markets fall");

        Assert.Equal("SEC", result.Actor);
    }

    [Theory]
    [InlineData("ACME Inc revenue matched estimates as guidance rose", "guidance_earnings")]
    [InlineData("ACME Inc says the goal of the buyback is to lift returns", "company_action")]
    [InlineData("ACME Inc signs contract in a match against rivals", "company_contract")]
    public void 금융문장의_match와_goal은_기업사건으로_포함한다(string title, string kind)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(kind, result.EventKind);
    }

    [Theory]
    [InlineData("Striker scored a goal in the football match")]
    [InlineData("Team scores winning goal in soccer match")]
    public void 스포츠_골과_경기는_제외한다(string title)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }

    [Theory]
    [InlineData("Dell signs supply contract with hyperscaler", "entity:Dell")]
    [InlineData("DELL wins server contract", "entity:DELL")]
    [InlineData("Contract awarded to Dell for servers", "entity:Dell")]
    public void 관심종목_사전_엔티티와_사건동사는_direct로_포함한다(string title, string evidence)
    {
        var result = _policy.Evaluate(Item(title), Context(("DELL", "Dell")));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("company_contract", result.EventKind);
        var target = Assert.Single(result.Targets);
        Assert.Equal("DELL", target.Id);
        Assert.Equal("direct", target.Relation);
        Assert.Equal(evidence, target.Evidence);
    }

    [Fact]
    public void 한글_별칭과_조사가_붙은_엔티티도_direct로_포함한다()
    {
        var result = _policy.Evaluate(Item("엔비디아가 대규모 수주 계약을 따냈다"), Context(("NVDA", "NVIDIA")));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("NVDA", Assert.Single(result.Targets).Id);
    }

    [Fact]
    public void 사전_밖_일반명사와_동사는_review로_보류한다()
    {
        var result = _policy.Evaluate(Item("Supplier signs supply contract"), Context(("DELL", "Dell")));

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
    }

    [Fact]
    public void 다른_절의_엔티티는_사건대상으로_결합하지_않는다()
    {
        var result = _policy.Evaluate(Item("Dell shares were quiet. Supplier signs supply contract"), Context(("DELL", "Dell")));

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
    }

    [Fact]
    public void 짧은_한글_별칭은_다른_단어의_일부로_매칭하지_않는다()
    {
        var result = _policy.Evaluate(Item("메타버스 투자 확대"), Context(("META", "Meta Platforms")));

        Assert.DoesNotContain(result.Targets, x => x.Id == "META");
        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Fact]
    public void 짧은_한글_별칭에_조사가_붙으면_엔티티로_인식한다()
    {
        var result = _policy.Evaluate(Item("메타가 데이터센터 투자 확대"), Context(("META", "Meta Platforms")));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("META", Assert.Single(result.Targets).Id);
    }

    [Fact]
    public void 정책버전은_엔티티_사전_hash를_합성한다()
    {
        var empty = _policy.Evaluate(Item("Oil prices surge"), NewsRelevanceContext.Empty);
        var dell = _policy.Evaluate(Item("Oil prices surge"), Context(("DELL", "Dell")));
        var same = _policy.Evaluate(Item("Oil prices surge"), Context(("DELL", "Dell")));

        Assert.StartsWith("sbh-relevance-v2+entities:", empty.PolicyVersion);
        Assert.Equal(8, empty.PolicyVersion["sbh-relevance-v2+entities:".Length..].Length);
        Assert.NotEqual(empty.PolicyVersion, dell.PolicyVersion);
        Assert.Equal(dell.PolicyVersion, same.PolicyVersion);
    }

    [Fact]
    public void 사전은_버전을_가진다()
    {
        Assert.Equal("lexicon-2026-10-02", NewsRelevanceLexicon.Version);
        Assert.Equal("sbh-relevance-v2", NewsRelevancePolicy.CurrentVersion);
    }

    [Fact]
    public async Task watchlist_context_source는_관심종목과_별칭으로_사전을_만든다()
    {
        var source = new WatchlistNewsRelevanceContextSource(new NewsLocalStore(
            new Astra.Server.WatchItem("DELL", "Dell"), new Astra.Server.WatchItem("NVDA", "NVIDIA")));

        var context = await source.GetAsync(CancellationToken.None);

        Assert.Contains(context.Entities, x => x.Id == "DELL" && x.Surface == "Dell");
        Assert.Contains(context.Entities, x => x.Id == "DELL" && x.Surface == "DELL");
        Assert.Contains(context.Entities, x => x.Id == "NVDA" && x.Surface == "엔비디아");
    }

    NewsRelevanceAssessment Evaluate(string title) => _policy.Evaluate(Item(title), NewsRelevanceContext.Empty);

    static NewsRelevanceContext Context(params (string Symbol, string Name)[] watchlist)
        => NewsRelevanceContext.Create(watchlist.Select(x => new NewsWatchSymbol(x.Symbol, x.Name)));

    static NewsFeedItem Item(string title) => new("id", title, "", "SBH", DateTimeOffset.UtcNow, []);
}
