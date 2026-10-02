using System.Text.RegularExpressions;

namespace Astra.Server.Application;

/// <summary>SBH 관련성 판정 키워드 사전. 키워드를 바꾸면 <see cref="Version"/>을 올린다(§A R6, #310).</summary>
public static class NewsRelevanceLexicon
{
    public const string Version = "lexicon-2026-10-02";

    const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    const RegexOptions CaseSensitive = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    const string FedAcronym = @"(?:Fed|FED)(?!\s+[Uu][Pp]\b)";

    public static readonly Regex NonMarket = new(
        @"\b(football|soccer|premier league|champions league|world cup|scor(?:e|es|ed|ing)\s+(?:a\s+|the\s+|his\s+|her\s+|their\s+)?(?:winning\s+|late\s+)?goals?|(?:football|soccer)\s+match(?:es)?|match\s+against|actor|actress|celebrity|movie|film|music|concert)\b"
        + @"|축구|프리미어리그|챔피언스리그|월드컵|골을?\s*(?:넣|기록)|배우|연예|영화|가수|콘서트", IgnoreCase);

    public static readonly Regex FinancialQualifier = new(
        @"\b(stocks?|equity|investors?|tariffs?|sanctions?|inflation|Federal Reserve|Treasur(?:y|ies)|bond yields?|interest rates?|payrolls?)\b"
        + @"|주가|증시|투자자|관세|제재|물가|연준|국채|채권금리|금리|소비자물가|생산자물가|고용|국내총생산", IgnoreCase);

    public static readonly Regex MacroTarget = new(
        @"\b(Trump|China|Chinese|Beijing|Iran|Iranian|Tehran|Strait of Hormuz|Hormuz|oil|crude|Brent|Treasur(?:y|ies)|bonds?|yields?|interest rates?|rate (?:hike|cut|decision)s?|Federal Reserve|Saudi(?: Arabia)?|Saudis|Houthi(?:s)?|Yemen(?:i)?|missiles?|airstrikes?|air strikes?|(?:missile|drone|military) strikes?|payrolls?|jobless claims?|durable goods)\b"
        + @"|트럼프|중국|이란|호르무즈|유가|원유|국채|채권금리|채권|기준금리|금리|연준|사우디(?:아라비아)?|후티|예멘|미사일|공습|소비자물가|생산자물가|고용|실업수당|내구재|구매관리자지수|국내총생산", IgnoreCase);

    public static readonly Regex MacroAcronym = new(@"\b(?:" + FedAcronym + @"|CPI|PPI|PMI|GDP|WTI)\b", CaseSensitive);

    public static readonly Regex MacroAction = new(
        @"\b(announce[ds]?|impose[ds]?|raise[ds]?|cut[s]?|hold[s]?|increase[ds]?|decrease[ds]?|rise[sn]?|rose|fall[s]?|fell|surge[ds]?|drop(?:ped|s)?|attack(?:ed|s)?|strike[sd]?|struck|airstrike[sd]?|launch(?:ed|es)?|fire[sd]?|block(?:ed|s)?|close[sd]?|disrupt(?:ed|s)?|resume[ds]?|release[sd]?|report(?:ed|s)?|beat[s]?|miss(?:ed|es)?|expand(?:ed|s)?|restrict(?:ed|s)?|signal(?:ed|s)?)\b"
        + @"|발표|부과|인상|인하|동결|상승|하락|급등|급락|공격|타격|공습|발사|봉쇄|폐쇄|차질|재개|확대|축소|제한|상회|하회", IgnoreCase);

    public static readonly Regex CompanyAction = new(
        @"\b(sign(?:ed|s)?|win[s]?|won|award(?:ed|s)?|contracts?|cancel(?:led|s)?|terminate[ds]?|renew(?:ed|s)?|price\s+(?:increase|cut)|raise[sd]?\s+prices?|cut[s]?\s+prices?|earnings|revenue|profit|guidance|forecast|invest(?:s|ed|ment)?|capex|launch(?:ed|es)?|recall(?:ed|s)?|discontinue[ds]?|demand|supply|shortage|cyberattack|breach(?:ed)?|vulnerability|regulat(?:or|ion)|lawsuit|settle[ds]?|fine[sd]?|sanction(?:ed|s)?|offering|convertible|capital raise|buyback|dividend|acquire[sd]?|merger)\b"
        + @"|계약|수주|해지|갱신|가격\s*(?:인상|인하)|실적|매출|이익|가이던스|전망|투자|설비투자|출시|리콜|단종|수요|공급|부족|사이버(?:공격|보안)|침해|취약점|규제|소송|벌금|제재|증자|전환사채|자본조달|자사주|배당|인수|합병", IgnoreCase);

    public static readonly Regex CompanySuffix = new(
        @"\b[A-Z][A-Za-z&.-]+(?:\s+[A-Z][A-Za-z&.-]+){0,3}\s+(?:Inc\.?|Corp\.?|Corporation|Ltd\.?|PLC|Holdings|Systems|Technologies|Electronics|Group)\b|(?:주식회사|㈜)\s*[가-힣A-Za-z0-9]+",
        CaseSensitive);

    public static readonly Regex Institution = new(@"\bFederal Reserve\b|한국은행|금융위원회|금융감독원", IgnoreCase);
    public static readonly Regex InstitutionAcronym = new(@"\b(?:" + FedAcronym + @"|SEC|NATO|OPEC)\b", CaseSensitive);
    public static readonly Regex AmbiguousWord = new(@"\b(rate|strike|market|bond)\b|금리|파업|시장|채권", IgnoreCase);
    public static readonly Regex Clause = new(@"[^.;!?。！？]+", IgnoreCase);

    public static readonly Regex GeopoliticalAction = new(@"attack|strike|struck|airstrike|launch|fire|block|close|disrupt|공격|타격|공습|발사|봉쇄|폐쇄|차질", IgnoreCase);
    public static readonly Regex GeopoliticalTarget = new(@"Iran|Tehran|Hormuz|Saudi|Houthi|Yemen|oil|crude|Brent|WTI|missile|strike|이란|호르무즈|사우디|후티|예멘|유가|원유|미사일|공습", IgnoreCase);
    public static readonly Regex OilAsset = new(@"\b(oil|crude|Brent|WTI)\b|유가|원유", IgnoreCase);
    public static readonly Regex TreasuryAsset = new(@"\b(Treasur(?:y|ies)|bonds?|yields?)\b|국채|채권금리|채권", IgnoreCase);
    public static readonly Regex MacroReleaseKind = new(@"CPI|PPI|payroll|claims|durable|PMI|GDP|소비자|생산자|고용|실업|내구재|구매관리|국내총", IgnoreCase);
    public static readonly Regex MonetaryKind = new(@"Fed|rate|Treasur|bond|yield|금리|연준|국채|채권", IgnoreCase);
    public static readonly Regex GeopoliticalContext = new(@"missile|strike|airstrike|attack|war|미사일|공습|공격|전쟁|제재", IgnoreCase);

    public static readonly Regex ContractKind = new(@"contract|sign|win|won|award|cancel|terminate|renew|계약|수주|해지|갱신", IgnoreCase);
    public static readonly Regex EarningsKind = new(@"guidance|earnings|revenue|profit|forecast|실적|매출|이익|가이던스|전망", IgnoreCase);
    public static readonly Regex CyberKind = new(@"cyber|breach|vulnerab|사이버|침해|취약", IgnoreCase);
    public static readonly Regex FinancingKind = new(@"offering|convertible|capital raise|증자|전환사채|자본조달", IgnoreCase);
    public static readonly Regex RegulatoryKind = new(@"regulat|lawsuit|fine|sanction|규제|소송|벌금|제재", IgnoreCase);
}
