using Astra.Server.Domain;

namespace Astra.Server.Application;

public sealed record MarketMoodAsset(
    string Key,
    string Label,
    string Symbol,
    string AssetKind,
    bool Proxy,
    bool IsAvailable,
    string Direction,
    double? ChangePercent,
    DateTimeOffset? AsOf,
    string Source,
    string DelayStatus,
    string SessionStatus,
    string? Reason);

public sealed record MarketMoodResponse(
    DateTimeOffset AsOf,
    string Status,
    double? Score,
    string Direction,
    int AvailableCount,
    int TotalCount,
    string Aggregation,
    string Source,
    int RefreshSeconds,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<MarketMoodAsset> Assets);

public sealed class MarketMoodQueryService(IMarketDataGateway marketData, TimeProvider clock)
{
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    static readonly (string Key, string Label, string Symbol, string AssetKind)[] Definitions =
    [
        ("gold", "금", "GLD", "commodity"),
        ("oil", "유가", "USO", "commodity"),
        ("nasdaq", "나스닥", "QQQ", "index"),
        ("bitcoin", "비트코인", "IBIT", "crypto"),
        ("us-treasury", "미국채", "TLT", "bond")
    ];

    static readonly string[] Limitations =
    [
        "Toss Open API 무료 미국 주식·ETF 시세를 60초 캐시로 조회하며 실시간 틱을 보장하지 않습니다.",
        "갱신 1회당 시장 캘린더 1회·현재가 5회·일봉 5회를 사용하며 무료 호출 한도는 Toss 제공자 정책을 따릅니다.",
        "금·유가·나스닥·비트코인·미국채는 선물·현물·지수가 아니라 GLD·USO·QQQ·IBIT·TLT ETF 프록시입니다.",
        "누락·휴장·5분 이상 지연된 자산은 동일 가중치 평균에서 제외합니다."
    ];

    readonly SemaphoreSlim _refreshGate = new(1, 1);
    MarketMoodResponse? _cached;
    DateTimeOffset _cachedAt;

    public async Task<MarketMoodResponse> GetAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_cached is not null && now - _cachedAt < RefreshInterval) return _cached;
            _cached = await RefreshAsync(now, ct);
            _cachedAt = now;
            return _cached;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    async Task<MarketMoodResponse> RefreshAsync(DateTimeOffset now, CancellationToken ct)
    {
        MarketSession? session;
        try
        {
            session = await marketData.Session(now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            session = null;
        }

        IReadOnlyList<MarketMoodAsset> assets;
        if (session is null)
        {
            assets = Definitions.Select(definition => Unavailable(definition, "unknown", "시장 세션을 확인하지 못했습니다.")).ToArray();
        }
        else if (!session.IsOpen)
        {
            assets = Definitions.Select(definition => Unavailable(definition, "closed", $"{session.Label}이므로 집계에서 제외했습니다.")).ToArray();
        }
        else
        {
            assets = await Task.WhenAll(Definitions.Select(definition => RefreshAssetAsync(definition, session, now, ct)));
        }

        var included = assets.Where(asset => asset.IsAvailable && asset.ChangePercent is not null).ToArray();
        double? score = included.Length == 0
            ? null
            : Math.Round(included.Average(asset => asset.ChangePercent!.Value), 2, MidpointRounding.AwayFromZero);
        var status = included.Length == 0 ? "unavailable" : included.Length == assets.Count ? "available" : "partial";

        return new(
            now,
            status,
            score,
            Direction(score),
            included.Length,
            assets.Count,
            "equal_weight_available_only",
            "Toss Open API · 미국 주식·ETF",
            (int)RefreshInterval.TotalSeconds,
            Limitations,
            included.Select(asset => asset.Key).ToArray(),
            assets);
    }

    async Task<MarketMoodAsset> RefreshAssetAsync(
        (string Key, string Label, string Symbol, string AssetKind) definition,
        MarketSession session,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            var quoteTask = marketData.Price(definition.Symbol, ct);
            var dailyTask = marketData.DailyCandles(definition.Symbol, ct);
            await Task.WhenAll(quoteTask, dailyTask);
            var quote = await quoteTask;
            var daily = await dailyTask;

            if (!double.IsFinite(quote.Price) || quote.Price <= 0)
                return Unavailable(definition, "open", "유효한 현재가가 없습니다.", quote.At);

            var previousClose = daily
                .Where(candle => double.IsFinite(candle.Close) && candle.Close > 0 &&
                                 (session.Start is null || candle.Timestamp < session.Start.Value))
                .OrderBy(candle => candle.Timestamp)
                .LastOrDefault();
            if (previousClose is null)
                return Unavailable(definition, "open", "전일 종가가 없어 등락률을 계산할 수 없습니다.", quote.At);

            var change = Math.Round((quote.Price / previousClose.Close - 1d) * 100d, 2, MidpointRounding.AwayFromZero);
            var stale = quote.At > now || now - quote.At >= StaleAfter;
            return new(
                definition.Key,
                definition.Label,
                definition.Symbol,
                definition.AssetKind,
                true,
                !stale,
                Direction(change),
                change,
                quote.At,
                "Toss Open API · 미국 ETF",
                stale ? "stale" : "fresh",
                "open",
                stale ? "시세가 5분 이상 지연되어 집계에서 제외했습니다." : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable(definition, "open", "시세를 조회하지 못했습니다.");
        }
    }

    static MarketMoodAsset Unavailable(
        (string Key, string Label, string Symbol, string AssetKind) definition,
        string sessionStatus,
        string reason,
        DateTimeOffset? asOf = null) =>
        new(definition.Key, definition.Label, definition.Symbol, definition.AssetKind, true, false, "unknown", null,
            asOf, "Toss Open API · 미국 ETF", "unavailable", sessionStatus, reason);

    static string Direction(double? value) => value switch
    {
        > 0 => "up",
        < 0 => "down",
        0 => "flat",
        _ => "unavailable"
    };
}
