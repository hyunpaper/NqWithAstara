using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>피드 공급자 식별자. <see cref="FoxNewsRss"/>는 구 state·레코드 판별용으로만 남는다(#339).</summary>
public static class NewsFeedProviders
{
    public const string FoxNewsRss = "fox-news-rss";
    public const string SbhNews = "sbhnews";
    public const string SbhNewsSource = "SBHNews / 센서스튜디오 (CC BY 4.0)";
}

/// <summary>
/// 피드 목록 한 건(#151 §1). 상세는 매칭 기사에 한해 따로 받는다. <see cref="GroupId"/>가 같은
/// 기사들은 사건 그룹으로 묶여 대표 1건만 분류된다(#171).
/// </summary>
public sealed record NewsFeedItem(
    string Id,
    string Title,
    string Summary,
    string Source,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tickers,
    string Headline = "",
    bool HeadlineOnly = false,
    string? GroupId = null,
    IReadOnlyList<NewsEntity>? Entities = null,
    string Content = "",
    string? Url = null,
    string Provider = "",
    NewsRelevanceAssessment? Relevance = null);


/// <summary>기사 상세(#151). AI 요약이 있으면 본문 대신 그것을 분류 입력으로 쓴다.</summary>
public sealed record NewsDetail(string Summary, string Body);

public sealed record NewsProviderFetchStatus(string Provider, string Status, int Count, int NewCount = 0,
    TimeSpan? RetryAfter = null, int IncludedCount = 0, int ExcludedCount = 0, int ReviewCount = 0,
    string? FilterPolicyVersion = null);

public sealed record NewsFeedBatch(
    IReadOnlyList<NewsFeedItem> Items,
    string Status,
    IReadOnlyList<NewsProviderFetchStatus> Providers);

/// <summary>외부 뉴스 피드 포트(#151 §1). 구현은 Infrastructure에만 둔다.</summary>
public interface INewsFeed
{
    string Name => "feed";
    TimeSpan MinimumInterval => TimeSpan.Zero;
    int DailyRequestLimit => int.MaxValue;

    Task<IReadOnlyList<NewsFeedItem>> ListAsync(int page, CancellationToken ct);

    async Task<NewsFeedBatch> FetchAsync(int page, CancellationToken ct)
    {
        var items = await ListAsync(page, ct);
        return new NewsFeedBatch(items, items.Count == 0 ? "empty" : "ok",
            [new NewsProviderFetchStatus(Name, items.Count == 0 ? "empty" : "ok", items.Count)]);
    }

    /// <summary>기사 상세. 없거나 실패하면 null이다.</summary>
    Task<NewsDetail?> DetailAsync(string id, CancellationToken ct);
}

/// <summary>분류 요청(#151 §3). Body는 AI 요약·상세 본문·목록 요약 중 하나다.</summary>
public sealed record NewsClassificationRequest(string Title, string Body, IReadOnlyList<string> Tickers);

/// <summary>
/// 분류 응답(#151 §3). <see cref="Available"/>가 false면 로컬 LLM에 닿지 못한 것이며,
/// 이 경우 호출자는 기사를 소비하지 않고 다음 주기로 미룬다. <see cref="PromptVersion"/>은 저장
/// 레코드·health에 그대로 노출한다(#171).
/// </summary>
public sealed record NewsClassificationResult(
    NewsClassification? Classification, string Model, long LatencyMs, bool Available, string PromptVersion = "");


/// <summary>로컬 LLM 분류기 포트(#151 §3).</summary>
public interface INewsClassifier
{
    Task<NewsClassificationResult> ClassifyAsync(NewsClassificationRequest request, CancellationToken ct);
}

public interface INewsRelevanceAdjudicator
{
    Task<NewsRelevanceAssessment?> AdjudicateAsync(NewsFeedItem item, CancellationToken ct);
}

/// <summary>`App_Data/news` 전용 파일 포트(#151 §5). 실거래·v5 관측 파일과 분리한다.</summary>
public interface INewsStore
{
    Task<long> SizeAsync(string file, CancellationToken ct);
    Task<IReadOnlyList<string>> ReadLinesAsync(string file, CancellationToken ct);
    Task<int> FilterLinesAsync(string file, Func<string, bool> keep, CancellationToken ct);
    Task<IReadOnlyDictionary<string, int>> FilterFilesAsync(IReadOnlyList<string> files, Func<string, bool> keep, CancellationToken ct);
    Task AppendAsync(string file, string line, CancellationToken ct);
    Task<string?> ReadTextAsync(string file, CancellationToken ct);
    Task WriteTextAsync(string file, string content, CancellationToken ct);
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct);
    Task DeleteAsync(string file, CancellationToken ct);
}
