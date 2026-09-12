using Astra.Server;
using Astra.Server.Application;
using Xunit;

namespace Astra.Server.Tests;

public sealed class BarStoreServiceTests
{
    static readonly DateTimeOffset SessionStart = new(2026, 9, 11, 13, 30, 0, TimeSpan.Zero);

    static Candle Bar(int minute, double close = 100) =>
        new(SessionStart.AddMinutes(minute), close, close + 1, close - 1, close, 1000);

    [Fact]
    public async Task NewCompletedBarsAreAppendedInOrder()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));

        await service.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1), Bar(2)], default);

        Assert.Equal(3, store.Lines("2026-09-11", "NVDA").Count);
    }

    [Fact]
    public async Task SameBarSavedTwiceProducesOneLine()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));

        await service.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1)], default);
        await service.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1)], default);

        Assert.Equal(2, store.Lines("2026-09-11", "NVDA").Count);
    }

    [Fact]
    public async Task OnlyBarsNewerThanTheLastSavedOneAreAppended()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));
        await service.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1)], default);

        await service.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1), Bar(2)], default);

        Assert.Equal(3, store.Lines("2026-09-11", "NVDA").Count);
    }

    [Fact]
    public async Task RestartRestoresLastSavedTimestampFromTheFileTail()
    {
        var store = new MemoryBarStore();
        var first = new BarStoreService(store, new FixedClock(SessionStart));
        await first.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1)], default);

        // 재기동을 모사한다 — 메모리 캐시가 없는 새 인스턴스가 같은 파일을 이어받는다.
        var restarted = new BarStoreService(store, new FixedClock(SessionStart));
        await restarted.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1), Bar(2)], default);

        Assert.Equal(3, store.Lines("2026-09-11", "NVDA").Count);
    }

    [Fact]
    public async Task EachAppendedLineCarriesTUtcIsoAndOhlcv()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));

        await service.SaveNewBarsAsync("NVDA", [Bar(0, 123.45)], default);

        var line = Assert.Single(store.Lines("2026-09-11", "NVDA"));
        using var json = System.Text.Json.JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal(SessionStart.UtcDateTime, root.GetProperty("t").GetDateTime());
        Assert.Equal(123.45, root.GetProperty("o").GetDouble());
        Assert.Equal(124.45, root.GetProperty("h").GetDouble());
        Assert.Equal(122.45, root.GetProperty("l").GetDouble());
        Assert.Equal(123.45, root.GetProperty("c").GetDouble());
        Assert.Equal(1000, root.GetProperty("v").GetDouble());
    }

    [Fact]
    public async Task LineSizeStaysWithinTheDiskBudgetAssumption()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));
        var bars = Enumerable.Range(0, 390).Select(i => Bar(i, 100 + i * 0.01)).ToArray();

        await service.SaveNewBarsAsync("NVDA", bars, default);

        // C7: 15종목×390봉×~100B ≈ 0.6MB/일, 30일 ≈ 18MB — 한 줄이 이 예산을 벗어나지 않는지 고정한다.
        var totalBytes = store.Lines("2026-09-11", "NVDA").Sum(x => System.Text.Encoding.UTF8.GetByteCount(x) + 1);
        Assert.True(totalBytes < 390 * 130, $"실제 바이트 {totalBytes}");
    }

    [Fact]
    public async Task CleanupRunsOnceAndDeletesOnlyDaysOlderThanThirtyDays()
    {
        var store = new MemoryBarStore();
        store.SeedDay("2026-08-01"); // 41일 전 — 삭제 대상
        store.SeedDay("2026-08-20"); // 22일 전 — 보존
        store.SeedDay("2026-09-11"); // 오늘 — 보존
        var service = new BarStoreService(store, new FixedClock(SessionStart));

        await service.SaveNewBarsAsync("NVDA", [Bar(0)], default);

        Assert.DoesNotContain("2026-08-01", store.Days);
        Assert.Contains("2026-08-20", store.Days);
        Assert.Contains("2026-09-11", store.Days);
    }

    [Fact]
    public async Task CleanupOnlyRunsOnceForTheSameTradingDate()
    {
        var store = new MemoryBarStore();
        store.SeedDay("2026-08-01");
        var service = new BarStoreService(store, new FixedClock(SessionStart));
        await service.SaveNewBarsAsync("NVDA", [Bar(0)], default);
        store.SeedDay("2026-08-01"); // 같은 날 안에 재생성돼도 두 번째 저장에서는 다시 지워지지 않는다.

        await service.SaveNewBarsAsync("NVDA", [Bar(1)], default);

        Assert.Contains("2026-08-01", store.Days);
    }

    [Fact]
    public async Task NoBarsIsANoOp()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));

        await service.SaveNewBarsAsync("NVDA", [], default);

        Assert.Empty(store.Lines("2026-09-11", "NVDA"));
    }

    [Fact]
    public async Task HealthCountsTodaysBarsSeparatelyFromTheBenchmark()
    {
        var store = new MemoryBarStore();
        var service = new BarStoreService(store, new FixedClock(SessionStart));
        await service.SaveNewBarsAsync("NVDA", [Bar(0), Bar(1)], default);
        await service.SaveNewBarsAsync("TSLA", [Bar(0)], default);
        await service.SaveNewBarsAsync("QQQ", [Bar(0), Bar(1), Bar(2)], default);

        var health = await service.HealthAsync(DateOnly.FromDateTime(SessionStart.UtcDateTime), "QQQ", default);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(health));
        var root = json.RootElement;

        Assert.Equal(1, root.GetProperty("days").GetInt32());
        Assert.Equal(3, root.GetProperty("todayBars").GetInt32());
        var benchmark = root.GetProperty("benchmark");
        Assert.Equal("QQQ", benchmark.GetProperty("symbol").GetString());
        Assert.Equal(3, benchmark.GetProperty("todayBars").GetInt32());
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed class MemoryBarStore : IBarStore
    {
        readonly Dictionary<(string Day, string Symbol), List<string>> _files = new();
        public IReadOnlyCollection<string> Days => _files.Keys.Select(x => x.Day).Distinct().ToArray();

        public IReadOnlyList<string> Lines(string day, string symbol) =>
            _files.TryGetValue((day, symbol.ToUpperInvariant()), out var lines) ? lines : [];

        public void SeedDay(string day) => _files[(day, "SEED")] = ["seed"];

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct)
        {
            var lines = Lines(day, symbol);
            return Task.FromResult(lines.Count == 0 ? null : lines[^1]);
        }

        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct)
        {
            var key = (day, symbol.ToUpperInvariant());
            if (!_files.TryGetValue(key, out var lines)) _files[key] = lines = [];
            lines.Add(line);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(Days.ToArray());

        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Where(x => x.Day == day).Select(x => x.Symbol).ToArray());

        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(Lines(day, symbol).Count);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(Lines(day, symbol));

        public Task DeleteDayAsync(string day, CancellationToken ct)
        {
            foreach (var key in _files.Keys.Where(x => x.Day == day).ToArray()) _files.Remove(key);
            return Task.CompletedTask;
        }
    }
}
