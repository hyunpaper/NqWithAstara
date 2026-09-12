using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Server;

/// <summary>Receives public US trade ticks from Toss. REST candles remain the source for indicators.</summary>
public sealed class TossStreamService(ILogger<TossStreamService> log, Application.TickFlowTape? tape = null) : Application.IRealtimeMarketStream, IAsyncDisposable
{
    readonly Application.TickFlowTape _tape = tape ?? new Application.TickFlowTape(TimeProvider.System);

    const int MaxSymbols = 30;
    const int MaxMessageBytes = 256 * 1024;
    static readonly Uri Endpoint = new("wss://openapi-ws.tossinvest.com/ws/v1");
    static readonly Regex SymbolPattern = new("^[A-Z0-9.-]{1,20}$", RegexOptions.CultureInvariant);
    readonly SemaphoreSlim _lifecycle = new(1, 1);
    CancellationTokenSource? _runCts;
    Task? _runTask;

    public ConcurrentDictionary<string, TossTrade> Latest { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Status { get; private set; } = "idle";
    public string Message { get; private set; } = "실시간 연결 대기 중";
    public DateTimeOffset? LastTickAt { get; private set; }

    public bool TryGetLatest(string symbol, out TossTrade trade) => Latest.TryGetValue(symbol, out trade!);

    /// <summary>틱 룰 분류는 <see cref="Application.TickFlowTape"/>가 소유한다 — ws 틱과 REST 체결이 같은 규칙을 쓴다.</summary>
    void RecordFlow(TossTrade trade) => _tape.RecordTick(trade);

    public (decimal Buy, decimal Sell)? Flow(string symbol, TimeSpan window)
        => Flow(symbol, DateTimeOffset.UtcNow - window, DateTimeOffset.UtcNow.AddSeconds(5));

    public (decimal Buy, decimal Sell)? Flow(string symbol, DateTimeOffset from, DateTimeOffset to)
        => _tape.Flow(symbol, from, to);

    public async Task StartAsync(
        IEnumerable<string> symbols,
        Func<CancellationToken, Task<string>> getAccessToken,
        bool allowedIpConfirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(getAccessToken);
        if (!allowedIpConfirmed)
            throw new InvalidOperationException("Toss Open API 허용 IP 등록 확인 후 실시간 연결을 시작할 수 있습니다.");

        var normalized = symbols.Select(x => x?.Trim().ToUpperInvariant() ?? "")
            .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (normalized.Length is 0 or > MaxSymbols)
            throw new ArgumentOutOfRangeException(nameof(symbols), $"미국 실시간 체결 구독은 1~{MaxSymbols}개 종목만 지원합니다.");
        if (normalized.Any(x => !SymbolPattern.IsMatch(x)))
            throw new ArgumentException("미국 종목 코드는 대문자 영문, 숫자, 점 또는 하이픈이어야 합니다.", nameof(symbols));

        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var carried = normalized.Select(x => Latest.TryGetValue(x, out var trade) ? trade : null)
                .OfType<TossTrade>().ToArray();
            await StopCoreAsync(normalized);
            foreach (var trade in carried) Latest[trade.Symbol] = trade;
            LastTickAt = carried.Length > 0 ? carried.Max(x => x.Timestamp) : null;
            _runCts = new CancellationTokenSource();
            Status = "connecting";
            Message = "Toss 실시간 체결 연결 중";
            _runTask = RunWithReconnectAsync(normalized, getAccessToken, _runCts.Token);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    async Task StopCoreAsync(string[]? retainFlows = null)
    {
        var cts = _runCts;
        var task = _runTask;
        _runCts = null;
        _runTask = null;
        if (cts is not null)
        {
            cts.Cancel();
            if (task is not null)
                try { await task; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
        Status = "idle";
        Message = "실시간 연결 중지됨";
        Latest.Clear();
        if (retainFlows is null) _tape.Clear(); else _tape.Retain(retainFlows);
        LastTickAt = null;
    }

    async Task RunWithReconnectAsync(string[] symbols, Func<CancellationToken, Task<string>> getToken, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = await getToken(ct);
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Toss access token이 비어 있습니다.");
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
                await socket.ConnectAsync(Endpoint, ct);
                await SendSubscriptionAsync(socket, symbols, ct);

                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ping = SendPingsAsync(socket, connectionCts.Token);
                try { await ReceiveAsync(socket, ct); }
                finally
                {
                    connectionCts.Cancel();
                    try { await ping; } catch (OperationCanceledException) { }
                    await CloseQuietlyAsync(socket);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning("Toss websocket disconnected: {Type}", ex.GetType().Name);
                Status = "reconnecting";
                Message = "Toss 실시간 연결 재시도 중";
            }

            var delaySeconds = Math.Min(30, 1 << Math.Min(attempt++, 5));
            var jitter = Random.Shared.NextDouble() * Math.Min(1, delaySeconds * .2);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds + jitter), ct);
        }
    }

    static async Task SendSubscriptionAsync(ClientWebSocket socket, string[] symbols, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new { id = Guid.NewGuid().ToString("N") },
            new { type = "trade:us", codes = symbols }
        });
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);
    }

    static async Task SendPingsAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct))
            await socket.SendAsync("PING"u8.ToArray(), WebSocketMessageType.Text, true, ct);
    }

    async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer, ct);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (part.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Toss websocket에서 지원하지 않는 binary frame을 받았습니다.");
                if (message.Length + part.Count > MaxMessageBytes)
                    throw new InvalidDataException("Toss websocket frame이 허용 크기를 초과했습니다.");
                message.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);

            var frame = message.GetBuffer().AsSpan(0, checked((int)message.Length));
            HandleControlFrame(frame);
            if (TryParseTrade(frame, out var trade) && StoreIfFresh(trade))
            {
                RecordFlow(trade);
                LastTickAt = trade.Timestamp;
                Status = "connected";
                Message = "Toss 실시간 체결 수신 중";
            }
        }
    }

    bool StoreIfFresh(TossTrade trade)
    {
        var now = DateTimeOffset.UtcNow;
        if (trade.Timestamp > now.AddSeconds(30) || trade.Timestamp < now.AddMinutes(-3)) return false;
        while (true)
        {
            if (!Latest.TryGetValue(trade.Symbol, out var current))
            {
                if (Latest.TryAdd(trade.Symbol, trade)) return true;
                continue;
            }
            if (trade.Timestamp < current.Timestamp) return false;
            if (Latest.TryUpdate(trade.Symbol, trade, current)) return true;
        }
    }

    void HandleControlFrame(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeNode)) return;
            var type = typeNode.GetString();
            if (type == "subscriptions" && root.TryGetProperty("subscribed", out var subscribed))
            {
                var accepted = subscribed.EnumerateArray().Count(x => x.GetString()?.StartsWith("trade:us:", StringComparison.Ordinal) == true);
                if (accepted > 0)
                {
                    Status = "connected";
                    Message = $"Toss 실시간 체결 {accepted}종목 구독 중";
                }
                else
                {
                    Status = "error";
                    Message = "Toss 실시간 체결 구독이 거부되었습니다.";
                }
            }
            else if (type == "error" && root.TryGetProperty("error", out var error) &&
                     error.TryGetProperty("code", out var codeNode))
            {
                var code = codeNode.GetString() ?? "unknown";
                Status = "error";
                Message = $"Toss 실시간 오류: {code}";
                if (code == "server-shutdown") throw new WebSocketException("Toss websocket server shutdown");
            }
        }
        catch (JsonException) { }
    }

    public static bool TryParseTrade(ReadOnlySpan<byte> utf8Json, out TossTrade trade)
    {
        trade = null!;
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !root.TryGetProperty("topic", out var topicNode)) return false;
            var topic = topicNode.GetString();
            const string prefix = "trade:us:";
            if (topic is null || !topic.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var symbol = topic[prefix.Length..];
            if (!SymbolPattern.IsMatch(symbol) || !root.TryGetProperty("data", out var data) ||
                !TryDecimal(data, "price", out var price) || price <= 0 ||
                !TryDecimal(data, "volume", out var volume) || volume < 0 ||
                !data.TryGetProperty("timestamp", out var timestampNode) ||
                !DateTimeOffset.TryParse(timestampNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                !data.TryGetProperty("currency", out var currencyNode)) return false;
            var currency = currencyNode.GetString();
            if (string.IsNullOrWhiteSpace(currency)) return false;
            trade = new TossTrade(symbol, price, volume, timestamp, currency);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return false; }
    }

    static bool TryDecimal(JsonElement data, string name, out decimal value)
    {
        value = default;
        return data.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String &&
            decimal.TryParse(node.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", timeout.Token); }
        catch { /* the peer may already be gone */ }
    }

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        // DI에 구체 타입과 IRealtimeMarketStream 두 등록으로 잡혀 있어 호스트 종료 시 컨테이너가
        // 같은 인스턴스를 두 번 dispose한다. 두 번째 호출이 이미 버려진 semaphore를 기다리다
        // ObjectDisposedException을 던지지 않도록 멱등으로 만든다.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await StopAsync();
        _lifecycle.Dispose();
    }
}
