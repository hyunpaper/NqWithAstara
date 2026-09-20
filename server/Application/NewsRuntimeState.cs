using Astra.Server.Domain.News;

namespace Astra.Server.Application;

/// <summary>`News` 설정 섹션(#151 §7). Enabled 기본 false이며 false면 어떤 외부 호출도 하지 않는다.</summary>
public sealed class NewsOptions
{
    public bool Enabled { get; set; }
    public string FeedUrl { get; set; } = "https://www.saveticker.com";
    public string MarketauxApiKey { get; set; } = "";
    public string MarketauxUrl { get; set; } = "https://api.marketaux.com/v1/news/all";
    public string GoogleNewsUrl { get; set; } = "https://news.google.com/rss/search?q=stock%20market%20OR%20semiconductor%20OR%20earnings&hl=en-US&gl=US&ceid=US:en";
    public string YahooNewsUrl { get; set; } = "https://finance.yahoo.com/rss/2.0/headline?s=SOXL,SOXX,NVDA,AMD,INTC&region=US&lang=en-US";
    public int PollSeconds { get; set; } = 60;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public int MaxClassificationsPerMinute { get; set; } = 12;
    public double HalfLifeMinutes { get; set; } = NewsSentimentDecay.DefaultHalfLifeMinutes;
    public string KeepAlive { get; set; } = "30m";
    public string PapagoClientId { get; set; } = "";
    public string PapagoClientSecret { get; set; } = "";

    /// <summary>목록 확장 상한. 신규가 한 페이지를 넘칠 때만 다음 페이지를 본다.</summary>
    public int MaxPages { get; set; } = 1;

    /// <summary>목록·상세를 합한 피드 요청 예산(#151 §1 "분당 요청 ≤3").</summary>
    public int MaxFeedRequestsPerMinute { get; set; } = 1;

    /// <summary>외부 피드의 24시간 요청 상한. 공급자 쿼터를 넘지 않도록 보수적으로 제한한다.</summary>
    public int MaxDailyFeedRequests { get; set; } = 1440;

    public int MaxQueue { get; set; } = 100;

    /// <summary>재기동 시 번역·영향도 누락 기사에 대한 분류 재시도 상한.</summary>
    public int ReclassifyUnclassifiedPerPoll { get; set; } = 3;

    /// <summary>일자 파일 상한(바이트). 넘으면 더 쓰지 않되 기존 기록은 지우지 않는다.</summary>
    public long MaxDailyBytes { get; set; } = 5 * 1024 * 1024;

    public int RecentCapacity { get; set; } = 300;
}

/// <summary>
/// 저장·조회 공용 기사 레코드(#151 §5). <see cref="ClassifiedFrom"/>은 사건 그룹 대표의 판정을
/// 복사해 저장한 팔로워 기사에만 대표 기사 id로 채워진다(#171). 그룹이 없거나 자신이 대표면 null이다.
/// </summary>
public sealed record NewsRecord(
    string Id,
    string Title,
    string Source,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<string> MatchedSymbols,
    IReadOnlyList<string> Symbols,
    string Sentiment,
    int Strength,
    string Reason,
    string Model,
    long LatencyMs,
    DateTimeOffset ClassifiedAt,
    string InputKind = NewsInputKinds.Body,
    string PromptVersion = "",
    string? ClassifiedFrom = null,
    IReadOnlyList<NewsEntity>? Entities = null,
    string? TitleKo = null,
    string? SourceKo = null,
    IReadOnlyDictionary<string, int>? ImpactScores = null);

/// <summary>health·조회가 함께 보는 뉴스 런타임 상태(#151 §6). 스레드 안전하다.</summary>
public sealed class NewsRuntimeState
{
    readonly object _gate = new();
    readonly LinkedList<NewsRecord> _recent = new();
    readonly Dictionary<string, LinkedListNode<NewsRecord>> _index = new(StringComparer.Ordinal);

    public DateTimeOffset? LastPollAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public string? LastError { get; private set; }
    public int Queue { get; private set; }
    public long Dropped { get; private set; }
    public long Seen { get; private set; }
    public long Classified { get; private set; }
    public bool OllamaOk { get; private set; } = true;
    public bool StorageLimited { get; private set; }

    public void PollStarted(DateTimeOffset at) { lock (_gate) LastAttemptAt = at; }
    public void PollCompleted(DateTimeOffset at) { lock (_gate) { LastPollAt = at; LastSuccessAt = at; LastError = null; } }
    public void PollFailed(DateTimeOffset at, string error) { lock (_gate) { LastPollAt = at; LastError = error; } }
    public void QueueDepth(int depth) { lock (_gate) Queue = depth; }
    public void Drop(int count) { lock (_gate) Dropped += count; }
    public void SeenArticles(int count) { lock (_gate) Seen += count; }
    public void Ollama(bool ok) { lock (_gate) OllamaOk = ok; }
    public void Limited(bool limited) { lock (_gate) StorageLimited = limited; }

    public void Add(NewsRecord record, int capacity)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(record.Id, out var existing)) { _recent.Remove(existing); _index.Remove(record.Id); }
            else Classified++;
            var node = _recent.AddFirst(record);
            _index[record.Id] = node;
            while (_recent.Count > Math.Max(1, capacity))
            {
                var last = _recent.Last!;
                _recent.RemoveLast();
                _index.Remove(last.Value.Id);
            }
        }
    }

    public IReadOnlyList<NewsRecord> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }
}
