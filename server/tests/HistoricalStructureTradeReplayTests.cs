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
    public void PendingReplayConfirmationRequiresExactNextBarAndClearsOnMissingBar()
    {
        var pending = new PendingEntry("replay-event", "TSLA", TradeSide.Long,
            Start, Start.AddMinutes(1), Start.AddMinutes(3), 95, 110, 100, "plan", "policy");
        var missing = PendingEntryPolicy.Confirm(pending,
            new Candle(Start.AddMinutes(2), 100, 102, 99, 101, 10), Start.AddMinutes(3), 101);
        Assert.Equal(PendingEntryDecision.RejectedUnobservedFill, missing.Decision);
        Assert.Equal("UNOBSERVED", missing.EvidenceStatus);

        var exact = PendingEntryPolicy.Confirm(pending,
            new Candle(Start.AddMinutes(1), 100, 102, 99, 101, 10), Start.AddMinutes(2), 101);
        Assert.Equal(PendingEntryDecision.Confirmed, exact.Decision);
        Assert.Equal(101, exact.FillPrice);

        var resolution = HistoricalStructureTradeReplay.ResolvePending(pending,
            new Candle(Start.AddMinutes(2), 100, 102, 99, 101, 10), Start.AddMinutes(3));
        Assert.True(resolution.Clear);
        Assert.Equal("MISSING_CONFIRMATION_BAR", resolution.Reason);
        var consumed = new HashSet<string>([pending.EntryEventId]);
        Assert.False(HistoricalStructureTradeReplay.ShouldQueuePending(consumed, pending));
        consumed.Clear();
        Assert.True(HistoricalStructureTradeReplay.ShouldQueuePending(consumed, pending));
    }

    [Fact]
    public void ReplayEntryRequestCarriesCompletedBarsForCooldownEvaluation()
    {
        var stopped = new SimTrade("stopped", "TSLA", "PULLBACK", Start,
            100, 110, 95, null, null, "STOP", 95, Start.AddMinutes(1), -5, 95,
            SessionEnd: Start.AddHours(1));
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), StructurePolicy.Default);
        Assert.True(evaluation.Plan is not null);
        var plan = evaluation.Plan!;
        var context = StructuralSimulation.Freeze(plan, "replay-cooldown", "UP", 1, 50, Start, null);
        var request = new StructuralEntryRequest("TSLA", Start.AddMinutes(3), Start.AddMinutes(3),
            Start.AddHours(1), context, [Start, Start.AddMinutes(1), Start.AddMinutes(2)], Start);
        var result = StructuralSimulation.Enter([stopped], request, new StructurePolicy { StopReentryCooldownBars = 3 });
        Assert.Equal(StructuralEntryOutcome.BlockedByStopCooldown, result.Outcome);
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

    [Fact]
    public async Task 상세Replay는_후보_게이트_사유를_보존하고_결정적이다()
    {
        var first = await new HistoricalStructureTradeReplay(new MemoryBars(), StructurePolicy.Default)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);
        var second = await new HistoricalStructureTradeReplay(new MemoryBars(), StructurePolicy.Default)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);

        Assert.Equal(JsonSerializer.Serialize(first.Candidates), JsonSerializer.Serialize(second.Candidates));
        Assert.All(first.Candidates, x => Assert.NotNull(x.EventId));
        Assert.All(first.Candidates, x => Assert.False(string.IsNullOrWhiteSpace(x.Symbol)));
    }

    [Fact]
    public async Task 상세Replay는_제공된_과거호가를_비용입력으로_전달한다()
    {
        var source = new FixedLiquiditySource();
        var bars = new MemoryBars();
        bars.Seed("2026-09-08", "TSLA", 120);
        await new HistoricalStructureTradeReplay(bars, StructurePolicy.Default, source)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);

        Assert.True(source.Calls > 0);
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

    sealed class FixedLiquiditySource : IHistoricalLiquiditySource
    {
        public int Calls { get; private set; }

        public StructureLiquidity? Get(string symbol, DateTimeOffset observedAt)
        {
            Calls++;
            return new StructureLiquidity(99.99m, 100.01m, observedAt, 100, 100);
        }
    }
}
