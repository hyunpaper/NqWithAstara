using System.Text.Json;

namespace Astra.Server.Application;

/// <summary>검증된 SBHNews 전환 뒤 뉴스 전용 저장소만 정리한다(#304).</summary>
public sealed class NewsStorageMigrationService(NewsOptions options, NewsRuntimeState state, INewsStore store,
    NewsTranslationQueue? translations = null)
{
    public async Task<NewsStorageMigrationPlan> PlanAsync(CancellationToken ct)
    {
        var healthy = IsHealthy();
        var files = (await store.ListFilesAsync(ct)).Where(IsDatedArticleFile).Order(StringComparer.Ordinal).ToArray();
        var legacy = 0;
        foreach (var file in files)
            legacy += (await store.ReadLinesAsync(file, ct)).Count(IsLegacyRecord);
        return new NewsStorageMigrationPlan(healthy, healthy ? null : "SBHNews 수집 성공 확인 전에는 정리할 수 없습니다.", files, legacy);
    }

    public async Task<NewsStorageMigrationResult> ExecuteAsync(NewsStorageMigrationPlan plan, CancellationToken ct)
    {
        if (!plan.CanExecute || !IsHealthy()) return new NewsStorageMigrationResult(false,
            plan.Reason ?? "SBHNews 수집 성공 상태가 유지되지 않아 정리할 수 없습니다.", [], 0);
        var existing = new HashSet<string>((await store.ListFilesAsync(ct)).Where(IsDatedArticleFile), StringComparer.Ordinal);
        var rewritten = new List<string>();
        var removed = 0;
        foreach (var file in plan.ArticleFiles.Where(IsDatedArticleFile).Where(existing.Contains).Distinct(StringComparer.Ordinal))
        {
            var lines = await store.ReadLinesAsync(file, ct);
            var kept = lines.Where(line => !IsLegacyRecord(line)).ToArray();
            removed += lines.Count - kept.Length;
            if (kept.Length == lines.Count) continue;
            await store.WriteTextAsync(file, string.Join("\n", kept) + (kept.Length > 0 ? "\n" : ""), ct);
            rewritten.Add(file);
        }
        await PreserveSbhStateAsync(ct);
        if (translations is not null) await translations.ClearCacheAsync(ct);
        else await store.DeleteAsync(NewsTranslationQueue.CacheFile, ct);
        return new NewsStorageMigrationResult(true, null, rewritten, removed);
    }

    async Task PreserveSbhStateAsync(CancellationToken ct)
    {
        var text = await store.ReadTextAsync(NewsFeedService.StateFile, ct);
        if (string.IsNullOrWhiteSpace(text)) return;
        var state = JsonSerializer.Deserialize<NewsFeedState>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (state is null) return;
        var inbox = state.Inbox?.Where(x => string.Equals(x.Item.Provider, NewsFeedProviders.SbhNews, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        var baselines = (state.BaselinedProviders ?? []).Append(NewsFeedProviders.SbhNews).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await store.WriteTextAsync(NewsFeedService.StateFile, JsonSerializer.Serialize(state with
        {
            Inbox = inbox,
            RequestProvider = NewsFeedProviders.SbhNews,
            BaselinedProviders = baselines
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);
    }

    bool IsHealthy() => options.UseSbhNews && state.FeedStatus is "ok" or "empty" or "baseline"
        && state.Providers.Any(x => x.Provider == NewsFeedProviders.SbhNews && x.LastSuccessAt is not null);

    static bool IsDatedArticleFile(string file)
        => file.Length == 16 && file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            && DateOnly.TryParseExact(file[..10], "yyyy-MM-dd", out _);

    static bool IsLegacyRecord(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var source) && !root.TryGetProperty("Source", out source)) return false;
            return !string.Equals(source.GetString(), NewsFeedProviders.SbhNewsSource, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}

public sealed record NewsStorageMigrationPlan(bool CanExecute, string? Reason, IReadOnlyList<string> ArticleFiles, int LegacyRecordCount);
public sealed record NewsStorageMigrationResult(bool Executed, string? Reason, IReadOnlyList<string> RewrittenFiles, int RemovedRecords);
