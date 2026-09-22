using Astra.Server.Application.Backtest;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using System.Collections.Immutable;
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

    [Fact]
    public void 과거검증_비용모델은_운영호가와_구분된다()
    {
        var source = new ModeledHistoricalLiquiditySource(HistoricalReplayCostModel.ConservativeDefault);
        var liquidity = source.Get("TSLA", Start, 100m);

        Assert.True(source.IsModeled);
        Assert.Equal("historical.ohlcv-spread-borrow-model.v2", source.SourceName);
        Assert.Equal(.10m, (liquidity!.BestAsk!.Value - liquidity.BestBid!.Value));
    }

    [Fact]
    public async Task 상세Replay는_원천봉_커버리지와_주기지원상태를_제공한다()
    {
        var bars = new MemoryBars();
        bars.Seed("2026-09-08", "TSLA", 78, 5);

        var run = await new HistoricalStructureTradeReplay(bars, StructurePolicy.Default)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);

        var coverage = Assert.Single(run.Coverage);
        Assert.Equal(5, coverage.SourceBarMinutes);
        Assert.Equal("native-supported", coverage.GranularityStatus);
        Assert.Equal(5, coverage.ReplayBarMinutes);
        Assert.Equal("aligned", coverage.ReplayTimingStatus);
        Assert.Equal("native-five-minute-v1", coverage.EnginePath);
        Assert.Equal(78, coverage.ExpectedBars);
        Assert.Equal(78, coverage.ActualBars);
        Assert.Equal(1, coverage.CoverageRate);
    }

    [Fact]
    public async Task 지원하지않는_원천주기는_후보와_거래를_성과로_집계하지않는다()
    {
        var bars = new MemoryBars();
        bars.Seed("2026-09-08", "TSLA", 26, 15);

        var run = await new HistoricalStructureTradeReplay(bars, StructurePolicy.Default,
                new ModeledHistoricalLiquiditySource(HistoricalReplayCostModel.ConservativeDefault))
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), ["TSLA"], default);

        Assert.Empty(run.Candidates);
        Assert.Empty(run.Trades["TSLA"]);
        Assert.Equal("unsupported", Assert.Single(run.Coverage).GranularityStatus);
    }

    [Fact]
    public void 시간순선택은_최소표본과_symbol_session빈도를_모두_요구한다()
    {
        var sparse = new HistoricalStructureTradeReplay.ReplaySourceCoverage("TSLA", 10, 780, 780, 0, 1,
            5, "native-supported", 5, "aligned", "native-five-minute-v1", "fixture");
        var frequent = sparse with { Symbol = "SOXL", Sessions = 500 };

        Assert.Equal(ReplaySelectionPolicy.MinimumTrainingRows,
            ReplaySelectionPolicy.RequiredTrainingRows([sparse]));
        Assert.Equal(25, ReplaySelectionPolicy.RequiredTrainingRows([frequent]));
    }

    [Fact]
    public void Gate요약은_중복발생과_exclusive_first_failure를_분리한다()
    {
        var rows = new[]
        {
            Diagnostic("a", CandidateDisposition.Rejected,
                [TrendEvaluator.BlockerMissing5mStructure, StructuralPlanner.NoTargetStructure]),
            Diagnostic("b", CandidateDisposition.Rejected,
                [SetupDetector.BlockerAfterEntryCutoff, StructuralPlanner.NoTargetStructure]),
            Diagnostic("c", CandidateDisposition.Ready)
        };

        var summary = HistoricalStructureTradeReplay.SummarizeGates(rows);

        Assert.Equal(3, summary.Generated);
        Assert.Equal(1, summary.StructuralReady);
        Assert.Equal(2, summary.Rejected);
        Assert.True(summary.CountsOverlap);
        Assert.Equal(2, summary.Gates.Sum(x => x.ExclusiveFirstFailures));
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == TrendEvaluator.BlockerMissing5mStructure)
            .ExclusiveFirstFailures);
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == SetupDetector.BlockerAfterEntryCutoff)
            .ExclusiveFirstFailures);
        Assert.Equal(2, summary.Gates.Single(x => x.Reason == StructuralPlanner.NoTargetStructure).Candidates);
    }

    [Fact]
    public void Gate요약은_구조READY뒤_비용과_기대값_최종탈락도_거절로_집계한다()
    {
        var rows = new[]
        {
            Diagnostic("missing-cost", CandidateDisposition.Ready, costComplete: false),
            Diagnostic("non-positive-ev", CandidateDisposition.Ready, expectedNetR: 0),
            Diagnostic("approved", CandidateDisposition.Ready)
        };

        var summary = HistoricalStructureTradeReplay.SummarizeGates(rows);

        Assert.Equal(3, summary.StructuralReady);
        Assert.Equal(1, summary.FinalApproved);
        Assert.Equal(2, summary.Rejected);
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == StructuralPlanner.MissingLiquidityCost)
            .ExclusiveFirstFailures);
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == "EXPECTED_NET_R_NON_POSITIVE")
            .ExclusiveFirstFailures);
    }

    [Fact]
    public void 동일가격경로의_5분원천은_1분엔진의_5분구조로_재집계되지_않는다()
    {
        var minutePath = Enumerable.Range(0, 60).Select(i =>
        {
            var open = 100 + Math.Sin(i / 6d);
            var close = 100 + Math.Sin((i + 1) / 6d);
            return new Candle(Start.AddMinutes(i), open, Math.Max(open, close) + .3,
                Math.Min(open, close) - .3, close, 1000 + i);
        }).ToArray();
        var fiveMinutePath = minutePath.Chunk(5).Select(chunk => new Candle(chunk[0].Timestamp,
            chunk[0].Open, chunk.Max(x => x.High), chunk.Min(x => x.Low), chunk[^1].Close,
            chunk.Sum(x => x.Volume))).ToArray();
        var session = new MarketSession(true, "테스트", null, Start, Start.AddHours(6.5));
        var now = Start.AddMinutes(60);
        var oneMinute = StructureSnapshotFactory.Create("TSLA", session, minutePath, [], minutePath[^1].Close,
            now, now, 1, StructurePolicy.Default, barDuration: TimeSpan.FromMinutes(1));
        var fiveMinute = StructureSnapshotFactory.Create("TSLA", session, fiveMinutePath, [], fiveMinutePath[^1].Close,
            now, now, 1, StructurePolicy.Default, barDuration: TimeSpan.FromMinutes(5));

        Assert.Equal(12, oneMinute.FiveMinuteBars.Length);
        Assert.Equal(12, fiveMinute.FiveMinuteBars.Length);
        Assert.All(fiveMinute.Bars.Bars, x => Assert.Equal(TimeSpan.FromMinutes(5), x.Duration));
        Assert.All(fiveMinute.FiveMinuteBars, x => Assert.Equal(TimeSpan.FromMinutes(5), x.Duration));
    }

    static HistoricalStructureTradeReplay.ReplayCandidateDiagnostic Diagnostic(string id,
        CandidateDisposition disposition, string[]? reasons = null, bool costComplete = true,
        double? expectedNetR = 1) => new("TSLA", new DateOnly(2026, 9, 8),
        id, Start, TradeSide.Long, "RANGE_NORMAL", disposition, disposition == CandidateDisposition.Ready,
        disposition == CandidateDisposition.Ready && costComplete && expectedNetR is > 0,
        costComplete, false, "test", expectedNetR,
        (reasons ?? []).ToImmutableArray(), ImmutableArray<string>.Empty);

    sealed class MemoryBars : IBarStore
    {
        readonly List<string> _lines = [];

        public void Seed(string day, string symbol, int count, int minutes = 1)
        {
            for (var i = 0; i < count; i++)
            {
                var open = 100 + Math.Sin(i / 6d);
                var close = 100 + Math.Sin((i + 1) / 6d);
                _lines.Add(JsonSerializer.Serialize(new
                {
                    t = Start.AddMinutes(i * minutes).UtcDateTime,
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

        public StructureLiquidity? Get(string symbol, DateTimeOffset observedAt, decimal referencePrice)
        {
            Calls++;
            return new StructureLiquidity(99.99m, 100.01m, observedAt, 100, 100);
        }
    }
}
