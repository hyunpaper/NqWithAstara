using System.Text.RegularExpressions;

namespace Astra.Server.Application;

/// <summary>SBH 관련성 판정 키워드 사전. 키워드를 바꾸면 <see cref="Version"/>을 올린다(§A R6, #310).</summary>
public static class NewsRelevanceLexicon
{
    public const string Version = "lexicon-2026-10-02.2";

    const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    const RegexOptions CaseSensitive = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    const string FedAcronym = @"(?:Fed|FED)(?!\s+[Uu][Pp]\b)";
    const string KoreanVerbEnding = @"(?=$|[^가-힣]|[했하할한함해된되될됐돼됨세시을를이가은는에도로])";
    const string PolicyAdjustAction = @"(?<=(?:규제|제재|관세|제한|기준|통제|압박|조치|의무화)\s?)(?:완화|강화|해제)";
    const string ResumeAction = @"(?<=(?:수출|생산|가동|운항|항행|폭격|공습|협상|회담|거래|공급)\s?)재개";

    public static readonly Regex NonMarket = new(
        @"\b(football|soccer|premier league|champions league|world cup|scor(?:e|es|ed|ing)\s+(?:a\s+|the\s+|his\s+|her\s+|their\s+)?(?:winning\s+|late\s+)?goals?|(?:football|soccer)\s+match(?:es)?|match\s+against|actor|actress|celebrity|movie|film|music|concert)\b"
        + @"|축구|프리미어리그|챔피언스리그|월드컵|골을?\s*(?:넣|기록)|배우|연예|영화|가수|콘서트|예측시장", IgnoreCase);

    public static readonly Regex FinancialQualifier = new(
        @"\b(stocks?|equity|investors?|tariffs?|sanctions?|inflation|Federal Reserve|Treasur(?:y|ies)|bond yields?|interest rates?|payrolls?)\b"
        + @"|주가|증시|투자자|관세|제재|물가|연준|국채|채권금리|금리|소비자물가|생산자물가|고용|국내총생산", IgnoreCase);

    public static readonly Regex MacroTarget = new(
        @"\b(Trump|China|Chinese|Beijing|Iran|Iranian|Tehran|Strait of Hormuz|Hormuz|oil|crude|Brent|Treasur(?:y|ies)|bonds?|yields?|interest rates?|rate (?:hike|cut|decision)s?|Federal Reserve|Saudi(?: Arabia)?|Saudis|Houthi(?:s)?|Yemen(?:i)?|payrolls?|jobless claims?|durable goods)\b"
        + @"|트럼프|중국|(?<![가-힣])이란|호르무즈|유가|원유|국채|채권금리|채권|기준금리|금리|연준|사우디(?:아라비아)?|후티|예멘|시진핑|정상회담|미중|무역협상|소비자물가|생산자물가|고용|실업수당|내구재|구매관리자지수|국내총생산"
        + @"|소비자심리(?:지수)?|소비심리|미시간대|기대\s*인플레이션|인플레이션|고물가|비농업|신규\s*실업|컨퍼런스보드|소매판매|점도표|양적긴축|달러\s*인덱스|환율", IgnoreCase);

    public static readonly Regex MacroAcronym = new(@"\b(?:" + FedAcronym + @"|CPI|PPI|PMI|GDP|WTI|FOMC|ISM)\b", CaseSensitive);

    public static readonly Regex MacroAction = new(
        @"\b(announce[ds]?|impose[ds]?|raise[ds]?|cut[s]?|hold[s]?|increase[ds]?|decrease[ds]?|rise[sn]?|rose|fall[s]?|fell|surge[ds]?|drop(?:ped|s)?|attack(?:ed|s)?|strike[sd]?|struck|airstrike[sd]?|launch(?:ed|es)?|fire[sd]?|block(?:ed|s)?|close[sd]?|disrupt(?:ed|s)?|resume[ds]?|release[sd]?|report(?:ed|s)?|beat[s]?|miss(?:ed|es)?|expand(?:ed|s)?|restrict(?:ed|s)?|signal(?:ed|s)?|ease[sd]?|tighten(?:ed|s)?|repeal(?:ed|s)?|revoke[sd]?|suspend(?:ed|s)?|halt(?:ed|s)?)\b"
        + @"|(?:발표|부과|인상|(?<!확)인하|동결|상승|하락|급등|급락|공격|타격|공습|발사|봉쇄|폐쇄|차질|확대|축소|제한|(?<!정)상회|하회|폐지|폐기|철회|단행|합의|타결|결렬|중단|"
        + PolicyAdjustAction + "|" + ResumeAction + ")" + KoreanVerbEnding, IgnoreCase);

    public static readonly Regex CompanyAction = new(
        @"\b(sign(?:ed|s)?|win[s]?|won|award(?:ed|s)?|contracts?|cancel(?:led|s)?|terminate[ds]?|renew(?:ed|s)?|price\s+(?:increase|cut)|raise[sd]?\s+prices?|cut[s]?\s+prices?|earnings|revenue|profit|guidance|forecast|invest(?:s|ed|ment)?|capex|launch(?:ed|es)?|recall(?:ed|s)?|discontinue[ds]?|demand|supply|shortage|cyberattack|breach(?:ed)?|vulnerability|regulat(?:or|ion)|lawsuit|settle[ds]?|fine[sd]?|sanction(?:ed|s)?|offering|convertible|capital raise|buyback|dividend|acquire[sd]?|merger)\b"
        + @"|계약|수주|해지|갱신|가격\s*(?:인상|인하)|실적|매출|이익|가이던스|전망|투자|설비투자|출시|리콜|단종|수요|공급|부족|사이버(?:공격|보안)|침해|취약점|규제|소송|벌금|제재|증자|전환사채|자본조달|자사주|배당|인수|합병|주가|공개", IgnoreCase);

    public static readonly Regex CompanySuffix = new(
        @"\b[A-Z][A-Za-z&.-]+(?:\s+[A-Z][A-Za-z&.-]+){0,3}\s+(?:Inc\.?|Corp\.?|Corporation|Ltd\.?|PLC|Holdings|Systems|Technologies|Electronics|Group)\b|(?:주식회사|㈜)\s*[가-힣A-Za-z0-9]+",
        CaseSensitive);

    public static readonly Regex ParenTicker = new(@"(?<=[가-힣A-Za-z])\s?\(([A-Z]{1,5}(?:\.[A-Z])?)\)", CaseSensitive);

    public static readonly Regex Institution = new(@"\bFederal Reserve\b|한국은행|금융위원회|금융감독원", IgnoreCase);
    public static readonly Regex InstitutionAcronym = new(@"\b(?:" + FedAcronym + @"|SEC|NATO|OPEC)\b", CaseSensitive);
    public static readonly Regex AmbiguousWord = new(@"\b(rate|strike|market|bond)\b|금리|파업|시장|채권", IgnoreCase);
    public static readonly Regex Clause = new(@"(?:[^.;!?。！？]|(?<=\d)\.(?=\d))+", IgnoreCase);
    public static readonly Regex SentenceBreak = new(@"[;!?。！？]|(?<!\d)\.|\.(?!\d)", IgnoreCase);

    public static readonly Regex GeopoliticalAction = new(@"attack|strike|struck|airstrike|launch|fire|block|close|disrupt|공격|타격|공습|발사|봉쇄|폐쇄|차질", IgnoreCase);
    public static readonly Regex GeopoliticalTarget = new(@"Iran|Tehran|Hormuz|Saudi|Houthi|Yemen|oil|crude|Brent|WTI|이란|호르무즈|사우디|후티|예멘|유가|원유", IgnoreCase);
    public static readonly Regex OilAsset = new(@"\b(oil|crude|Brent|WTI)\b|유가|원유", IgnoreCase);
    public static readonly Regex TreasuryAsset = new(@"\b(Treasur(?:y|ies)|bonds?|yields?)\b|국채|채권금리|채권", IgnoreCase);
    public static readonly Regex MacroReleaseKind = new(@"CPI|PPI|payroll|claims|durable|PMI|GDP|ISM|소비자|소비심리|미시간|생산자|고용|실업|내구재|구매관리|국내총|인플레이션|고물가|비농업|소매판매|컨퍼런스", IgnoreCase);
    public static readonly Regex TrumpTarget = new(@"Trump|트럼프", IgnoreCase);
    public static readonly Regex TrumpContext = new(
        @"tariff|sanction|regulat|executive order|Fed\b|rate|oil|trade|China|Iran|administration|관세|제재|규제|행정명령|행정부|연준|금리|유가|비축유|감세|무역|중국|이란|시진핑|정상회담|수출|수입|달러|국채|증시|주가|석유|에너지|반도체|전기차|연비", IgnoreCase);
    public static readonly Regex NonUsMarker = new(@"\b(RBA|ECB|BOJ|BOE|BOK)\b|한은|한국은행|한국|국내|호주|유럽중앙은행|유럽|일본은행|일본|영란은행|영국|인민은행|인도|캐나다|브라질|중앙은행", IgnoreCase);
    public static readonly Regex UsMarker = new(@"(?<![가-힣])미(?:국|[\s·-]|$)|연준|" + FedAcronym + @"|FOMC|뉴욕|월가|트럼프|백악관|워싱턴|미시간|나스닥|다우|S&P|\bU\.?S\.?\b|Federal Reserve|Treasury|Wall Street", IgnoreCase);
    public static readonly Regex MonetaryKind = new(@"Fed|rate|Treasur|bond|yield|금리|연준|국채|채권", IgnoreCase);
    public static readonly Regex GeopoliticalContext = new(@"missile|strike|airstrike|attack|war|미사일|공습|공격|전쟁|제재", IgnoreCase);

    public static readonly Regex ContractKind = new(@"contract|sign|win|won|award|cancel|terminate|renew|계약|수주|해지|갱신", IgnoreCase);
    public static readonly Regex EarningsKind = new(@"guidance|earnings|revenue|profit|forecast|실적|매출|이익|가이던스|전망", IgnoreCase);
    public static readonly Regex CyberKind = new(@"cyber|breach|vulnerab|사이버|침해|취약", IgnoreCase);
    public static readonly Regex FinancingKind = new(@"offering|convertible|capital raise|증자|전환사채|자본조달", IgnoreCase);
    public static readonly Regex RegulatoryKind = new(@"regulat|lawsuit|fine|sanction|규제|소송|벌금|제재", IgnoreCase);
}
