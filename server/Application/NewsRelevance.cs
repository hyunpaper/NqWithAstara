using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Astra.Server.Domain;
using Astra.Server.Domain.News;
using static Astra.Server.Application.NewsRelevanceLexicon;

namespace Astra.Server.Application;

public interface INewsRelevancePolicy
{
    string Version { get; }
    NewsRelevanceAssessment Evaluate(NewsFeedItem item, NewsRelevanceContext context);
}

/// <summary>관심종목 symbol·이름·한글 별칭으로 만든 알려진 엔티티 사전(§A R5, #310).</summary>
public sealed class NewsRelevanceContext
{
    const int MinimumSymbolLength = 2;
    const int MinimumNameLength = 3;
    const int MinimumAliasLength = 2;

    NewsRelevanceContext(IReadOnlyList<NewsKnownEntity> entities, string version)
    {
        Entities = entities;
        Version = version;
    }

    public static NewsRelevanceContext Empty { get; } = Create([]);
    public IReadOnlyList<NewsKnownEntity> Entities { get; }
    public string Version { get; }

    public static NewsRelevanceContext Create(IEnumerable<NewsWatchSymbol> watchlist)
    {
        var entities = new List<NewsKnownEntity>();
        foreach (var watch in watchlist.Where(x => !string.IsNullOrWhiteSpace(x.Symbol)))
        {
            var id = watch.Symbol.Trim().TrimStart('$').ToUpperInvariant();
            if (id.Length >= MinimumSymbolLength) entities.Add(NewsKnownEntity.Create(id, id, caseSensitive: true));
            var name = watch.Name?.Trim() ?? "";
            if (name.Length >= MinimumNameLength) entities.Add(NewsKnownEntity.Create(id, name, caseSensitive: false));
            foreach (var alias in SymbolAliases.AliasesFor(id).Where(x => x.Length >= MinimumAliasLength))
                entities.Add(NewsKnownEntity.Create(id, alias, caseSensitive: true));
        }
        var distinct = entities.DistinctBy(x => (x.Id, x.Surface, x.CaseSensitive))
            .OrderBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Surface, StringComparer.Ordinal).ToArray();
        var canonical = string.Join('\n', distinct.Select(x => x.Id + "|" + x.Surface + "|" + (x.CaseSensitive ? "cs" : "ci")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();
        return new NewsRelevanceContext(distinct, "entities:" + hash);
    }
}

public sealed class NewsKnownEntity
{
    const string KoreanParticles = "은는이가을를의와과도로에측서";
    readonly Regex _pattern;

    NewsKnownEntity(string id, string surface, bool caseSensitive)
    {
        Id = id;
        Surface = surface;
        CaseSensitive = caseSensitive;
        var ascii = surface.Any(char.IsAsciiLetterOrDigit);
        var pattern = ascii
            ? @"(?<![A-Za-z0-9])" + Regex.Escape(surface) + @"(?![A-Za-z0-9])"
            : @"(?<![가-힣A-Za-z0-9])" + Regex.Escape(surface) + @"(?=$|[^가-힣]|[" + KoreanParticles + "])";
        _pattern = new Regex(pattern, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant);
    }

    public string Id { get; }
    public string Surface { get; }
    public bool CaseSensitive { get; }

    public static NewsKnownEntity Create(string id, string surface, bool caseSensitive) => new(id, surface, caseSensitive);

    /// <summary>영숫자 경계 안의 언급만 찾는다. 한글 별칭은 뒤에 조사만 허용한다.</summary>
    public IEnumerable<Match> Find(string text) => _pattern.Matches(text);
}

/// <summary>관심종목 watchlist에서 관련성 엔티티 사전을 만든다(§A R5, #310).</summary>
public interface INewsRelevanceContextSource
{
    Task<NewsRelevanceContext> GetAsync(CancellationToken ct);
}

public sealed class WatchlistNewsRelevanceContextSource(ILocalStore store) : INewsRelevanceContextSource
{
    public async Task<NewsRelevanceContext> GetAsync(CancellationToken ct)
    {
        _ = ct;
        var items = await store.Read("watchlist.json", new List<WatchItem>());
        return NewsRelevanceContext.Create(items.Select(x => new NewsWatchSymbol(x.Symbol, x.Name ?? "")));
    }
}

public sealed class NewsRelevancePolicy : INewsRelevancePolicy
{
    public const string CurrentVersion = "sbh-relevance-v2";
    const int MaxEventDistance = 80;

    public string Version => CurrentVersion;

    public static string ComposeVersion(string policyVersion, NewsRelevanceContext context)
        => policyVersion + "+" + context.Version;

    public NewsRelevanceAssessment Evaluate(NewsFeedItem item, NewsRelevanceContext context)
    {
        var version = ComposeVersion(Version, context);
        var text = Normalize(string.Join(' ', item.Title, item.Summary, item.Content));
        if (text.Length == 0) return Result(version, NewsRelevanceDecisions.Exclude, "unknown", "", "", [], "", "empty_text");

        var macroEvent = MacroEvent(text);
        var macroTarget = macroEvent?.Target ?? FirstMatch(text, MacroTarget, MacroAcronym);
        var macroAction = macroEvent?.Action ?? MacroAction.Match(text);
        var companyActions = CompanyAction.Matches(text).Cast<Match>().ToArray();
        var explicitTargets = Targets(item, text, context);

        if (macroEvent is not null)
        {
            var kind = MacroKind(macroTarget.Value, text);
            var targets = MacroTargets(macroTarget.Value);
            return Result(version, NewsRelevanceDecisions.Include, kind, ActorInSpan(text, macroEvent.Value.ClauseStart,
                    macroEvent.Value.ClauseLength, macroTarget.Value, macroAction.Index), macroAction.Value,
                targets, Span(text, macroTarget.Index, macroAction.Index), "macro_event_confirmed");
        }

        foreach (var action in companyActions)
        {
            var company = CompanyTargetsNearAction(item, text, action, context);
            if (company.Targets.Count == 0) continue;
            return Result(version, NewsRelevanceDecisions.Include, CompanyKind(action.Value), company.Actor,
                action.Value, company.Targets, Span(text, action.Index, action.Index), "company_event_confirmed");
        }

        if (NonMarket.IsMatch(text))
            return Result(version, NewsRelevanceDecisions.Exclude, "non_market", "", "", [], Evidence(text), "non_market_context");

        if (macroTarget.Success || companyActions.Length > 0 || explicitTargets.Count > 0 || AmbiguousWord.IsMatch(text))
            return Result(version, NewsRelevanceDecisions.Review, "unknown", Actor(text, macroTarget.Value),
                companyActions.Length > 0 ? companyActions[0].Value : macroAction.Value, explicitTargets,
                Evidence(text), "insufficient_actor_action_target_context");

        return Result(version, NewsRelevanceDecisions.Exclude, "unknown", "", "", [], Evidence(text), "no_market_event");
    }

    static NewsRelevanceAssessment Result(string version, string decision, string kind, string actor, string action,
        IReadOnlyList<NewsEventTarget> targets, string evidence, string reason)
        => new(version, decision, kind, actor.Trim(), action.Trim(), targets, evidence, reason);

    static List<NewsEventTarget> Targets(NewsFeedItem item, string text, NewsRelevanceContext context)
    {
        var targets = StructuredTargets(item);
        foreach (Match match in CompanySuffix.Matches(text))
        {
            var value = match.Value.Trim();
            if (value.Length < 2) continue;
            targets.Add(new NewsEventTarget(value.ToUpperInvariant(), "company", "direct", value));
        }
        foreach (var entity in context.Entities)
        {
            if (entity.Find(text).Any())
                targets.Add(new NewsEventTarget(entity.Id, "company", "unresolved", "entity:" + entity.Surface));
        }
        return targets.DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    static List<NewsEventTarget> StructuredTargets(NewsFeedItem item)
    {
        var targets = new List<NewsEventTarget>();
        foreach (var ticker in item.Tickers.Where(x => !string.IsNullOrWhiteSpace(x)))
            targets.Add(new NewsEventTarget(ticker.Trim().ToUpperInvariant(), "company", "unresolved", "provider:ticker:" + ticker.Trim()));
        foreach (var entity in item.Entities ?? [])
        {
            var id = string.IsNullOrWhiteSpace(entity.Symbol) ? entity.Name : entity.Symbol.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(id)) targets.Add(new NewsEventTarget(id, "company", "unresolved", "provider:entity:" + entity.Name));
        }
        return targets.DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>사건 동사와 같은 절·80자 이내의 대상만 direct로 둔다. 구조화 대상 → 사전 엔티티·법인 접미사 순.</summary>
    static (string Actor, IReadOnlyList<NewsEventTarget> Targets) CompanyTargetsNearAction(NewsFeedItem item, string text,
        Match action, NewsRelevanceContext context)
    {
        var evidenced = StructuredTargets(item).Where(target => StructuredTargetBeforeAction(item, target, text, action))
            .Select(target => target with { Relation = "direct", Evidence = "text:" + target.Id }).ToArray();
        if (evidenced.Length > 0) return (evidenced[0].Id, evidenced);

        var candidates = context.Entities
            .SelectMany(entity => entity.Find(text).Select(match => (Match: match, Id: entity.Id, Evidence: "entity:" + entity.Surface)))
            .Concat(CompanySuffix.Matches(text).Cast<Match>().Where(match => match.Index <= action.Index)
                .Select(match => (Match: match, Id: match.Value.Trim().ToUpperInvariant(), Evidence: match.Value.Trim())))
            .Where(x => !Overlaps(x.Match.Index, x.Match.Length, action.Index, action.Length))
            .Select(x => (x.Match, x.Id, x.Evidence, Distance: Gap(x.Match.Index, x.Match.Length, action.Index, action.Length)))
            .Where(x => x.Distance <= MaxEventDistance && SameClause(text, x.Match.Index, action.Index))
            .OrderBy(x => x.Distance).ThenBy(x => x.Match.Index)
            .ToArray();
        if (candidates.Length == 0) return ("", []);
        var nearest = candidates[0];
        return (nearest.Match.Value, [new NewsEventTarget(nearest.Id, "company", "direct", nearest.Evidence)]);
    }

    static bool StructuredTargetBeforeAction(NewsFeedItem item, NewsEventTarget target, string text, Match action)
    {
        var aliases = item.Entities?.Where(x => string.Equals(x.Symbol, target.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Name, target.Id, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => new[] { x.Symbol, x.Name }).Append(target.Id) ?? [target.Id];
        return aliases.Where(x => !string.IsNullOrWhiteSpace(x)).Any(alias =>
        {
            var index = text.LastIndexOf(alias, action.Index, StringComparison.OrdinalIgnoreCase);
            return index >= 0 && action.Index - (index + alias.Length) <= MaxEventDistance && SameClause(text, index, action.Index);
        });
    }

    static (Match Target, Match Action, int ClauseStart, int ClauseLength)? MacroEvent(string text)
    {
        foreach (Match clause in Clause.Matches(text))
        {
            if (NonMarket.IsMatch(clause.Value) && !IsFinancial(clause.Value)) continue;
            var targets = AllMatches(clause.Value, MacroTarget, MacroAcronym);
            var actions = MacroAction.Matches(clause.Value).Cast<Match>().ToArray();
            var pair = targets.SelectMany(target => actions.Select(action => (Target: target, Action: action)))
                .Where(x => Math.Abs(x.Action.Index - x.Target.Index) <= MaxEventDistance
                    && !Overlaps(x.Target.Index, x.Target.Length, x.Action.Index, x.Action.Length)
                    && Compatible(x.Target.Value, x.Action.Value))
                .OrderBy(x => Math.Abs(x.Action.Index - x.Target.Index)).FirstOrDefault();
            if (pair.Target is not null && pair.Action is not null)
                return (Offset(pair.Target, clause.Index), Offset(pair.Action, clause.Index), clause.Index, clause.Length);
        }
        return null;
    }

    static bool Compatible(string target, string action)
        => !GeopoliticalAction.IsMatch(action) || GeopoliticalTarget.IsMatch(target);

    static bool IsFinancial(string text) => FinancialQualifier.IsMatch(text) || MacroAcronym.IsMatch(text);

    static Match[] AllMatches(string text, params Regex[] patterns)
        => patterns.SelectMany(pattern => pattern.Matches(text).Cast<Match>()).OrderBy(x => x.Index).ToArray();

    static Match FirstMatch(string text, params Regex[] patterns)
        => AllMatches(text, patterns).FirstOrDefault() ?? Match.Empty;

    static bool Overlaps(int firstIndex, int firstLength, int secondIndex, int secondLength)
        => firstIndex < secondIndex + secondLength && secondIndex < firstIndex + firstLength;

    static int Gap(int firstIndex, int firstLength, int secondIndex, int secondLength)
        => firstIndex <= secondIndex ? secondIndex - (firstIndex + firstLength) : firstIndex - (secondIndex + secondLength);

    static Match Offset(Match match, int offset) => Regex.Match(new string(' ', offset + match.Index) + match.Value, Regex.Escape(match.Value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static bool SameClause(string text, int first, int second)
        => !text[Math.Min(first, second)..Math.Max(first, second)].Any(x => x is '.' or ';' or '!' or '?' or '。' or '！' or '？');

    static IReadOnlyList<NewsEventTarget> MacroTargets(string evidence)
    {
        if (OilAsset.IsMatch(evidence)) return [new("OIL", "asset", "direct", evidence)];
        if (TreasuryAsset.IsMatch(evidence)) return [new("TREASURY", "asset", "direct", evidence)];
        return [new(NewsSymbols.Market, "market", "direct", evidence)];
    }

    static string MacroKind(string target, string text)
    {
        if (MacroReleaseKind.IsMatch(target)) return "macro_release";
        if (MonetaryKind.IsMatch(target)) return "monetary_policy";
        if (GeopoliticalContext.IsMatch(text)) return "geopolitical";
        return "macro_policy";
    }

    static string CompanyKind(string action)
    {
        if (ContractKind.IsMatch(action)) return "company_contract";
        if (EarningsKind.IsMatch(action)) return "guidance_earnings";
        if (CyberKind.IsMatch(action)) return "cybersecurity";
        if (FinancingKind.IsMatch(action)) return "financing";
        if (RegulatoryKind.IsMatch(action)) return "regulatory";
        return "company_action";
    }

    static string Actor(string text, string fallback)
        => FirstMatch(text, Institution, InstitutionAcronym) is { Success: true } institution ? institution.Value
            : CompanySuffix.Match(text) is { Success: true } company ? company.Value : fallback;

    static string ActorInSpan(string text, int start, int length, string fallback, int eventIndex)
    {
        var clause = text.Substring(start, length);
        var candidates = AllMatches(clause, Institution, InstitutionAcronym).Concat(CompanySuffix.Matches(clause).Cast<Match>())
            .Select(match => new { Match = match, Distance = Math.Abs(start + match.Index - eventIndex) })
            .OrderBy(x => x.Distance).ThenBy(x => x.Match.Index).ToArray();
        return candidates.Length > 0 ? candidates[0].Match.Value : fallback;
    }

    static string Span(string text, int first, int second)
    {
        var start = Math.Max(0, Math.Min(first, second) - 80);
        var length = Math.Min(320, text.Length - start);
        return text.Substring(start, length);
    }

    static string Evidence(string text) => text[..Math.Min(320, text.Length)];
    static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
