using System.Text.RegularExpressions;
using Astra.Server.Domain.News;

namespace Astra.Server.Application;

public interface INewsRelevancePolicy
{
    string Version { get; }
    NewsRelevanceAssessment Evaluate(NewsFeedItem item);
}

public sealed class NewsRelevancePolicy : INewsRelevancePolicy
{
    public const string CurrentVersion = "sbh-relevance-v1";
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    static readonly Regex NonMarket = new(@"\b(football|soccer|premier league|champions league|world cup|goal|match|actor|actress|celebrity|movie|film|music|concert)\b|축구|프리미어리그|챔피언스리그|월드컵|골을?\s*(?:넣|기록)|배우|연예|영화|가수|콘서트", Options);
    static readonly Regex StrongFinancialPath = new(@"\b(stock|equity|investors?|tariff|sanction|economy|economic|inflation|recession|currency|dollar|commodity|futures?|Federal Reserve|Fed|Treasur(?:y|ies)|bond yields?|interest rates?|CPI|PPI|payrolls?|GDP)\b|주가|증시|투자자|무역|관세|제재|경제|물가|경기침체|환율|달러|원자재|선물|연준|국채|채권금리|금리|소비자물가|생산자물가|고용|국내총생산", Options);
    static readonly Regex MacroTarget = new(@"\b(Trump|China|Chinese|Iran|Iranian|Strait of Hormuz|Hormuz|oil|crude|Treasur(?:y|ies)|bond|yield|interest rates?|Federal Reserve|Fed|Saudi(?: Arabia)?|Houthi(?:s)?|Yemen|CPI|PPI|payrolls?|jobless claims?|durable goods|PMI|GDP)\b|트럼프|중국|이란|호르무즈|유가|원유|국채|채권금리|채권|금리|연준|사우디(?:아라비아)?|후티|예멘|미사일|공습|소비자물가|생산자물가|고용|실업수당|내구재|구매관리자지수|국내총생산", Options);
    static readonly Regex MacroAction = new(@"\b(announce[ds]?|impose[ds]?|raise[ds]?|cut[s]?|hold[s]?|increase[ds]?|decrease[ds]?|rise[sn]?|fall[s]?|surge[ds]?|drop(?:ped|s)?|attack(?:ed|s)?|strike[sd]?|airstrike[sd]?|launch(?:ed|es)?|block(?:ed|s)?|close[sd]?|disrupt(?:ed|s)?|resume[ds]?|release[sd]?|report(?:ed|s)?|beat[s]?|miss(?:ed|es)?|expand(?:ed|s)?|restrict(?:ed|s)?)\b|발표|부과|인상|인하|동결|상승|하락|급등|급락|공격|타격|공습|발사|봉쇄|폐쇄|차질|재개|확대|축소|제한|상회|하회", Options);
    static readonly Regex CompanyAction = new(@"\b(sign(?:ed|s)?|win[s]?|award(?:ed|s)?|cancel(?:led|s)?|terminate[ds]?|renew(?:ed|s)?|price\s+(?:increase|cut)|raise[sd]?\s+prices?|cut[s]?\s+prices?|earnings|revenue|profit|guidance|forecast|invest(?:s|ed|ment)?|capex|launch(?:ed|es)?|recall(?:ed|s)?|discontinue[ds]?|demand|supply|shortage|cyberattack|breach(?:ed)?|vulnerability|regulat(?:or|ion)|lawsuit|settle[ds]?|fine[sd]?|sanction(?:ed|s)?|offering|convertible|capital raise|buyback|dividend|acquire[sd]?|merger)\b|계약|수주|해지|갱신|가격\s*(?:인상|인하)|실적|매출|이익|가이던스|전망|투자|설비투자|출시|리콜|단종|수요|공급|부족|사이버(?:공격|보안)|침해|취약점|규제|소송|벌금|제재|증자|전환사채|자본조달|자사주|배당|인수|합병", Options);
    static readonly Regex Company = new(@"\b[A-Z][A-Za-z&.-]+(?:\s+[A-Z][A-Za-z&.-]+){0,3}\s+(?:Inc\.?|Corp\.?|Corporation|Ltd\.?|PLC|Holdings|Systems|Technologies|Electronics|Group)\b|(?:주식회사|㈜)\s*[가-힣A-Za-z0-9]+|[가-힣A-Za-z0-9]+\s*(?:그룹|전자|반도체|에너지|은행|증권|바이오)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly Regex AmbiguousWord = new(@"\b(rate|strike|market|bond)\b|금리|파업|시장|채권", Options);

    public string Version => CurrentVersion;

    public NewsRelevanceAssessment Evaluate(NewsFeedItem item)
    {
        var text = Normalize(string.Join(' ', item.Title, item.Summary, item.Content));
        if (text.Length == 0) return Result(NewsRelevanceDecisions.Exclude, "unknown", "", "", [], "", "empty_text");

        var macroTarget = MacroTarget.Match(text);
        var macroAction = MacroAction.Match(text);
        var companyAction = CompanyAction.Match(text);
        var explicitTargets = Targets(item, text);
        var nonMarket = NonMarket.IsMatch(text);
        var strongFinancialPath = StrongFinancialPath.IsMatch(text);

        if (nonMarket && !strongFinancialPath)
            return Result(NewsRelevanceDecisions.Exclude, "non_market", "", "", [], Evidence(text), "non_market_context");

        if (macroTarget.Success && macroAction.Success)
        {
            var kind = MacroKind(macroTarget.Value, text);
            var targets = explicitTargets.Count > 0 ? explicitTargets : MacroTargets(text, macroTarget.Value);
            return Result(NewsRelevanceDecisions.Include, kind, Actor(text, macroTarget.Value), macroAction.Value,
                targets, Span(text, macroTarget.Index, macroAction.Index), "macro_event_confirmed");
        }

        if (companyAction.Success && explicitTargets.Count > 0)
            return Result(NewsRelevanceDecisions.Include, CompanyKind(companyAction.Value), explicitTargets[0].Id,
                companyAction.Value, explicitTargets, Span(text, companyAction.Index, companyAction.Index),
                "company_event_confirmed");

        if (macroTarget.Success || companyAction.Success || explicitTargets.Count > 0 || AmbiguousWord.IsMatch(text))
            return Result(NewsRelevanceDecisions.Review, "unknown", Actor(text, macroTarget.Value),
                companyAction.Success ? companyAction.Value : macroAction.Value, explicitTargets,
                Evidence(text), "insufficient_actor_action_target_context");

        return Result(NewsRelevanceDecisions.Exclude, "unknown", "", "", [], Evidence(text), "no_market_event");
    }

    NewsRelevanceAssessment Result(string decision, string kind, string actor, string action,
        IReadOnlyList<NewsEventTarget> targets, string evidence, string reason)
        => new(Version, decision, kind, actor.Trim(), action.Trim(), targets, evidence, reason);

    static List<NewsEventTarget> Targets(NewsFeedItem item, string text)
    {
        var targets = new List<NewsEventTarget>();
        foreach (var ticker in item.Tickers.Where(x => !string.IsNullOrWhiteSpace(x)))
            targets.Add(new NewsEventTarget(ticker.Trim().ToUpperInvariant(), "company", "direct", ticker.Trim()));
        foreach (var entity in item.Entities ?? [])
        {
            var id = string.IsNullOrWhiteSpace(entity.Symbol) ? entity.Name : entity.Symbol.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(id)) targets.Add(new NewsEventTarget(id, "company", "direct", entity.Name));
        }
        foreach (Match match in Company.Matches(text))
        {
            var value = match.Value.Trim();
            if (value.Length < 2 || value is "CPI" or "PPI" or "PMI" or "GDP") continue;
            targets.Add(new NewsEventTarget(value.ToUpperInvariant(), "company", "direct", value));
        }
        return targets.DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    static IReadOnlyList<NewsEventTarget> MacroTargets(string text, string evidence)
    {
        if (Regex.IsMatch(text, @"\b(oil|crude)\b|유가|원유", Options))
            return [new("OIL", "asset", "direct", evidence)];
        if (Regex.IsMatch(text, @"\b(Treasur(?:y|ies)|bond|yield)\b|국채|채권금리|채권", Options))
            return [new("TREASURY", "asset", "direct", evidence)];
        return [new(NewsSymbols.Market, "market", "direct", evidence)];
    }

    static string MacroKind(string target, string text)
    {
        if (Regex.IsMatch(target, @"CPI|PPI|payroll|claims|durable|PMI|GDP|소비자|생산자|고용|실업|내구재|구매관리|국내총", Options)) return "macro_release";
        if (Regex.IsMatch(target, @"Fed|rate|Treasur|bond|yield|금리|연준|국채|채권", Options)) return "monetary_policy";
        if (Regex.IsMatch(text, @"missile|strike|airstrike|attack|war|미사일|공습|공격|전쟁|제재", Options)) return "geopolitical";
        return "macro_policy";
    }

    static string CompanyKind(string action)
    {
        if (Regex.IsMatch(action, @"contract|sign|win|award|cancel|terminate|renew|계약|수주|해지|갱신", Options)) return "company_contract";
        if (Regex.IsMatch(action, @"guidance|earnings|revenue|profit|실적|매출|이익|가이던스|전망", Options)) return "guidance_earnings";
        if (Regex.IsMatch(action, @"cyber|breach|vulnerab|사이버|침해|취약", Options)) return "cybersecurity";
        if (Regex.IsMatch(action, @"offering|convertible|capital raise|증자|전환사채|자본조달", Options)) return "financing";
        if (Regex.IsMatch(action, @"regulat|lawsuit|fine|sanction|규제|소송|벌금|제재", Options)) return "regulatory";
        return "company_action";
    }

    static string Actor(string text, string fallback)
        => Company.Match(text) is { Success: true } company ? company.Value : fallback;

    static string Span(string text, int first, int second)
    {
        var start = Math.Max(0, Math.Min(first, second) - 80);
        var length = Math.Min(320, text.Length - start);
        return text.Substring(start, length);
    }

    static string Evidence(string text) => text[..Math.Min(320, text.Length)];
    static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
