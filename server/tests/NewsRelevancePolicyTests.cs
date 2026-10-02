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
        var result = Evaluate(Item(title));

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
        var result = Evaluate(Item(title));

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
        var result = Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void 스포츠의_이적_시장은_거시_대상과_동사가_있어도_제외한다()
    {
        var result = Evaluate(Item("중국 축구 이적 시장 확대 발표"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }

    [Fact]
    public void 스포츠와_금융_사건이_혼재하면_강한_금융_근거로_포함한다()
    {
        var result = Evaluate(Item("월드컵 경기 중 연준 금리 인상 발표로 증시 하락"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("monetary_policy", result.EventKind);
    }

    [Fact]
    public void 스포츠_동사와_금융_명사를_서로_다른_문맥에서_결합하지_않는다()
    {
        var result = Evaluate(Item("Football players strike as bond investors watch the match"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }

    [Fact]
    public void 스포츠_기사안의_독립된_금융절은_포함한다()
    {
        var result = Evaluate(Item("World Cup match ends; Federal Reserve raises interest rates, stocks fall"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("monetary_policy", result.EventKind);
    }

    [Theory]
    [InlineData("Fed official gives a general interview")]
    [InlineData("ACME Inc considers its options")]
    public void 대상은_있으나_사건_동사가_없으면_review로_보류한다(string title)
    {
        var result = Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("insufficient_actor_action_target_context", result.Reason);
    }

    [Fact]
    public void 본문에_없는_ticker는_계약의_직접대상으로_승격하지_않는다()
    {
        var result = Evaluate(Item("Supplier signs multi-year contract") with { Tickers = ["NVDA"] });

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("unresolved", Assert.Single(result.Targets).Relation);
    }

    [Fact]
    public void 본문에서_사건과_연결된_ticker만_직접대상으로_승격한다()
    {
        var result = Evaluate(Item("NVDA signs multi-year contract") with { Tickers = ["NVDA", "AMD"] });

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        var target = Assert.Single(result.Targets);
        Assert.Equal("NVDA", target.Id);
        Assert.Equal("direct", target.Relation);
        Assert.StartsWith("text:", target.Evidence);
    }

    [Fact]
    public void 스포츠절의_strike와_다음절의_bond를_결합하지_않는다()
    {
        var result = Evaluate(Item("Football players strike during the final; bond investors watch quietly"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
    }

    [Fact]
    public void 선택된_사건절_밖의_기관을_actor로_사용하지_않는다()
    {
        var result = Evaluate(Item("SEC comments on policy. Iran attacks oil supply"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("Iran", result.Actor);
    }

    [Fact]
    public void 선택된_Fed_event는_같은_절의_별도_oil_target으로_덮지_않는다()
    {
        var result = Evaluate(Item("Fed raises interest rates as oil falls"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(NewsSymbols.Market, Assert.Single(result.Targets).Id);
        Assert.Equal("Fed", result.Actor);
    }

    [Fact]
    public void 같은_절의_먼_기관보다_event에_인접한_actor를_선택한다()
    {
        var result = Evaluate(Item("SEC observes while Federal Reserve raises interest rates"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("Federal Reserve", result.Actor);
    }

    [Fact]
    public void 스포츠절과_별도절의_금융명사만으로_review로_우회하지_않는다()
    {
        var result = Evaluate(Item("Football players strike during final. Bond investors watch"));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
    }

    [Theory]
    [InlineData("US imposes sanctions on Iran and oil prices rise", "OIL", "asset")]
    [InlineData("OPEC cuts oil supply", "OIL", "asset")]
    [InlineData("NATO announces restrictions after Iran attack", "MARKET", "market")]
    [InlineData("SEC announces sanctions on Iran as markets fall", "MARKET", "market")]
    public void 기관_약어는_회사로_간주해_거시대상을_덮지_않는다(string title, string targetId, string targetKind)
    {
        var result = Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        var target = Assert.Single(result.Targets);
        Assert.Equal(targetId, target.Id);
        Assert.Equal(targetKind, target.Kind);
    }

    [Fact]
    public void 한국은행_금리_사건은_비미국_통화정책으로_review에_남긴다()
    {
        var result = Evaluate(Item("한국은행 금리 인상 발표"));

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("non_us_macro", result.Reason);
        Assert.Equal("한국은행", result.Actor);
        var target = Assert.Single(result.Targets);
        Assert.Equal(NewsSymbols.Market, target.Id);
        Assert.Equal("market", target.Kind);
    }

    [Fact]
    public void 회사사건은_사건동사에_가까운_영향대상만_direct로_남긴다()
    {
        var result = Evaluate(Item("NVIDIA Corp comments as Tesla Inc recalls vehicles"));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        var target = Assert.Single(result.Targets);
        Assert.Equal("TESLA INC", target.Id);
        Assert.Equal("direct", target.Relation);
    }

    NewsRelevanceAssessment Evaluate(NewsFeedItem item) => _policy.Evaluate(item, NewsRelevanceContext.Empty);

    static NewsFeedItem Item(string title) => new("id", title, "", "SBH", DateTimeOffset.UtcNow, []);

    [Fact]
    public void 스포츠_섹션의_연예_기사는_평가액이_있어도_제외한다()
    {
        var item = new NewsFeedItem("sweeney",
            "Jealous media attack Sydney Sweeney's sports ad while her $2B valuation proves she's the ultimate boss",
            "Sydney Sweeney's Novig stake surged from $500 million to $2 billion after her viral sports ad sparked fierce debate over women's sports imagery.",
            "Fox News", DateTimeOffset.UtcNow, [],
            Url: "https://www.foxnews.com/outkick-sports/jealous-columnists-attack-sydney-sweeney-sports-ad-2b-valuation-proves-ultimate-boss");

        var result = Evaluate(item);

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_section", result.Reason);
    }

    [Fact]
    public void 섹션_URL이_없어도_연예_스포츠_본문은_포함되지_않는다()
    {
        var result = Evaluate(new NewsFeedItem("sweeney", "Jealous media attack Sydney Sweeney's sports ad while her $2B valuation proves she's the ultimate boss",
            "Sydney Sweeney's Novig stake surged from $500 million to $2 billion after her viral sports ad.", "Fox News", DateTimeOffset.UtcNow, []));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
    }

    [Theory]
    [InlineData("https://www.foxnews.com/sports/nfl-week-3", true)]
    [InlineData("https://www.foxnews.com/outkick-betting/promo-code", true)]
    [InlineData("https://www.foxnews.com/entertainment/stallone", true)]
    [InlineData("https://www.foxnews.com/lifestyle/news-quiz", true)]
    [InlineData("https://www.foxnews.com/food-drink/deli-meat", true)]
    [InlineData("https://www.foxnews.com/travel/noahs-ark", true)]
    [InlineData("https://www.foxnews.com/politics/fed-rate-decision", false)]
    [InlineData("https://www.foxnews.com/world/iran-strike", false)]
    [InlineData("https://www.sbhnews.com/news/fed-rate-2026-10-02", false)]
    [InlineData(null, false)]
    public void 섹션_URL_규칙은_연예_스포츠_생활만_가른다(string? url, bool nonMarket)
    {
        Assert.Equal(nonMarket, NewsRelevancePolicy.IsNonMarketSection(url));
    }

    [Theory]
    [InlineData("Fed raises interest rates by a quarter point as inflation stays high", "https://www.foxnews.com/politics/fed-raises-rates", "monetary_policy")]
    [InlineData("Apple Inc reports record earnings and raises guidance", "https://www.foxnews.com/us/apple-earnings", "guidance_earnings")]
    [InlineData("Trump imposes new tariffs on China and stocks fall", "https://www.foxnews.com/world/trump-tariffs", "macro_policy")]
    public void 영문_시장_기사는_섹션_게이트를_지나_포함된다(string title, string url, string kind)
    {
        var result = Evaluate(new NewsFeedItem("id", title, "", "Fox News", DateTimeOffset.UtcNow, [], Url: url));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(kind, result.EventKind);
    }

    [Theory]
    [InlineData("Fanatics Sportsbook promo code: bet $20, get $350 on NFL Week 3")]
    [InlineData("Hollywood star stuns on the red carpet ahead of box office weekend")]
    public void 영문_스포츠_연예_키워드는_비시장_문맥으로_제외한다(string title)
    {
        var result = Evaluate(Item(title));

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }
}
