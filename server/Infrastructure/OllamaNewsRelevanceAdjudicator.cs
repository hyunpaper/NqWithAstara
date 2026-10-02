using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

public sealed class OllamaNewsRelevanceAdjudicator : INewsRelevanceAdjudicator
{
    const string PromptVersion = "sbh-relevance-adjudication-v1";
    static readonly HashSet<string> EventKinds = ["macro_policy", "macro_release", "monetary_policy", "geopolitical",
        "company_contract", "guidance_earnings", "cybersecurity", "financing", "regulatory", "company_action", "unknown"];
    static readonly HashSet<string> TargetKinds = ["company", "market", "asset"];
    static readonly HashSet<string> Fields = ["decision", "eventKind", "actor", "action", "targetId", "targetKind", "evidence", "reason"];
    readonly NewsOptions _options;
    readonly HttpClient _http;
    readonly SemaphoreSlim _concurrency;

    public OllamaNewsRelevanceAdjudicator(NewsOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
        _concurrency = new SemaphoreSlim(Math.Clamp(options.SbhRelevanceAdjudicationConcurrency, 1, 4));
    }

    public async Task<NewsRelevanceAssessment?> AdjudicateAsync(NewsFeedItem item, CancellationToken ct)
    {
        if (!_options.SbhRelevanceAdjudicationEnabled
            || !string.Equals(item.Provider, NewsFeedProviders.SbhNews, StringComparison.OrdinalIgnoreCase)
            || item.Relevance?.Decision != NewsRelevanceDecisions.Review) return null;
        await _concurrency.WaitAsync(ct);
        try
        {
            var title = item.Title.Trim();
            title = title[..Math.Min(180, title.Length)];
            var span = item.Relevance?.EvidenceSpan?.Trim() ?? "";
            span = span[..Math.Min(320, span.Length)];
            var evidence = string.Join("\n", new[] { title, span }.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal));
            if (evidence.Length == 0) return null;
            var prompt = "Return JSON only. Decide whether this SBH article contains an explicit financial-market event. "
                + "Schema: {\"decision\":\"include|exclude\",\"eventKind\":\"macro_policy|macro_release|monetary_policy|geopolitical|company_contract|guidance_earnings|cybersecurity|financing|regulatory|company_action|unknown\","
                + "\"actor\":\"exact substring\",\"action\":\"exact substring\",\"targetId\":\"exact substring or MARKET|OIL|TREASURY\",\"targetKind\":\"company|market|asset\",\"evidence\":\"exact contiguous substring\",\"reason\":\"short code\"}. "
                + "Include only with explicit actor-action-target evidence. Never infer ticker, sector, company impact, or causal relation. ARTICLE: " + evidence;
            var sw = Stopwatch.StartNew();
            using var response = await _http.PostAsJsonAsync(new Uri(new Uri(_options.OllamaUrl.TrimEnd('/') + "/"), "api/generate"),
                new { model = _options.Model, prompt, stream = false, format = "json", keep_alive = _options.KeepAlive }, ct);
            if (!response.IsSuccessStatusCode) return null;
            var envelope = await response.Content.ReadFromJsonAsync<OllamaEnvelope>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(envelope?.Response)) return null;
            using var document = JsonDocument.Parse(envelope.Response);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Any(x => !Fields.Contains(x.Name))) return null;
            var value = document.RootElement.Deserialize<AdjudicationDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (value is null || value.Decision is not (NewsRelevanceDecisions.Include or NewsRelevanceDecisions.Exclude)
                || !EventKinds.Contains(value.EventKind ?? "")
                || string.IsNullOrWhiteSpace(value.Evidence) || !evidence.Contains(value.Evidence, StringComparison.Ordinal)) return null;
            if (value.Decision == NewsRelevanceDecisions.Include
                && (string.IsNullOrWhiteSpace(value.Actor) || string.IsNullOrWhiteSpace(value.Action)
                    || string.IsNullOrWhiteSpace(value.TargetId) || !TargetKinds.Contains(value.TargetKind ?? "")
                    || !evidence.Contains(value.Actor, StringComparison.Ordinal)
                    || !evidence.Contains(value.Action, StringComparison.Ordinal) || !ExactTarget(value.TargetId, evidence))) return null;
            if (value.Decision == NewsRelevanceDecisions.Exclude
                && (!string.Equals(value.EventKind, "unknown", StringComparison.Ordinal)
                    || !string.IsNullOrEmpty(value.TargetKind) || !string.IsNullOrEmpty(value.TargetId))) return null;
            var targets = value.Decision == NewsRelevanceDecisions.Include
                ? new[] { new NewsEventTarget(value.TargetId!, value.TargetKind!, "direct", value.Evidence) }
                : [];
            return new NewsRelevanceAssessment(item.Relevance?.PolicyVersion ?? NewsRelevancePolicy.CurrentVersion, value.Decision, value.EventKind ?? "unknown",
                value.Actor ?? "", value.Action ?? "", targets, value.Evidence, value.Reason ?? "ollama_adjudicated",
                PromptVersion, sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        finally { _concurrency.Release(); }
    }

    static bool ExactTarget(string value, string evidence)
        => value is NewsSymbols.Market or "OIL" or "TREASURY" || evidence.Contains(value, StringComparison.Ordinal);

    sealed record OllamaEnvelope(string Response);
    sealed record AdjudicationDecision(string Decision, string? EventKind, string? Actor, string? Action, string? TargetId,
        string? TargetKind, string Evidence, string? Reason);
}
