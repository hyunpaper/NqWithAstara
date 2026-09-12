using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Domain;

namespace Astra.Server.Tests;

sealed class NewsClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

sealed class NewsDiagnostics : IMonitorDiagnostics
{
    public List<(string Scope, Exception Exception)> Failures { get; } = [];
    public void PollFailed(string scope, Exception exception) => Failures.Add((scope, exception));
    public void MarketDataFailed(string symbol, string operation, Exception exception) => Failures.Add((operation, exception));
}

sealed class FakeNewsFeed : INewsFeed
{
    public Dictionary<int, List<NewsFeedItem>> Pages { get; } = [];
    public Dictionary<string, NewsDetail> Details { get; } = new(StringComparer.Ordinal);
    public List<int> ListCalls { get; } = [];
    public List<string> DetailCalls { get; } = [];
    public Exception? ListError { get; set; }

    public Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct)
    {
        ListCalls.Add(page);
        if (ListError is not null) throw ListError;
        return Task.FromResult<IReadOnlyList<NewsFeedItem>>(
            Pages.TryGetValue(page, out var items) ? items.ToArray() : []);
    }

    public Task<NewsDetail?> DetailAsync(string id, CancellationToken ct)
    {
        DetailCalls.Add(id);
        return Task.FromResult(Details.TryGetValue(id, out var detail) ? detail : null);
    }
}

sealed class FakeNewsClassifier : INewsClassifier
{
    public List<NewsClassificationRequest> Requests { get; } = [];
    public Func<NewsClassificationRequest, NewsClassificationResult> Respond { get; set; } =
        _ => new NewsClassificationResult(
            new Astra.Server.Domain.News.NewsClassification(["AAPL"], "positive", 3, "테스트"), "fake", 10, true);

    public Task<NewsClassificationResult> ClassifyAsync(NewsClassificationRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

sealed class MemoryNewsStore : INewsStore
{
    public Dictionary<string, List<string>> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    public Task<long> SizeAsync(string file, CancellationToken ct)
        => Task.FromResult(Files.TryGetValue(file, out var lines)
            ? lines.Sum(x => (long)System.Text.Encoding.UTF8.GetByteCount(x) + 1)
            : 0L);

    public Task<IReadOnlyList<string>> ReadLinesAsync(string file, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Files.TryGetValue(file, out var lines) ? lines.ToArray() : []);

    public Task AppendAsync(string file, string line, CancellationToken ct)
    {
        if (!Files.TryGetValue(file, out var lines)) Files[file] = lines = [];
        lines.Add(line);
        return Task.CompletedTask;
    }

    public Task<string?> ReadTextAsync(string file, CancellationToken ct)
        => Task.FromResult(Texts.TryGetValue(file, out var text) ? text : null);

    public Task WriteTextAsync(string file, string content, CancellationToken ct)
    {
        Texts[file] = content;
        return Task.CompletedTask;
    }
}

sealed class NewsLocalStore : ILocalStore
{
    readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public NewsLocalStore(params WatchItem[] watchlist)
        => _files["watchlist.json"] = JsonSerializer.Serialize(watchlist.ToList());

    public Task<T> Read<T>(string file, T fallback)
        => Task.FromResult(_files.TryGetValue(file, out var json)
            ? JsonSerializer.Deserialize<T>(json) ?? fallback
            : fallback);

    public Task Write<T>(string file, T data)
    {
        _files[file] = JsonSerializer.Serialize(data);
        return Task.CompletedTask;
    }

    public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
    {
        var current = await Read(file, fallback);
        var (data, result) = change(current);
        await Write(file, data);
        return result;
    }
}

static class NewsBuilder
{
    public static NewsFeedItem Item(string id, string title, DateTimeOffset at, params string[] tickers)
        => new(id, title, "목록 요약", "financial-juice", at, tickers);

    public static NewsFeedService Service(
        NewsOptions options, FakeNewsFeed feed, FakeNewsClassifier classifier, MemoryNewsStore store,
        NewsLocalStore local, NewsRuntimeState state, NewsClock clock, NewsDiagnostics diagnostics)
        => new(options, feed, classifier, store, local, state, diagnostics, clock);
}
