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
    public void ReplayConfirmationUsesTheSharedBreakoutBoundaryContract()
    {
        var pending = new PendingEntry("replay-breakout", "TSLA", TradeSide.Long,
            Start, Start.AddMinutes(1), Start.AddMinutes(3), 95, 110, 101, "plan", "policy",
            "BREAKOUT", 100, "hold-breakout-boundary.1");
        var confirmationBar = new Candle(Start.AddMinutes(1), 101, 102, 99, 100, 10);

        var resolution = HistoricalStructureTradeReplay.ResolvePending(pending, confirmationBar,
            Start.AddMinutes(2));

        Assert.True(resolution.Clear);
        Assert.Equal(PendingEntryDecision.RejectedThesisInvalidated, resolution.Confirmation!.Decision);
        Assert.Equal(PendingEntryDecision.RejectedThesisInvalidated.ToString(), resolution.Reason);
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
    public async Task 종목병렬도_1과_2는_입력순서와_누적결과가_같다()
    {
        var bars = new MemoryBars();
        bars.Seed("2026-09-08", "TSLA", 78, 5);
        string[] symbols = ["SOXL", "TSLA", "KORU"];

        var sequential = await new HistoricalStructureTradeReplay(bars, StructurePolicy.Default,
                maxDegreeOfParallelism: 1)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), symbols, default);
        var parallel = await new HistoricalStructureTradeReplay(bars, StructurePolicy.Default,
                maxDegreeOfParallelism: 2)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8), symbols, default);

        Assert.Equal(JsonSerializer.Serialize(sequential), JsonSerializer.Serialize(parallel));
        Assert.Equal(symbols.Order(StringComparer.Ordinal), parallel.Coverage.Select(x => x.Symbol));
    }

    [Fact]
    public async Task 종목Replay는_동시에_두개까지만_읽는다()
    {
        var bars = new ConcurrencyTrackingBars();

        var run = await new HistoricalStructureTradeReplay(bars, StructurePolicy.Default,
                maxDegreeOfParallelism: 2)
            .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
                ["SOXL", "TSLA", "KORU", "NVDA"], default);

        Assert.Equal(2, bars.MaximumConcurrency);
        Assert.Equal(4, run.Trades.Count);
    }

    [Fact]
    public async Task 종목Replay는_저장소읽기_취소를_호출자에게_전파한다()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HistoricalStructureTradeReplay(new CancellationBars(), StructurePolicy.Default)
                .RunDetailedAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
                    ["SOXL", "TSLA", "KORU"], cancellation.Token));
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
    public void FeeOnly프로필은_잠금호가와_0spread_0borrow를_명시한다()
    {
        var model = HistoricalReplayCostModel.FeeOnlyLongOnly;
        var source = new ModeledHistoricalLiquiditySource(model);
        var liquidity = source.Get("TSLA", Start, 100m);

        Assert.Equal("historical.fee-only-long-only.v1", source.SourceName);
        Assert.Equal(0, model.SpreadPercent);
        Assert.Null(model.ShortBorrowPercent);
        Assert.Equal(100m, liquidity!.BestBid);
        Assert.Equal(100m, liquidity.BestAsk);
    }

    [Fact]
    public void FeeOnly_long거래는_gross에서_왕복수수료_0점2퍼센트만_차감한다()
    {
        var trade = new SimTrade("long", "TSLA", "PULLBACK", Start, 100, 110, 95,
            null, null, "OPEN", null, null, null, 100, SessionEnd: Start.AddHours(1));
        var result = HistoricalStructureTradeReplay.ReplayPendingBars([trade], "TSLA",
            [new Candle(Start.AddMinutes(1), 100, 110, 99, 110, 1000)], 0);

        Assert.Equal(9.8, Assert.Single(result).PnlPercent);
    }

    [Fact]
    public void Replay_longOnly가드는_borrow설정과_무관하게_short를_선택전에_거절한다()
    {
        var longCandidate = Candidate("long", TradeSide.Long);
        var shortCandidate = Candidate("short", TradeSide.Short) with { KindName = "A_BREAKOUT" };
        var policy = StructurePolicy.Default with { ShortBorrowCostPercent = .02 };

        var filtered = HistoricalStructureTradeReplay.ApplyReplayPositionPolicy([shortCandidate, longCandidate]);

        Assert.NotNull(policy.ShortBorrowCostPercent);
        var rejected = Assert.Single(filtered.Where(x => x.Side == TradeSide.Short));
        Assert.Equal(CandidateDisposition.Rejected, rejected.Disposition);
        Assert.Contains(HistoricalStructureTradeReplay.ReplayLongOnlyShortRejected, rejected.RejectionCodes);
        Assert.Equal(longCandidate.EventId, CandidateSelection.SelectPreferred(filtered)!.EventId);
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
        Assert.Equal(2, summary.GateRejected);
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
        Assert.Equal(1, summary.GateApproved);
        Assert.Equal(2, summary.GateRejected);
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == StructuralPlanner.MissingLiquidityCost)
            .ExclusiveFirstFailures);
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == "EXPECTED_NET_R_NON_POSITIVE")
            .ExclusiveFirstFailures);
    }

    [Fact]
    public void ReboundVwap거절은_진단과_실행퍼널에_동일한_사유로_남는다()
    {
        var row = Diagnostic("rebound-above-vwap", CandidateDisposition.Rejected,
            [SetupDetector.CodeReboundLongAboveVwap]);

        var summary = HistoricalStructureTradeReplay.SummarizeGates([row]);

        Assert.Equal(1, summary.GateRejected);
        Assert.Equal(0, summary.ExecutionFunnel!.Preferred);
        Assert.True(summary.ExecutionFunnel.Reconciled);
        Assert.Equal(1, summary.Gates.Single(x => x.Reason == SetupDetector.CodeReboundLongAboveVwap).Candidates);
    }

    [Fact]
    public void 실행퍼널은_EventId별_후보부터_체결까지_서로다른_분모를_보존한다()
    {
        var rows = new[]
        {
            Diagnostic("candidate-only", CandidateDisposition.Rejected),
            Diagnostic("preferred", CandidateDisposition.Ready) with { Preferred = true },
            Diagnostic("pending", CandidateDisposition.Ready) with { Preferred = true, PendingQueued = true },
            Diagnostic("confirmed", CandidateDisposition.Ready) with
                { Preferred = true, PendingQueued = true, Confirmed = true },
            Diagnostic("filled", CandidateDisposition.Ready) with
                { Preferred = true, PendingQueued = true, Confirmed = true, Filled = true }
        };

        var funnel = HistoricalStructureTradeReplay.SummarizeGates(rows).ExecutionFunnel!;

        Assert.Equal(5, funnel.Candidates);
        Assert.Equal(4, funnel.Preferred);
        Assert.Equal(3, funnel.PendingQueued);
        Assert.Equal(2, funnel.Confirmed);
        Assert.Equal(1, funnel.Filled);
        Assert.True(funnel.Reconciled);
        Assert.Equal("distinct-entry-event-id", funnel.Unit);
    }

    [Fact]
    public void 실행퍼널은_선행단계없는_체결을_불일치로_표시한다()
    {
        var row = Diagnostic("broken", CandidateDisposition.Ready) with { Filled = true };

        Assert.False(HistoricalStructureTradeReplay.SummarizeGates([row]).ExecutionFunnel!.Reconciled);
    }

    [Fact]
    public void Preferred이후_재평가된_nonReady상태는_승인과_체결단계를_되돌리지않는다()
    {
        var attempted = Diagnostic("attempt", CandidateDisposition.Ready) with
        {
            Preferred = true,
            PendingQueued = true,
            Confirmed = true,
            Filled = true
        };
        var later = Diagnostic("attempt", CandidateDisposition.Expired,
            ["DISPOSITION_EXPIRED"], costComplete: false, expectedNetR: null);

        var reconciled = HistoricalStructureTradeReplay.ReconcileDiagnostic(attempted, later);
        var summary = HistoricalStructureTradeReplay.SummarizeGates([reconciled]);

        Assert.True(reconciled.StructuralReady);
        Assert.True(reconciled.FinalApproved);
        Assert.True(reconciled.GateApproved);
        Assert.True(reconciled.Filled);
        Assert.Equal(1, summary.FinalApproved);
        Assert.Equal(1, summary.GateApproved);
        Assert.Equal(0, summary.Rejected);
        Assert.Equal(0, summary.GateRejected);
    }

    [Fact]
    public void 기존후보Json은_FinalApproved를_보존하고_새GateApproved를_기본값으로읽는다()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
            Diagnostic("legacy", CandidateDisposition.Ready), options))!.AsObject();
        json.Remove("gateApproved");

        var restored = JsonSerializer.Deserialize<HistoricalStructureTradeReplay.ReplayCandidateDiagnostic>(
            json.ToJsonString(), options);

        Assert.NotNull(restored);
        Assert.True(restored.FinalApproved);
        Assert.True(restored.GateApproved);
        Assert.Equal(1, HistoricalStructureTradeReplay.SummarizeGates([restored]).GateApproved);
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

    static EntryCandidate Candidate(string id, TradeSide side)
    {
        var quality = new EntryQualityResult(60, [], [], [], [], []);
        var planning = new PlanEvaluation(null, [], null, null, null, null, null, null,
            null, 2, null, null, null, null, false, null, []);
        return new EntryCandidate(id, id, SetupKind.Pullback, "PULLBACK", "zone", Start, Start, Start, Start,
            Start.AddMinutes(5), CandidateDisposition.Ready, 100, 99, 60, quality, null, planning,
            [], [], false, false, null, side);
    }

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

    sealed class ConcurrencyTrackingBars : IBarStore
    {
        int _active;
        int _maximum;
        public int MaximumConcurrency => _maximum;

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["2026-09-08"]);
        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(0);
        public async Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximum, active);
            try
            {
                await Task.Delay(50, ct);
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
        public Task DeleteDayAsync(string day, CancellationToken ct) => Task.CompletedTask;
    }

    sealed class CancellationBars : IBarStore
    {
        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["2026-09-08"]);
        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) => Task.FromResult(0);
        public async Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        }
        public Task DeleteDayAsync(string day, CancellationToken ct) => Task.CompletedTask;
    }

    static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
