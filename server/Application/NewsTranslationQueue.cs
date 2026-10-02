using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Astra.Server.Application;

public sealed record NewsTranslationCacheDocument(
    Dictionary<string, string>? Entries = null,
    DateOnly? QuotaDay = null,
    int CharactersUsed = 0);

/// <summary>수집 경로와 분리된 뉴스 번역 큐와 내용 해시 캐시(#265).</summary>
public sealed class NewsTranslationQueue(
    NewsOptions options,
    INewsTranslator translator,
    INewsStore store,
    NewsRuntimeState state,
    IMonitorDiagnostics diagnostics,
    TimeProvider clock)
{
    public const string CacheFile = "translation-cache.json";
    const int ProviderTextLimit = 5000;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly Channel<TranslationWork> _channel = Channel.CreateUnbounded<TranslationWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _cacheGate = new(1, 1);
    readonly SemaphoreSlim _processingGate = new(1, 1);
    NewsTranslationCacheDocument? _cache;

    public Task ClearCacheAsync(CancellationToken ct)
        => RunExclusiveMaintenanceAsync<int>(_ => Task.FromResult(0), ct);

    public async Task<TResult> RunExclusiveMaintenanceAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation, CancellationToken ct)
    {
        await _processingGate.WaitAsync(ct);
        try
        {
            var result = await operation(ct);
            await ClearCacheCoreAsync(ct);
            return result;
        }
        finally { _processingGate.Release(); }
    }

    async Task ClearCacheCoreAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct);
        try
        {
            _cache = null;
            await store.DeleteAsync(CacheFile, ct);
        }
        finally { _cacheGate.Release(); }
    }

    sealed record TranslationWork(string Id, string Hash);
    sealed record TextResult(string? Value, string Status);

    public int QueueDepth { get { lock (_pending) return _pending.Count; } }

    public void Enqueue(NewsRecord record)
    {
        if (!options.Enabled) return;
        var hash = ContentHash(record);
        if (string.Equals(record.TranslationContentHash, hash, StringComparison.Ordinal)
            && (record.TranslationStatus is "translated" or "not_needed"
                || record.TranslationStatus == "not_configured" && !translator.IsConfigured)) return;
        var key = record.Id + "\n" + hash;
        lock (_pending)
        {
            if (!_pending.Add(key)) return;
        }
        _channel.Writer.TryWrite(new TranslationWork(record.Id, hash));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(ct))
            await ProcessAsync(work, ct);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        if (!_channel.Reader.TryRead(out var work)) return false;
        await ProcessAsync(work, ct);
        return true;
    }

    async Task ProcessAsync(TranslationWork work, CancellationToken ct)
    {
        var key = work.Id + "\n" + work.Hash;
        var acquired = false;
        try
        {
            await _processingGate.WaitAsync(ct);
            acquired = true;
            try
            {
                var record = state.Find(work.Id);
                if (record is null || !string.Equals(ContentHash(record), work.Hash, StringComparison.Ordinal)) return;

                if (!translator.IsConfigured)
                {
                    await SaveAsync(record with
                    {
                        TranslationStatus = "not_configured",
                        TitleTranslationStatus = "not_configured",
                        SummaryTranslationStatus = EmptyStatus(record.Summary, "not_configured"),
                        ContentTranslationStatus = EmptyStatus(record.Content, "not_configured"),
                        ClassificationTranslationStatus = EmptyStatus(record.ClassificationText, "not_configured"),
                        TranslationContentHash = work.Hash
                    }, ct);
                    return;
                }

                var title = await TranslateAsync(record.Title, ct);
                var source = await TranslateAsync(record.Source, ct);
                var summary = await TranslateAsync(record.Summary, ct);
                var content = await TranslateAsync(record.Content, ct);
                var classification = await TranslateAsync(record.ClassificationText, ct);
                var statuses = new[] { title.Status, source.Status, summary.Status, content.Status, classification.Status };
                var overall = statuses.Any(x => x == "quota_wait") ? "quota_wait"
                    : statuses.Any(x => x == "failed") ? (statuses.Any(x => x is "translated" or "not_needed" or "partial") ? "partial" : "failed")
                    : statuses.All(x => x is "not_needed" or "not_available") ? "not_needed"
                    : statuses.Any(x => x == "partial") ? "partial" : "translated";

                await SaveAsync(record with
                {
                    TitleKo = title.Value ?? record.TitleKo,
                    SourceKo = source.Value ?? record.SourceKo,
                    SummaryKo = summary.Value,
                    ContentKo = content.Value,
                    ClassificationTextKo = classification.Value,
                    TranslationStatus = overall,
                    TitleTranslationStatus = title.Status,
                    SummaryTranslationStatus = summary.Status,
                    ContentTranslationStatus = content.Status,
                    ClassificationTranslationStatus = classification.Status,
                    TranslationContentHash = work.Hash
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                diagnostics.PollFailed("news-translate", new InvalidOperationException("뉴스 번역 처리에 실패했습니다."));
            }
        }
        finally
        {
            lock (_pending) _pending.Remove(key);
            if (acquired) _processingGate.Release();
        }
    }

    async Task<TextResult> TranslateAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TextResult(null, "not_available");
        if (ContainsKorean(text)) return new TextResult(text, "not_needed");
        var source = text.Length > ProviderTextLimit ? text[..ProviderTextLimit] : text;
        var hash = Hash(source);
        var cache = await LoadCacheAsync(ct);
        if (cache.Entries!.TryGetValue(hash, out var cached))
            return new TextResult(cached, text.Length > ProviderTextLimit ? "partial" : "translated");

        var retries = Math.Max(1, options.TranslationMaxRetries);
        for (var attempt = 0; attempt < retries; attempt++)
        {
            if (!await ReserveCharactersAsync(source.Length, ct)) return new TextResult(null, "quota_wait");
            var translated = await translator.TranslateTextAsync(source, ct);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                await PutCacheAsync(hash, translated, ct);
                return new TextResult(translated, text.Length > ProviderTextLimit ? "partial" : "translated");
            }
            if (attempt + 1 < retries && options.TranslationRetryDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(options.TranslationRetryDelaySeconds), ct);
        }
        return new TextResult(null, "failed");
    }

    async Task<NewsTranslationCacheDocument> LoadCacheAsync(CancellationToken ct)
    {
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;
            var text = await store.ReadTextAsync(CacheFile, ct);
            try { _cache = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<NewsTranslationCacheDocument>(text, Json); }
            catch (JsonException) { _cache = null; }
            _cache ??= new NewsTranslationCacheDocument(new Dictionary<string, string>(StringComparer.Ordinal));
            if (_cache.Entries is null) _cache = _cache with { Entries = new Dictionary<string, string>(StringComparer.Ordinal) };
            return _cache;
        }
        finally { _cacheGate.Release(); }
    }

    async Task<bool> ReserveCharactersAsync(int count, CancellationToken ct)
    {
        await LoadCacheAsync(ct);
        await _cacheGate.WaitAsync(ct);
        try
        {
            var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (_cache!.QuotaDay != day) _cache = _cache with { QuotaDay = day, CharactersUsed = 0 };
            if (_cache.CharactersUsed + count > Math.Max(0, options.TranslationDailyCharacterBudget)) return false;
            _cache = _cache with { CharactersUsed = _cache.CharactersUsed + count };
            await store.WriteTextAsync(CacheFile, JsonSerializer.Serialize(_cache, Json), ct);
            return true;
        }
        finally { _cacheGate.Release(); }
    }

    async Task PutCacheAsync(string hash, string value, CancellationToken ct)
    {
        await LoadCacheAsync(ct);
        await _cacheGate.WaitAsync(ct);
        try
        {
            _cache!.Entries![hash] = value;
            await store.WriteTextAsync(CacheFile, JsonSerializer.Serialize(_cache, Json), ct);
        }
        finally { _cacheGate.Release(); }
    }

    async Task SaveAsync(NewsRecord record, CancellationToken ct)
    {
        var file = clock.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";
        await store.AppendAsync(file, JsonSerializer.Serialize(record, Json), ct);
        state.Add(record, options.RecentCapacity);
    }

    static string EmptyStatus(string text, string otherwise) => string.IsNullOrWhiteSpace(text) ? "not_available" : otherwise;
    static bool ContainsKorean(string text) => text.Any(c => c is >= '\uac00' and <= '\ud7a3');
    static string ContentHash(NewsRecord record)
        => Hash(string.Join("\n", record.Title, record.Source, record.Summary, record.Content, record.ClassificationText));
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
