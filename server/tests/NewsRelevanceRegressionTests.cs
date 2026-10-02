using Astra.Server.Application;
using Astra.Server.Domain.News;
using Xunit;

namespace Astra.Server.Tests;

public sealed class NewsRelevanceRegressionTests
{
    readonly NewsRelevancePolicy _policy = new();

    [Theory]
    [InlineData("양국 정상회담 자료에서 특정 문구가 제외됐다", "상회")]
    [InlineData("대표단이 일정을 확인하며 방문을 마쳤다", "인하")]
    [InlineData("지난달 상승률은 2.9%로 둔화했다", "상승")]
    public void 한국어_동작어는_다른_단어의_부분문자열로_매치하지_않는다(string title, string action)
    {
        var result = Evaluate(title);

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
        Assert.NotEqual(action, result.Action);
    }

    [Theory]
    [InlineData("금리 인하 기대에 국채 금리 하락, 미 증시 상승")]
    [InlineData("연준이 기준금리를 인하했다")]
    [InlineData("미 국채 금리가 급등하며 증시가 하락했다")]
    public void 활용어미나_조사가_붙은_동작어는_인식한다(string title)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Fact]
    public void 공습은_지정학_대상이_없으면_자기확정하지_않는다()
    {
        var result = Evaluate("인접국이 국경 지역을 공습해 민간인이 사망했다");

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Fact]
    public void 공습은_지정학_대상과_함께면_포함한다()
    {
        var result = Evaluate("이란 정유시설 공습으로 원유 공급 차질");

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal("geopolitical", result.EventKind);
    }

    [Theory]
    [InlineData("대화 재개 노력을 환영했다")]
    [InlineData("항공 노선 재개를 촉구했다")]
    public void 재개는_경제_군사_활동_명사가_앞에_없으면_동작어가_아니다(string title)
    {
        var result = Evaluate("이란 " + title);

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Fact]
    public void 폭격_재개는_지정학_동작어다()
    {
        var result = Evaluate("이란 휴전안 거부…선거 후 폭격 재개 가능");

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Theory]
    [InlineData("HD현대중공업이 수주 계약을 체결했다", "HD")]
    [InlineData("포드 주지사가 예산 투자 계획을 발표했다", "F")]
    [InlineData("비자 취소로 유학생 계약이 해지됐다", "V")]
    public void 짧은_전역_별칭은_주어_표지_없이_엔티티로_매치하지_않는다(string title, string symbol)
    {
        var result = _policy.Evaluate(Item(title), NewsRelevanceContext.Create([]));

        Assert.DoesNotContain(result.Targets, x => x.Id == symbol);
        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Theory]
    [InlineData("애플, 특허소송 배상 평결", "AAPL", "regulatory")]
    [InlineData("마이크론 사상 최대 실적 달성", "MU", "guidance_earnings")]
    [InlineData("세일즈포스 주가 3.1% 급등, 매출 가이던스 상향", "CRM", "company_action")]
    public void 전역_별칭은_사건동사와_결합하면_기업사건으로_포함한다(string title, string symbol, string kind)
    {
        var result = _policy.Evaluate(Item(title), NewsRelevanceContext.Create([]));

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(kind, result.EventKind);
        Assert.Equal(symbol, Assert.Single(result.Targets).Id);
    }

    [Fact]
    public void 괄호_티커는_사전에_있는_심볼만_대상으로_승격한다()
    {
        var known = _policy.Evaluate(Item("알파벳(GOOGL) 주가 3% 상승, 클라우드 매출 증가"), NewsRelevanceContext.Create([]));
        var unknown = _policy.Evaluate(Item("토큰증권(STO) 시장 개막 앞두고 제도 정비 시급"), NewsRelevanceContext.Create([]));

        Assert.Equal(NewsRelevanceDecisions.Include, known.Decision);
        Assert.Equal("GOOGL", Assert.Single(known.Targets).Id);
        Assert.DoesNotContain(unknown.Targets, x => x.Id == "STO");
    }

    [Theory]
    [InlineData("전국 아침 기온 하락, 강풍 주의 전망")]
    [InlineData("지역 상가가 산업 거점으로 재탄생…규제 개선 논의")]
    public void 대상_없는_동작어_단독은_review가_아니라_exclude다(string title)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("action_without_target", result.Reason);
    }

    [Theory]
    [InlineData("호주중앙은행(RBA)이 현금금리를 4.6%로 인상했다")]
    [InlineData("한국은행은 10월 소비자물가 상승이 이어질 것으로 전망했다")]
    [InlineData("아이슬란드 인플레이션 5.9%로 상승")]
    public void 비미국_금리_지표_사건은_review로_강등한다(string title)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("non_us_macro", result.Reason);
    }

    [Theory]
    [InlineData("미국 9월 소비자심리지수 48.1…기대 인플레이션 4.6%로 급등", "macro_release")]
    [InlineData("미 소비심리 위축… 미시간대 지수 48.1로 하락", "macro_release")]
    [InlineData("트럼프 행정부, 연비 규제 완화…전기차 의무화 폐기", "macro_policy")]
    [InlineData("미 재무부, 이란 산업에 대규모 제재 단행", "geopolitical")]
    [InlineData("트럼프·시진핑, 11월 추가 정상회담 개최 합의", "macro_policy")]
    public void 미국_지표와_정책_동작어를_인식한다(string title, string kind)
    {
        var result = Evaluate(title);

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
        Assert.Equal(kind, result.EventKind);
    }

    [Theory]
    [InlineData("의원들이 트럼프 대화 재개 노력을 지지했다")]
    [InlineData("대학생들의 트럼프 관련 발언 논란으로 긴급 조사")]
    public void 트럼프는_정책_시장_문맥_없이는_거시_대상이_아니다(string title)
    {
        var result = Evaluate(title);

        Assert.NotEqual(NewsRelevanceDecisions.Include, result.Decision);
        Assert.NotEqual("트럼프", result.Actor);
    }

    [Fact]
    public void 제목에_사건_단서가_없으면_본문_부수절만으로_확정하지_않는다()
    {
        var item = new NewsFeedItem("id", "부통령 탄핵심판 증언 이어져", "증인이 총기에 대해 증언했다. 수송 파업과 유가 하락 소식도 전해졌다.",
            "SBH", DateTimeOffset.UtcNow, []);

        var result = _policy.Evaluate(item, NewsRelevanceContext.Empty);

        Assert.Equal(NewsRelevanceDecisions.Review, result.Decision);
        Assert.Equal("title_lacks_event", result.Reason);
    }

    [Fact]
    public void 소수점은_절_경계가_아니다()
    {
        var result = Evaluate("미 국채금리 5.3%로 급등하며 증시 하락");

        Assert.Equal(NewsRelevanceDecisions.Include, result.Decision);
    }

    [Fact]
    public void 예측시장_확률_변동은_비시장_문맥으로_제외한다()
    {
        var item = new NewsFeedItem("id", "이란 관련 베팅 확률 17.5%로 급락", "해외 예측시장에서 해당 확률이 24시간 만에 20%p 하락했다.",
            "SBH", DateTimeOffset.UtcNow, []);

        var result = _policy.Evaluate(item, NewsRelevanceContext.Empty);

        Assert.Equal(NewsRelevanceDecisions.Exclude, result.Decision);
        Assert.Equal("non_market_context", result.Reason);
    }

    NewsRelevanceAssessment Evaluate(string title) => _policy.Evaluate(Item(title), NewsRelevanceContext.Empty);

    static NewsFeedItem Item(string title) => new("id", title, "", "SBH", DateTimeOffset.UtcNow, []);
}
