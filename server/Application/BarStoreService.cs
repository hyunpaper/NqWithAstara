using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Server.Domain;

namespace Astra.Server.Application;

/// <summary>컨플루언스 측정 파이프라인(K4) 설정(#165 C5·C7). 감시 목록과 별도로 RS 기준 종목을 폴링한다.</summary>
public sealed class ConfluenceOptions
{
    public bool Enabled { get; set; } = true;
    public string BenchmarkSymbol { get; set; } = "QQQ";
}

/// <summary>
/// 완료 1분봉을 <see cref="IBarStore"/>에 저장한다(#165 C5). 심볼별 마지막 저장 시각을 메모리에 캐시하고,
/// 재기동 시에는 파일의 마지막 줄에서 복원한다 — 같은 봉을 두 번 넘겨도 한 줄만 쓴다.
/// 30일이 지난 날짜 폴더는 하루 1회만 정리한다.
/// </summary>
public sealed class BarStoreService(IBarStore store, TimeProvider clock)
{
    public const int RetentionDays = 30;

    readonly ConcurrentDictionary<string, (string Day, DateTimeOffset LastAt)> _last = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim _cleanupGate = new(1, 1);
    DateOnly? _cleanedThrough;

    public async Task SaveNewBarsAsync(string symbol, IReadOnlyList<Candle> bars, CancellationToken ct)
    {
        await CleanupIfNeededAsync(MarketRules.TradingDate(clock.GetUtcNow()), ct);
        if (bars.Count == 0) return;
        foreach (var group in bars.GroupBy(b => MarketRules.TradingDate(b.Timestamp)).OrderBy(g => g.Key))
        {
            var day = group.Key.ToString("yyyy-MM-dd");
            var last = await LastSavedAsync(symbol, day, ct);
            foreach (var bar in group.OrderBy(b => b.Timestamp))
            {
                if (bar.Timestamp <= last) continue;
                await store.AppendAsync(day, symbol, ToLine(bar), ct);
                last = bar.Timestamp;
            }
            _last[symbol] = (day, last);
        }
    }

    public async Task<object> HealthAsync(DateOnly today, string benchmarkSymbol, CancellationToken ct)
    {
        var days = await store.ListDaysAsync(ct);
        var todayKey = today.ToString("yyyy-MM-dd");
        var symbols = await store.ListSymbolsAsync(todayKey, ct);
        var total = 0;
        foreach (var symbol in symbols)
            if (!string.Equals(symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase))
                total += await store.CountLinesAsync(todayKey, symbol, ct);
        var benchmarkBars = symbols.Any(x => string.Equals(x, benchmarkSymbol, StringComparison.OrdinalIgnoreCase))
            ? await store.CountLinesAsync(todayKey, benchmarkSymbol, ct) : 0;
        return new { days = days.Count, todayBars = total, benchmark = new { symbol = benchmarkSymbol, todayBars = benchmarkBars } };
    }

    async Task<DateTimeOffset> LastSavedAsync(string symbol, string day, CancellationToken ct)
    {
        if (_last.TryGetValue(symbol, out var cached) && cached.Day == day) return cached.LastAt;
        var line = await store.LastLineAsync(day, symbol, ct);
        var last = line is null ? DateTimeOffset.MinValue : ParseTimestamp(line);
        _last[symbol] = (day, last);
        return last;
    }

    async Task CleanupIfNeededAsync(DateOnly today, CancellationToken ct)
    {
        if (_cleanedThrough == today) return;
        await _cleanupGate.WaitAsync(ct);
        try
        {
            if (_cleanedThrough == today) return;
            var cutoff = today.AddDays(-RetentionDays);
            foreach (var day in await store.ListDaysAsync(ct))
                if (DateOnly.TryParseExact(day, "yyyy-MM-dd", out var parsed) && parsed < cutoff)
                    await store.DeleteDayAsync(day, ct);
            _cleanedThrough = today;
        }
        finally { _cleanupGate.Release(); }
    }

    static string ToLine(Candle bar) => JsonSerializer.Serialize(new
    {
        t = bar.Timestamp.UtcDateTime,
        o = bar.Open,
        h = bar.High,
        l = bar.Low,
        c = bar.Close,
        v = bar.Volume,
    });

    static DateTimeOffset ParseTimestamp(string line) =>
        JsonDocument.Parse(line).RootElement.GetProperty("t").GetDateTimeOffset();
}

/// <summary>
/// 워치리스트와 분리된 RS 벤치마크(QQQ) 폴링(#165 C7). 시그널·시뮬·화면 목록에는 편입하지 않고 봉만 저장한다.
/// 조회 실패는 진단 로그로만 남긴다.
/// </summary>
public sealed class BenchmarkPollingService(IMarketDataGateway toss, BarStoreService bars, ConfluenceOptions options,
    IMonitorDiagnostics diagnostics) : IBenchmarkBarSource
{
    volatile IReadOnlyList<Candle> _latest = [];

    public string Symbol => options.BenchmarkSymbol;

    /// <summary>마지막 poll의 완료 벤치마크 봉. RS 기법(#167)이 시각 동기 검사를 하고 읽는다.</summary>
    public IReadOnlyList<Candle> Bars => _latest;

    public async Task PollAsync(MarketSession market, CancellationToken ct)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.BenchmarkSymbol)) return;
        try
        {
            var candles = await toss.Candles(options.BenchmarkSymbol, ct);
            var quote = await toss.Price(options.BenchmarkSymbol, ct);
            var completed = MarketRules.CompletedRegularBars(candles, market, quote.At);
            _latest = completed;
            await bars.SaveNewBarsAsync(options.BenchmarkSymbol, completed, ct);
        }
        catch (Exception ex) { diagnostics.MarketDataFailed(options.BenchmarkSymbol, "benchmark-poll", ex); }
    }
}
