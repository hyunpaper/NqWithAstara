using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain.News;

namespace Astra.Server.Infrastructure;

public sealed class OllamaNewsRelevanceAdjudicator : INewsRelevanceAdjudicator
{
    const string PromptVersion = "sbh-relevance-adjudication-v1";
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
            var evidence = string.Join(" ", new[] { item.Title, item.Summary, item.Content }
                .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (evidence.Length == 0) return null;
            evidence = evidence[..Math.Min(1200, evidence.Length)];
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
            var value = JsonSerializer.Deserialize<AdjudicationDecision>(envelope.Response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (value is null || value.Decision is not (NewsRelevanceDecisions.Include or NewsRelevanceDecisions.Exclude)
                || string.IsNullOrWhiteSpace(value.Evidence) || !evidence.Contains(value.Evidence, StringComparison.Ordinal)) return null;
            if (value.Decision == NewsRelevanceDecisions.Include
                && (string.IsNullOrWhiteSpace(value.Actor) || string.IsNullOrWhiteSpace(value.Action)
                    || string.IsNullOrWhiteSpace(value.TargetId) || !ExactOrTyped(value.Actor, evidence)
                    || !ExactOrTyped(value.Action, evidence) || !ExactOrTyped(value.TargetId, evidence))) return null;
            var targets = value.Decision == NewsRelevanceDecisions.Include
                ? new[] { new NewsEventTarget(value.TargetId!, value.TargetKind is "company" or "asset" or "market" ? value.TargetKind : "unknown", "direct", value.Evidence) }
                : [];
            return new NewsRelevanceAssessment(NewsRelevancePolicy.CurrentVersion, value.Decision, value.EventKind ?? "unknown",
                value.Actor ?? "", value.Action ?? "", targets, value.Evidence, value.Reason ?? "ollama_adjudicated",
                PromptVersion, sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        finally { _concurrency.Release(); }
    }

    static bool ExactOrTyped(string value, string evidence)
        => value is NewsSymbols.Market or "OIL" or "TREASURY" || evidence.Contains(value, StringComparison.Ordinal);

    sealed record OllamaEnvelope(string Response);
    sealed record AdjudicationDecision(string Decision, string? EventKind, string? Actor, string? Action, string? TargetId,
        string? TargetKind, string Evidence, string? Reason);
}
