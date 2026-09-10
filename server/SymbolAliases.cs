namespace Astra.Server;

/// <summary>Toss Open API에는 이름 검색이 없어 한글 종목명은 로컬 별칭 사전으로 심볼로 변환한다. 후보는 반드시 stocks API로 재검증된다.</summary>
public static class SymbolAliases
{
    static readonly (string Alias, string Symbol)[] Entries =
    [
        ("애플", "AAPL"), ("마이크로소프트", "MSFT"), ("마소", "MSFT"), ("엔비디아", "NVDA"), ("알파벳", "GOOGL"), ("구글", "GOOGL"),
        ("아마존", "AMZN"), ("메타", "META"), ("페이스북", "META"), ("테슬라", "TSLA"), ("브로드컴", "AVGO"), ("넷플릭스", "NFLX"),
        ("오라클", "ORCL"), ("세일즈포스", "CRM"), ("어도비", "ADBE"), ("인텔", "INTC"), ("시스코", "CSCO"), ("퀄컴", "QCOM"),
        ("마이크론", "MU"), ("텍사스인스트루먼트", "TXN"), ("어플라이드머티리얼즈", "AMAT"), ("램리서치", "LRCX"), ("에이엠디", "AMD"),
        ("티에스엠씨", "TSM"), ("대만반도체", "TSM"), ("에이에스엠엘", "ASML"), ("슈퍼마이크로", "SMCI"), ("슈마컴", "SMCI"), ("델", "DELL"),
        ("팔란티어", "PLTR"), ("스노우플레이크", "SNOW"), ("데이터독", "DDOG"), ("몽고디비", "MDB"), ("서비스나우", "NOW"),
        ("크라우드스트라이크", "CRWD"), ("팔로알토", "PANW"), ("포티넷", "FTNT"), ("지스케일러", "ZS"), ("클라우드플레어", "NET"),
        ("쇼피파이", "SHOP"), ("스포티파이", "SPOT"), ("우버", "UBER"), ("리프트", "LYFT"), ("에어비앤비", "ABNB"), ("부킹홀딩스", "BKNG"),
        ("도어대시", "DASH"), ("로블록스", "RBLX"), ("유니티", "U"), ("코인베이스", "COIN"), ("로빈후드", "HOOD"), ("소파이", "SOFI"),
        ("페이팔", "PYPL"), ("블록", "XYZ"), ("스퀘어", "XYZ"), ("마이크로스트래티지", "MSTR"), ("스트래티지", "MSTR"),
        ("아이온큐", "IONQ"), ("리게티", "RGTI"), ("디웨이브", "QBTS"), ("조비", "JOBY"), ("아처", "ACHR"), ("로켓랩", "RKLB"),
        ("루시드", "LCID"), ("리비안", "RIVN"), ("니오", "NIO"), ("알리바바", "BABA"), ("핀둬둬", "PDD"), ("징동", "JD"), ("바이두", "BIDU"),
        ("넥스트에라", "NEE"), ("콘스텔레이션에너지", "CEG"), ("비스트라", "VST"), ("오클로", "OKLO"), ("뉴스케일", "SMR"), ("카메코", "CCJ"),
        ("네비우스", "NBIS"), ("아리스타", "ANET"), ("버티브", "VRT"), ("이튼", "ETN"), ("코히런트", "COHR"), ("루멘텀", "LITE"),
        ("크레도", "CRDO"), ("아스테라랩스", "ALAB"), ("마벨", "MRVL"), ("마블테크놀로지", "MRVL"), ("온세미", "ON"),
        ("아날로그디바이스", "ADI"), ("시놉시스", "SNPS"), ("케이던스", "CDNS"), ("앱러빈", "APP"), ("트레이드데스크", "TTD"),
        ("일라이릴리", "LLY"), ("릴리", "LLY"), ("노보노디스크", "NVO"), ("화이자", "PFE"), ("머크", "MRK"), ("애브비", "ABBV"),
        ("존슨앤존슨", "JNJ"), ("유나이티드헬스", "UNH"), ("인튜이티브서지컬", "ISRG"), ("버크셔해서웨이", "BRK.B"), ("버크셔", "BRK.B"),
        ("제이피모건", "JPM"), ("뱅크오브아메리카", "BAC"), ("웰스파고", "WFC"), ("골드만삭스", "GS"), ("모건스탠리", "MS"),
        ("씨티그룹", "C"), ("비자", "V"), ("마스터카드", "MA"), ("아메리칸익스프레스", "AXP"), ("아멕스", "AXP"),
        ("월마트", "WMT"), ("코스트코", "COST"), ("타겟", "TGT"), ("홈디포", "HD"), ("맥도날드", "MCD"), ("스타벅스", "SBUX"),
        ("치폴레", "CMG"), ("나이키", "NKE"), ("룰루레몬", "LULU"), ("코카콜라", "KO"), ("펩시", "PEP"), ("펩시코", "PEP"),
        ("프록터앤갬블", "PG"), ("피앤지", "PG"), ("디즈니", "DIS"), ("컴캐스트", "CMCSA"), ("티모바일", "TMUS"), ("버라이즌", "VZ"),
        ("보잉", "BA"), ("록히드마틴", "LMT"), ("노스롭그루먼", "NOC"), ("레이시온", "RTX"), ("제너럴다이내믹스", "GD"),
        ("캐터필러", "CAT"), ("존디어", "DE"), ("제너럴일렉트릭", "GE"), ("하니웰", "HON"), ("엑슨모빌", "XOM"), ("셰브론", "CVX"),
        ("옥시덴탈", "OXY"), ("포드", "F"), ("제너럴모터스", "GM"), ("페덱스", "FDX"), ("프리포트맥모란", "FCX"), ("뉴몬트", "NEM"),
        ("시게이트", "STX"), ("웨스턴디지털", "WDC"), ("인튜이트", "INTU"), ("워크데이", "WDAY"), ("아틀라시안", "TEAM"),
        ("레딧", "RDDT"), ("스냅", "SNAP"), ("핀터레스트", "PINS"), ("로쿠", "ROKU"), ("드래프트킹스", "DKNG"),
        ("델타항공", "DAL"), ("유나이티드항공", "UAL"), ("아메리칸항공", "AAL"), ("로얄캐리비안", "RCL"), ("카니발", "CCL"),
        ("스파이", "SPY"), ("에스앤피", "SPY"), ("큐큐큐", "QQQ"), ("나스닥이티에프", "QQQ"), ("러셀이천", "IWM"),
        ("반도체이티에프", "SMH"), ("아크", "ARKK"), ("금이티에프", "GLD"), ("비트코인이티에프", "IBIT"),
    ];

    public static IReadOnlyList<string> Resolve(string query)
    {
        var norm = new string(query.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (norm.Length == 0) return [];
        return Entries
            .Select(e => (e.Alias, e.Symbol, Rank: e.Alias == norm ? 0 : e.Alias.StartsWith(norm, StringComparison.Ordinal) ? 1 : e.Alias.Contains(norm, StringComparison.Ordinal) ? 2 : 3))
            .Where(x => x.Rank < 3)
            .OrderBy(x => x.Rank).ThenBy(x => x.Alias.Length)
            .Select(x => x.Symbol).Distinct().Take(10).ToArray();
    }
}
