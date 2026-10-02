using Astra.Server.Application;
using Xunit;

namespace Astra.Server.Tests;

public sealed class NewsStorageMigrationServiceTests
{
    [Fact]
    public async Task SBHNews_성공_전에는_뉴스_파일을_삭제하지_않는다()
    {
        var options = new NewsOptions { UseSbhNews = true };
        var state = new NewsRuntimeState();
        var store = new MemoryNewsStore();
        store.Files["2026-09-26.jsonl"] = ["기사"];

        var plan = await new NewsStorageMigrationService(options, state, store).PlanAsync(CancellationToken.None);
        var result = await new NewsStorageMigrationService(options, state, store).ExecuteAsync(plan, CancellationToken.None);

        Assert.False(plan.CanExecute);
        Assert.False(result.Executed);
        Assert.True(store.Files.ContainsKey("2026-09-26.jsonl"));
    }

    [Fact]
    public async Task SBHNews_성공_뒤에는_뉴스_전용_파일만_정리한다()
    {
        var options = new NewsOptions { UseSbhNews = true };
        var state = new NewsRuntimeState();
        state.CollectionCompleted(DateTimeOffset.UtcNow, "ok", true, 1, null,
            [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "ok", 1)]);
        var store = new MemoryNewsStore();
        var sbh = new NewsRecord("sbh", "신규", NewsFeedProviders.SbhNewsSource, DateTimeOffset.UtcNow, [], [], ["MARKET"], "neutral", 0, "", "", 0, DateTimeOffset.UtcNow);
        var legacy = new NewsRecord("old", "기존", "Fox News", DateTimeOffset.UtcNow, [], [], ["MARKET"], "neutral", 0, "", "", 0, DateTimeOffset.UtcNow);
        store.Files["2026-09-26.jsonl"] = [System.Text.Json.JsonSerializer.Serialize(sbh), System.Text.Json.JsonSerializer.Serialize(legacy)];
        store.Texts[NewsFeedService.StateFile] = System.Text.Json.JsonSerializer.Serialize(new NewsFeedState(0, null, Inbox: [new NewsInboxEntry([], new NewsFeedItem("sbh", "", "", "", DateTimeOffset.UtcNow, [], Provider: NewsFeedProviders.SbhNews), DateTimeOffset.UtcNow, true)]));
        store.Texts[NewsStorageMigrationService.LegacyTranslationCacheFile] = "{}";
        store.Texts["other.json"] = "보존";
        var service = new NewsStorageMigrationService(options, state, store);

        var result = await service.ExecuteAsync(await service.PlanAsync(CancellationToken.None), CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal(["2026-09-26.jsonl"], result.RewrittenFiles);
        Assert.Equal(1, result.RemovedRecords);
        Assert.Single(store.Files["2026-09-26.jsonl"]);
        Assert.Contains("sbh", store.Files["2026-09-26.jsonl"][0]);
        Assert.Contains(NewsFeedProviders.SbhNews, store.Texts[NewsFeedService.StateFile]);
        Assert.False(store.Texts.ContainsKey(NewsStorageMigrationService.LegacyTranslationCacheFile));
        Assert.Equal("보존", store.Texts["other.json"]);
    }

    [Fact]
    public async Task 정리_뒤_런타임에는_SBHNews_기사만_남는다()
    {
        var options = new NewsOptions { UseSbhNews = true };
        var state = new NewsRuntimeState();
        state.CollectionCompleted(DateTimeOffset.UtcNow, "ok", true, 1, null, [new NewsProviderFetchStatus(NewsFeedProviders.SbhNews, "ok", 1)]);
        var sbh = new NewsRecord("sbh", "", NewsFeedProviders.SbhNewsSource, DateTimeOffset.UtcNow, [], [], [], "", 0, "", "", 0, DateTimeOffset.UtcNow);
        var legacy = sbh with { Id = "old", Source = "Fox News" };
        state.Add(sbh, 10);
        state.Add(legacy, 10);
        var service = new NewsStorageMigrationService(options, state, new MemoryNewsStore());

        await service.ExecuteAsync(await service.PlanAsync(CancellationToken.None), CancellationToken.None);

        Assert.Equal("sbh", Assert.Single(state.Recent()).Id);
    }
}
