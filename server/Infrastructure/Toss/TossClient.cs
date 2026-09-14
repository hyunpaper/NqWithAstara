using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Astra.Server.Domain;
namespace Astra.Server;

public static class TossRateLimitGroups { public const int MarketData = 15; public const int Stock = 5; public const int MarketDataChart = 20; public const int OrderInfo = 6; public const int OrderHistory = 5; public const int Asset = 5; }

public sealed class TossClient(HttpClient http)
{
    readonly SemaphoreSlim _requestGate = new(4, 4); readonly SemaphoreSlim _tokenGate = new(1, 1); string? _token; DateTimeOffset _expires;
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
            using var d = await Get(path, ct); var result = d.RootElement.GetProperty("result");
            bars.AddRange(result.GetProperty("candles").EnumerateArray().Select(x => new Candle(x.GetProperty("timestamp").GetDateTimeOffset(), D(x, "openPrice"), D(x, "highPrice"), D(x, "lowPrice"), D(x, "closePrice"), D(x, "volume"))));
            before = result.TryGetProperty("nextBefore", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null; if (before is null) break;
        }
        return bars.DistinctBy(x => x.Timestamp).OrderBy(x => x.Timestamp).ToArray();
    }
    public async Task<Application.Backtest.HistoricalBarReadResult> HistoricalCandles(string symbol, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        var bars = new List<Candle>(); string? before = null; var rawCount = 0; DateTimeOffset? oldest = null;
        var reachedStart = false; string? stopReason = null;
        for (var page = 0; page < 80; page++)
        {
            var path = $"api/v1/candles?symbol={Uri.EscapeDataString(symbol)}&interval=1m&count=200&adjusted=true" +
                       (before is null ? "" : "&before=" + Uri.EscapeDataString(before));
            using var d = await Get(path, ct); var result = d.RootElement.GetProperty("result");
            var fetched = result.GetProperty("candles").EnumerateArray()
                .Select(x => new Candle(x.GetProperty("timestamp").GetDateTimeOffset(), D(x, "openPrice"),
                    D(x, "highPrice"), D(x, "lowPrice"), D(x, "closePrice"), D(x, "volume"))).ToArray();
            rawCount += fetched.Length;
            if (fetched.Length > 0) oldest = oldest is null ? fetched.Min(x => x.Timestamp) :
                DateTimeOffset.Compare(oldest.Value, fetched.Min(x => x.Timestamp)) <= 0 ? oldest : fetched.Min(x => x.Timestamp);
            bars.AddRange(fetched.Where(x => x.Timestamp >= from && x.Timestamp < to));
            if (fetched.Length == 0) { stopReason = "Toss가 빈 페이지를 반환했습니다."; break; }
            if (fetched.Min(x => x.Timestamp) <= from) { reachedStart = true; break; }
            before = result.TryGetProperty("nextBefore", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString() : null;
            if (before is null) { stopReason = "Toss가 다음 페이지 커서를 반환하지 않았습니다."; break; }
            if (page == 79) stopReason = "Toss 페이지 조회 상한에 도달했습니다.";
        }
        return new(bars.OrderBy(x => x.Timestamp).ToArray(), rawCount, reachedStart, oldest, stopReason);
    }
    public async Task<IReadOnlyList<Candle>> DailyCandles(string symbol, CancellationToken ct) { using var d = await Get($"api/v1/candles?symbol={Uri.EscapeDataString(symbol)}&interval=1d&count=30&adjusted=true", ct); return d.RootElement.GetProperty("result").GetProperty("candles").EnumerateArray().Select(x => new Candle(x.GetProperty("timestamp").GetDateTimeOffset(), D(x, "openPrice"), D(x, "highPrice"), D(x, "lowPrice"), D(x, "closePrice"), D(x, "volume"))).DistinctBy(x => x.Timestamp).OrderBy(x => x.Timestamp).ToArray(); }
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
