using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Server.Domain;
namespace Astra.Server;

public static class TossRateLimitGroups { public const int MarketData = 15; public const int Stock = 5; public const int MarketDataChart = 20; public const int OrderInfo = 6; public const int OrderHistory = 5; public const int Asset = 5; }

public sealed class TossClient(HttpClient http)
{
    readonly SemaphoreSlim _requestGate = new(4, 4); readonly SemaphoreSlim _tokenGate = new(1, 1); string? _token; DateTimeOffset _expires;
    readonly SemaphoreSlim _chartRateGate = new(1, 1);
    long _nextChartRequest;
    const int CandlePageSize = 200;
    static readonly TimeSpan MaximumHistoricalRange = TimeSpan.FromHours(90 * 24 + 1);
    async Task<(string Id, string Secret)> Credentials()
    {
        var path = Environment.GetEnvironmentVariable("TOSS_CREDENTIALS_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "tossapi.txt");
        if (!File.Exists(path)) throw new InvalidOperationException("Desktop tossapi.txt not found.");
        var lines = (await File.ReadAllLinesAsync(path)).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        string? Find(params string[] keys) => lines.Select(x => x.Split(new[] { '=', ':' }, 2)).Where(x => x.Length == 2 && keys.Any(k => x[0].Contains(k, StringComparison.OrdinalIgnoreCase))).Select(x => x[1].Trim()).FirstOrDefault();
        var id = Find("client_id", "api key"); var secret = Find("client_secret", "secret key");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("tossapi.txt must contain client id and secret on separate lines or key=value lines."); return (id, secret);
    }
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await EnsureToken(ct);
        return _token ?? throw new InvalidOperationException("Toss access token was not issued.");
    }

    async Task EnsureToken(CancellationToken ct)
    {
        if (_token is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(1)) return; var c = await Credentials();
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_token is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(1)) return;
            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth2/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "client_credentials" }, { "client_id", c.Id }, { "client_secret", c.Secret } }) };
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.Forbidden) throw new TossAuthException("허용 IP 확인: 토스증권 WTS > 설정 > Open API > 허용 IP 관리", "https://developers.tossinvest.com/docs");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _expires = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32());
        }
        finally { _tokenGate.Release(); }
    }
    async Task<JsonDocument> Get(string path, CancellationToken ct) => await Get(path, null, ct);
    async Task<JsonDocument> Get(string path, int? accountSeq, CancellationToken ct)
    {
        await _requestGate.WaitAsync(ct); try { for (var attempt = 0; attempt < 3; attempt++) { await EnsureToken(ct); using var req = new HttpRequestMessage(HttpMethod.Get, path); req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token); if (accountSeq is not null) req.Headers.Add("X-Tossinvest-Account", accountSeq.Value.ToString()); using var res = await http.SendAsync(req, ct); if (res.StatusCode == HttpStatusCode.Unauthorized) { _token = null; continue; } if ((int)res.StatusCode == 429) { var delay = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); await Task.Delay(delay, ct); continue; } res.EnsureSuccessStatusCode(); return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct); } throw new HttpRequestException("Toss API retry limit exceeded."); } finally { _requestGate.Release(); }
    }
    public async Task<(double Price, DateTimeOffset At)> Price(string symbol, CancellationToken ct) { using var d = await Get("api/v1/prices?symbols=" + Uri.EscapeDataString(symbol), ct); var x = d.RootElement.GetProperty("result")[0]; return (double.Parse(x.GetProperty("lastPrice").GetString()!, System.Globalization.CultureInfo.InvariantCulture), x.GetProperty("timestamp").GetDateTimeOffset()); }
    public async Task<OrderBookSnapshot> OrderBook(string symbol, CancellationToken ct) { using var d = await Get("api/v1/orderbook?symbol=" + Uri.EscapeDataString(symbol), ct); var x = d.RootElement.GetProperty("result"); static OrderBookLevel L(JsonElement level) => new(decimal.Parse(level.GetProperty("price").GetString()!, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(level.GetProperty("volume").GetString()!, System.Globalization.CultureInfo.InvariantCulture)); return new(x.GetProperty("timestamp").GetDateTimeOffset(), x.GetProperty("currency").GetString() ?? "", x.GetProperty("asks").EnumerateArray().Select(L).ToArray(), x.GetProperty("bids").EnumerateArray().Select(L).ToArray()); }
    public async Task<IReadOnlyList<WatchItem>> Stocks(string symbols, CancellationToken ct) { using var d = await Get("api/v1/stocks?symbols=" + Uri.EscapeDataString(symbols), ct); return d.RootElement.GetProperty("result").EnumerateArray().Where(x => x.GetProperty("currency").GetString() == "USD" && x.GetProperty("status").GetString() == "ACTIVE").Select(x => new WatchItem(x.GetProperty("symbol").GetString()!, x.TryGetProperty("englishName", out var n) && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()! : x.GetProperty("name").GetString()!)).ToArray(); }
    public async Task<IReadOnlyList<Candle>> Candles(string symbol, CancellationToken ct)
    {
        var bars = new List<Candle>(); string? before = null;
        for (var page = 0; page < 3; page++)
        {
            var path = $"api/v1/candles?symbol={Uri.EscapeDataString(symbol)}&interval=1m&count=200&adjusted=true" + (before is null ? "" : "&before=" + Uri.EscapeDataString(before));
            using var d = await GetChart(path, ct); var result = d.RootElement.GetProperty("result");
            bars.AddRange(result.GetProperty("candles").EnumerateArray().Select(x => new Candle(x.GetProperty("timestamp").GetDateTimeOffset(), D(x, "openPrice"), D(x, "highPrice"), D(x, "lowPrice"), D(x, "closePrice"), D(x, "volume"))));
            before = NextBefore(result); if (before is null) break;
        }
        return bars.DistinctBy(x => x.Timestamp).OrderBy(x => x.Timestamp).ToArray();
    }
    public async Task<Application.Backtest.HistoricalBarReadResult> HistoricalCandles(string symbol, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        var bars = new List<Candle>(); var rawCount = 0; DateTimeOffset? oldest = null;
        var reachedStart = false; string? stopReason = null;
        await foreach (var page in HistoricalCandlePages(symbol, from, to, null, 0, [], ct))
        {
            rawCount += page.RawBarCount;
            oldest = oldest is null || page.OldestBar < oldest ? page.OldestBar : oldest;
            reachedStart |= page.ReachedRequestedStart;
            stopReason = page.StopReason;
            bars.AddRange(page.Bars.Where(x => x.Timestamp >= from && x.Timestamp < to));
        }
        return new(bars.OrderBy(x => x.Timestamp).ToArray(), rawCount, reachedStart, oldest, stopReason);
    }

    public async IAsyncEnumerable<Application.Backtest.HistoricalBarPage> HistoricalCandlePages(string symbol,
        DateTimeOffset from, DateTimeOffset to, string? resumeBefore, int completedPages,
        IReadOnlyCollection<string> visitedCursors,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (to <= from) throw new ArgumentException("과거 캔들 종료 시각은 시작 시각보다 늦어야 합니다.");
        var before = string.IsNullOrWhiteSpace(resumeBefore) ? to.ToString("O") : resumeBefore;
        var seenCursors = visitedCursors.ToHashSet(StringComparer.Ordinal);
        var pageBudget = HistoricalCandlePageBudget(from, to);
        for (var page = completedPages; page < pageBudget; page++)
        {
            var requestCursor = before;
            if (!seenCursors.Add(requestCursor)) yield break;
            var path = $"api/v1/candles?symbol={Uri.EscapeDataString(symbol)}&interval=1m&count={CandlePageSize}&adjusted=true" +
                       "&before=" + Uri.EscapeDataString(before);
            using var d = await GetChart(path, ct); var result = d.RootElement.GetProperty("result");
            var fetched = result.GetProperty("candles").EnumerateArray()
                .Select(x => new Candle(x.GetProperty("timestamp").GetDateTimeOffset(), D(x, "openPrice"),
                    D(x, "highPrice"), D(x, "lowPrice"), D(x, "closePrice"), D(x, "volume"))).ToArray();
            var oldest = fetched.Length == 0 ? (DateTimeOffset?)null : fetched.Min(x => x.Timestamp);
            var reachedStart = oldest <= from;
            var nextBefore = NextBefore(result);
            string? stopReason = null;
            if (fetched.Length == 0) stopReason = "Toss가 빈 페이지를 반환했습니다.";
            else if (reachedStart) nextBefore = null;
            else if (nextBefore is null) stopReason = "Toss 가용 과거 데이터의 끝에 도달했습니다.";
            else if (string.Equals(nextBefore, requestCursor, StringComparison.Ordinal))
                stopReason = "Toss가 동일한 다음 페이지 커서를 반복했습니다.";
            else if (seenCursors.Contains(nextBefore))
                stopReason = "Toss가 이전 페이지 커서를 순환해서 반환했습니다.";
            else if (page == pageBudget - 1)
                stopReason = $"요청 기간 안전 페이지 상한({pageBudget})에 도달했습니다.";
            yield return new(requestCursor, nextBefore, fetched, fetched.Length, reachedStart, oldest, stopReason);
            if (reachedStart || nextBefore is null || stopReason is not null) yield break;
            before = nextBefore;
        }
    }
    public static int HistoricalCandlePageBudget(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) throw new ArgumentException("과거 캔들 종료 시각은 시작 시각보다 늦어야 합니다.");
        if (to - from > MaximumHistoricalRange)
            throw new ArgumentOutOfRangeException(nameof(to), "과거 캔들 조회 기간은 90일을 초과할 수 없습니다.");
        return checked((int)Math.Ceiling((to - from).TotalMinutes / CandlePageSize) + 1);
    }
    async Task<JsonDocument> GetChart(string path, CancellationToken ct)
    {
        await _chartRateGate.WaitAsync(ct);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var remaining = _nextChartRequest - now;
            if (remaining > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), ct);
            _nextChartRequest = Stopwatch.GetTimestamp() +
                (long)Math.Ceiling((double)Stopwatch.Frequency / TossRateLimitGroups.MarketDataChart);
            return await Get(path, ct);
        }
        finally { _chartRateGate.Release(); }
    }
    public async Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) { using var d = await GetChart($"api/v1/candles?symbol={Uri.EscapeDataString(symbol)}&interval=1d&count=30&adjusted=true", ct); return d.RootElement.GetProperty("result").GetProperty("candles").EnumerateArray().Select(x => new Candle(x.GetProperty("timestamp").GetDateTimeOffset(), D(x, "openPrice"), D(x, "highPrice"), D(x, "lowPrice"), D(x, "closePrice"), D(x, "volume"))).DistinctBy(x => x.Timestamp).OrderBy(x => x.Timestamp).ToArray(); }
    static string? NextBefore(JsonElement result)
    {
        if (!result.TryGetProperty("nextBefore", out var next) || next.ValueKind != JsonValueKind.String)
            return null;
        var value = next.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    static double D(JsonElement x, string p) => double.Parse(x.GetProperty(p).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    static decimal Dec(JsonElement x, string p) => decimal.Parse(x.GetProperty(p).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    static decimal? DecN(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? decimal.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
    static DateOnly? DateN(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? DateOnly.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
    public async Task<IReadOnlyList<TossTrade>> Trades(string symbol, int count, CancellationToken ct)
    {
        using var d = await Get($"api/v1/trades?symbol={Uri.EscapeDataString(symbol)}&count={count}", ct);
        return d.RootElement.GetProperty("result").EnumerateArray().Select(x => new TossTrade(symbol, Dec(x, "price"), Dec(x, "volume"), x.GetProperty("timestamp").GetDateTimeOffset(), x.GetProperty("currency").GetString() ?? "")).ToArray();
    }
    public async Task<IReadOnlyList<StockInfo>> StockInfos(string symbols, CancellationToken ct)
    {
        using var d = await Get("api/v1/stocks?symbols=" + Uri.EscapeDataString(symbols), ct);
        return d.RootElement.GetProperty("result").EnumerateArray().Select(x => new StockInfo(
            x.GetProperty("symbol").GetString()!,
            x.TryGetProperty("englishName", out var n) && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()! : x.GetProperty("name").GetString()!,
            x.GetProperty("securityType").GetString() ?? "",
            x.GetProperty("market").GetString() ?? "",
            x.TryGetProperty("isCommonShare", out var c) && c.ValueKind == JsonValueKind.True,
            x.GetProperty("status").GetString() ?? "",
            DecN(x, "leverageFactor"),
            DecN(x, "sharesOutstanding"))).ToArray();
    }
    public async Task<IReadOnlyList<TossAccount>> Accounts(CancellationToken ct)
    {
        using var d = await Get("api/v1/accounts", ct);
        return d.RootElement.GetProperty("result").EnumerateArray().Select(x => new TossAccount(x.GetProperty("accountSeq").GetInt32(), x.GetProperty("accountType").GetString() ?? "")).ToArray();
    }
    public async Task<IReadOnlyList<TossCommission>> Commissions(int accountSeq, CancellationToken ct)
    {
        using var d = await Get("api/v1/commissions", accountSeq, ct);
        return d.RootElement.GetProperty("result").EnumerateArray().Select(x => new TossCommission(x.GetProperty("marketCountry").GetString() ?? "", Dec(x, "commissionRate"), DateN(x, "startDate"), DateN(x, "endDate"))).ToArray();
    }
    public async Task<TossOrderPage> ClosedOrders(int accountSeq, DateOnly? from, DateOnly? to, string? cursor, int limit, CancellationToken ct)
    {
        var path = $"api/v1/orders?status=CLOSED&limit={limit}" + (from is not null ? $"&from={from:yyyy-MM-dd}" : "") + (to is not null ? $"&to={to:yyyy-MM-dd}" : "") + (cursor is not null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
        using var d = await Get(path, accountSeq, ct);
        var r = d.RootElement.GetProperty("result");
        var orders = r.GetProperty("orders").EnumerateArray().Select(x => new TossOrder(
            x.GetProperty("orderId").GetString() ?? "",
            x.GetProperty("symbol").GetString() ?? "",
            x.GetProperty("side").GetString() ?? "",
            x.GetProperty("orderType").GetString() ?? "",
            x.GetProperty("status").GetString() ?? "",
            x.GetProperty("orderedAt").GetDateTimeOffset(),
            x.TryGetProperty("execution", out var e) && e.ValueKind == JsonValueKind.Object
                ? new TossOrderExecution(Dec(e, "filledQuantity"), Dec(e, "averageFilledPrice"), Dec(e, "filledAmount"), Dec(e, "commission"), e.GetProperty("filledAt").GetDateTimeOffset())
                : null)).ToArray();
        return new TossOrderPage(orders, r.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null, r.TryGetProperty("hasNext", out var hn) && hn.ValueKind == JsonValueKind.True);
    }
    public async Task<IReadOnlyList<TossHolding>> Holdings(int accountSeq, CancellationToken ct)
    {
        using var d = await Get("api/v1/holdings", accountSeq, ct);
        var r = d.RootElement.GetProperty("result");
        var items = r.TryGetProperty("items", out var it) ? it : r;
        return items.EnumerateArray().Select(x => new TossHolding(x.GetProperty("symbol").GetString() ?? "", Dec(x, "quantity"), Dec(x, "averagePurchasePrice"), Dec(x, "lastPrice"))).ToArray();
    }
    public async Task<MarketSession> Session(DateTimeOffset now, CancellationToken ct) { var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); var usDate = TimeZoneInfo.ConvertTime(now, eastern); using var d = await Get($"api/v1/market-calendar/US?date={usDate:yyyy-MM-dd}", ct); var r = d.RootElement.GetProperty("result"); var today = r.GetProperty("today"); if (today.GetProperty("regularMarket").ValueKind == JsonValueKind.Null) { DateTimeOffset? next = null; if (r.TryGetProperty("nextBusinessDay", out var n) && n.GetProperty("regularMarket").ValueKind != JsonValueKind.Null) next = n.GetProperty("regularMarket").GetProperty("startTime").GetDateTimeOffset(); return new(false, "휴장", next, null, null); } var m = today.GetProperty("regularMarket"); var s = m.GetProperty("startTime").GetDateTimeOffset(); var e = m.GetProperty("endTime").GetDateTimeOffset(); DateTimeOffset? nextOpen = now < s ? s : null; return new(now >= s && now < e, now >= s && now < e ? "정규장" : "정규장 외", nextOpen, s, e); }
}
