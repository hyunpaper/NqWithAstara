using Astra.Server.Application.Backtest;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using System.Text.Json;
using Xunit;

namespace Astra.Server.Tests;

public sealed class HistoricalStructureTradeReplayTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-08T13:30:00Z");

    [Fact]
    public void EntryCannotPrecedeTheAnalysisThatCreatedThePlan()
    {
        Assert.Equal(Start.AddMinutes(10), HistoricalStructureTradeReplay.EntryTime(
            Start.AddMinutes(9), Start.AddMinutes(10)));
        Assert.Equal(Start.AddMinutes(11), HistoricalStructureTradeReplay.EntryTime(
            Start.AddMinutes(11), Start.AddMinutes(10)));
    }

    [Fact]
    public void ReplayUsesAnExplicitConservativeCostProfile()
    {
        var profile = HistoricalReplayCostProfile.Conservative;
        var liquidity = profile.Liquidity(100m, Start);

        Assert.Equal(.05m, liquidity.BestAsk!.Value - liquidity.BestBid!.Value);
        Assert.Equal(1d, liquidity.BidSize);
        Assert.Equal(1d, liquidity.AskSize);
    }

    [Fact]
    public void FinalReplayOnlyAppliesBarsThatWereNotAlreadyProcessedBeforeEntry()
    {
        var trade = new SimTrade("id", "TSLA", "PULLBACK", Start, 100, 110, 95, null, null, "OPEN",
            null, null, null, 100, SessionEnd: Start.AddMinutes(3), LastPriceAt: Start);
        var trigger = new Candle(Start, 100, 101, 90, 100, 1000);
        var afterEntry = new Candle(Start.AddMinutes(1), 100, 111, 99, 110, 1000);

        var result = HistoricalStructureTradeReplay.ReplayPendingBars([trade], "TSLA",
            [trigger, afterEntry], 1);

        Assert.Equal("TARGET", Assert.Single(result).Status);
        Assert.Equal(afterEntry.Timestamp, result[0].LastEvaluatedBarAt);
    }

    [Fact]
    public async Task SameBarsAndPolicyProduceTheSameTradeSequence()
    {
        var bars = new MemoryBars();
        bars.Seed("2026-09-08", "TSLA", 120);
        var replay = new HistoricalStructureTradeReplay(bars, StructurePolicy.Default);

        var first = await replay.RunAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);
        var second = await replay.RunAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);

        Assert.Equal(JsonSerializer.Serialize(first["TSLA"]), JsonSerializer.Serialize(second["TSLA"]));
    }

    sealed class MemoryBars : IBarStore
    {
        readonly List<string> _lines = [];

        public void Seed(string day, string symbol, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var open = 100 + Math.Sin(i / 6d);
                var close = 100 + Math.Sin((i + 1) / 6d);
                _lines.Add(JsonSerializer.Serialize(new
                {
                    t = Start.AddMinutes(i).UtcDateTime,
                    o = open,
                    h = Math.Max(open, close) + .3,
                    l = Math.Min(open, close) - .3,
                    c = close,
                    v = 1000d + i
                }));
            }
        }

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(_lines.LastOrDefault());
        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["2026-09-08"]);
        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["TSLA"]);
        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(_lines.Count);
        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_lines);
        public Task DeleteDayAsync(string day, CancellationToken ct) => Task.CompletedTask;
    }
}
