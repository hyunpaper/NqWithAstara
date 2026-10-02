using System.Text.Json;

namespace Astra.Server.Application;

/// <summary>검증된 SBHNews 전환 뒤 뉴스 전용 저장소만 정리한다(#304).</summary>
public sealed class NewsStorageMigrationService(NewsOptions options, NewsRuntimeState state, INewsStore store,
    NewsFeedService? feedService = null)
{
    /// <summary>제거된 번역 파이프라인이 남긴 캐시 파일(#339). 정리 때 함께 지운다.</summary>
    public const string LegacyTranslationCacheFile = "translation-cache.json";

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
        if (feedService is not null)
            return await feedService.RunExclusiveMaintenanceAsync(token => ExecuteExclusiveAsync(plan, token), ct);
        return await ExecuteExclusiveAsync(plan, ct);
    }

    async Task<NewsStorageMigrationResult> ExecuteExclusiveAsync(NewsStorageMigrationPlan plan, CancellationToken ct)
    {
        if (!plan.CanExecute || !IsHealthy()) return new NewsStorageMigrationResult(false,
            plan.Reason ?? "SBHNews 수집 성공 상태가 유지되지 않아 정리할 수 없습니다.", [], 0);
        return await ExecuteCoreAsync(plan, ct);
    }

    async Task<NewsStorageMigrationResult> ExecuteCoreAsync(NewsStorageMigrationPlan plan, CancellationToken ct)
    {
        var files = plan.ArticleFiles.Where(IsDatedArticleFile).Distinct(StringComparer.Ordinal).ToArray();
        var filtered = await store.FilterFilesAsync(files, line => !IsLegacyRecord(line), ct);
        var rewritten = filtered.Keys.ToArray();
        var removed = filtered.Values.Sum();
        feedService?.DiscardLegacyPendingWork();
        if (feedService is not null) await feedService.RefreshDailyStorageAccountingAsync(ct);
        if (feedService is not null) await feedService.PreserveSbhStateAsync(ct);
        else await PreserveSbhStateAsync(ct);
        await store.DeleteAsync(LegacyTranslationCacheFile, ct);
        state.ClearLegacyRecords();
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
