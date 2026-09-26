using System.Collections.Immutable;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

public sealed class HistoricalStructureTradeReplay(IBarStore store, StructurePolicy policy,
    IHistoricalLiquiditySource? liquiditySource = null)
{
    public const string GateAttributionOrder =
        "data-quality>session-time>direction>structure>entry-quality>risk-cost>expected-value>other.v1";

    /// <summary>replay에서 생성된 후보의 1단계 후보화·2단계 최종 게이트 결과를 보존한다.</summary>
    public sealed record ReplayCandidateDiagnostic(string Symbol, DateOnly SessionDate,
        string EventId, DateTimeOffset SignalAt, TradeSide Side, string Regime,
        CandidateDisposition Disposition, bool StructuralReady, bool FinalApproved,
        bool CostComplete, bool CostModeled, string CostSource,
        double? ExpectedNetR, ImmutableArray<string> RejectionReasons, ImmutableArray<string> FeatureContributions,
        double? RealizedNetR = null, ConditionalReturnForecast? Forecast = null,
        StrategyRegimeAssessment? RegimeAssessment = null);

    public sealed record ReplaySourceCoverage(string Symbol, int Sessions, int ExpectedBars, int ActualBars,
        int MissingBars, double CoverageRate, double SourceBarMinutes, string GranularityStatus,
        double ReplayBarMinutes, string ReplayTimingStatus, string EnginePath, string Limitation);

    public sealed record ReplayGateCount(string Reason, int Candidates, int ExclusiveFirstFailures);

    public sealed record ReplayGateSummary(int Generated, int StructuralReady, int FinalApproved, int Rejected,
        int LongCandidates, int ShortCandidates, bool CountsOverlap, string ExclusiveAttributionOrder,
        ImmutableArray<ReplayGateCount> Gates);

    public sealed record ReplayRun(ImmutableDictionary<string, ImmutableArray<SimTrade>> Trades,
        ImmutableArray<ReplayCandidateDiagnostic> Candidates, ImmutableArray<ReplaySourceCoverage> Coverage,
        ReplayGateSummary GateSummary);

    public sealed record ReplayPendingResolution(bool Clear, EntryConfirmation? Confirmation, string Reason);

    /// <summary>운영 replay loop와 테스트가 공유하는 pending 수명주기 판정이다.</summary>
    public static ReplayPendingResolution ResolvePending(PendingEntry pending, Candle current, DateTimeOffset observedAt,
        TimeSpan? barDuration = null)
    {
        if (current.Timestamp == pending.ConfirmationBarStart)
        {
            var confirmation = PendingEntryPolicy.Confirm(pending, current, observedAt,
                current.Close, "REPLAY_CONFIRMATION_BAR_CLOSE", barDuration);
            return new(true, confirmation, confirmation.Decision.ToString());
        }
        if (current.Timestamp > pending.ConfirmationBarStart || observedAt >= pending.ExpiresAt)
            return new(true, null, "MISSING_CONFIRMATION_BAR");
        return new(false, null, "WAITING_CONFIRMATION_BAR");
    }

    public static bool ShouldQueuePending(ISet<string> consumedEventIds, PendingEntry pending) =>
        !consumedEventIds.Contains(pending.EntryEventId);

    // Replay에서도 실시간과 같은 확인봉 경계를 유지한다. 계획과 체결을 한 튜플로
    // 보관해 다음 봉이 닫히기 전에는 SimTrade를 생성하지 않는다.
    sealed record PendingReplayEntry(PendingEntry Pending, FrozenStructureContext Context,
        EntryCandidate Candidate);

    public async Task<ImmutableDictionary<string, ImmutableArray<SimTrade>>> RunAsync(DateOnly from, DateOnly to,
        IReadOnlyList<string> symbols, CancellationToken ct, double? expectedValueThreshold = null)
        => (await RunDetailedAsync(from, to, symbols, ct, expectedValueThreshold)).Trades;

    /// <summary>
    /// 운영 replay와 같은 계산을 실행하면서 후보 생성·최종 게이트의 사유를 함께 반환한다.
    /// 후보 사유는 EventId별 마지막 관측으로 접어 동일 후보의 반복 관측을 중복 집계하지 않는다.
    /// </summary>
    public async Task<ReplayRun> RunDetailedAsync(DateOnly from, DateOnly to,
        IReadOnlyList<string> symbols, CancellationToken ct, double? expectedValueThreshold = null)
    {
        var replayPolicy = expectedValueThreshold is { } threshold
            ? policy with { ExpectedValueFeatureThreshold = threshold }
            : policy;
        var days = (await store.ListDaysAsync(ct)).Where(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", out var day)
            && day >= from && day <= to).Order().ToArray();
        var result = symbols.ToImmutableDictionary(x => x, _ => new List<SimTrade>(),
            StringComparer.OrdinalIgnoreCase).ToBuilder();
        var daily = symbols.ToDictionary(x => x, _ => new List<Candle>(), StringComparer.OrdinalIgnoreCase);
        var candidateDiagnostics = new Dictionary<string, ReplayCandidateDiagnostic>(StringComparer.Ordinal);
        var coverage = symbols.ToDictionary(x => x, _ => new CoverageAccumulator(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var day in days)
            foreach (var symbol in symbols)
            {
                ct.ThrowIfCancellationRequested();
                var bars = ConfluenceReplay.Parse(await store.ReadLinesAsync(day, symbol, ct))
                    .Select(x => new Candle(x.Start, (double)x.Open, (double)x.High, (double)x.Low,
                        (double)x.Close, (double)x.Volume)).ToImmutableArray();
                if (bars.Length == 0) continue;
                var barSpan = InferSourceSpan(bars);
                var timeframe = ReplayTimeframePolicy.Contract(barSpan);
                var sessionStart = bars[0].Timestamp;
                var sessionEnd = sessionStart.AddHours(6.5);
                coverage[symbol].Add(bars, sessionStart, sessionEnd, barSpan, timeframe);
                if (!timeframe.Supported) continue;
                var sessionPolicy = ReplayTimeframePolicy.Apply(replayPolicy, timeframe);
                var market = new MarketSession(true, "과거 replay", null, sessionStart, sessionEnd);
                var previousZones = ImmutableArray<PriceZone>.Empty;
                var retired = ImmutableArray<string>.Empty;
                var latch = StructuralLatch.Empty(symbol, sessionStart, sessionPolicy.PolicyHash);
                var processedBars = 0;
                PendingReplayEntry? pending = null;
                var pendingEventIds = new HashSet<string>(StringComparer.Ordinal);

                for (var index = 0; index < bars.Length && bars[index].Timestamp.Add(barSpan) < sessionEnd; index++)
                {
                    var current = bars[index];
                    var openBeforeBar = result[symbol].Count(x => x.Status == "OPEN");
                    result[symbol] = SimulationEngine.ReplayBars(result[symbol], symbol, [current]);
                    var exitedThisPoll = result[symbol].Count(x => x.Status == "OPEN") < openBeforeBar;
                    processedBars = index + 1;
                    var now = current.Timestamp.Add(barSpan);
                    var completedStarts = bars.Take(index + 1).Select(x => x.Timestamp).ToArray();
                    // 이전 신호의 다음 완료 봉에서만 체결을 확인한다. current.Close는
                    // 해당 봉이 닫힌 뒤에만 관측 가능하므로 look-ahead가 없다.
                    if (pending is { } queued && current.Timestamp == queued.Pending.ConfirmationBarStart)
                    {
                        var resolution = ResolvePending(queued.Pending, current, now, barSpan);
                        if (resolution.Confirmation is { Decision: PendingEntryDecision.Confirmed } confirmation)
                        {
                            var entered = StructuralSimulation.Enter(result[symbol], new StructuralEntryRequest(symbol,
                                queued.Candidate.TriggerBarStart, now, sessionEnd, queued.Context,
                                completedStarts, sessionStart, queued.Candidate.Plan?.TargetZoneSnapshot.Aliases, confirmation,
                                RequireCompleteLiquidityCost: sessionPolicy.RequireCompleteLiquidityCost), sessionPolicy);
                            result[symbol] = entered.Trades;
                        }
                        pendingEventIds.Add(queued.Pending.EntryEventId);
                        pending = null;
                    }
                    else if (pending is { } unresolved)
                    {
                        var resolution = ResolvePending(unresolved.Pending, current, now, barSpan);
                        if (resolution.Clear)
                        {
                            // exact confirmation 봉을 놓치면 추후 봉으로 소급 체결하지 않는다.
                            pendingEventIds.Add(unresolved.Pending.EntryEventId);
                            pending = null;
                        }
                    }
                    var prefix = bars.Take(index + 1).ToArray();
                    var liquidity = liquiditySource?.Get(symbol, now, (decimal)current.Close);
                    var build = StructureSnapshotFactory.Create(symbol, market, prefix, daily[symbol], current.Close,
                        now, now, 1, sessionPolicy, liquidity, barSpan);
                    if (build.Snapshot is null || build.LastCompletedBarStart is null ||
                        build.Status != StructureAnalysisStatus.Available) continue;

                    var snapshot = build.Snapshot;
                    var cutoff = build.LastCompletedBarStart.Value;
                    var candidateFive = build.FiveMinuteBars;
                    var built = ZoneBuilder.Build(new ZoneBuildRequest(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, cutoff, build.Bars.Bars, candidateFive, build.DailyBars, previousZones,
                         retired), sessionPolicy);
                    var evaluated = ZoneEvaluator.Evaluate(built.Zones, new ZoneEvaluationRequest(snapshot.SessionStart,
                         cutoff, build.Bars.Bars, previousZones, built.RetiredZoneIds), sessionPolicy);
                    previousZones = evaluated.Zones;
                    retired = evaluated.RetiredZoneIds;
                    var trend = TrendEvaluator.Evaluate(TrendRequest.Create(symbol, snapshot.SessionStart,
                         snapshot.AnalysisAsOf, build.Bars.Bars, build.FiveMinuteBars), sessionPolicy);
                    var gate = StructuralLifecycle.Gate(latch, cutoff, cutoff.Add(barSpan), barSpan,
                         now, sessionPolicy);
                    var detected = SetupDetector.Detect(SetupDetectionRequest.Create(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, snapshot.AnalysisAsOf, now, build.Bars.Bars, evaluated.Zones,
                        evaluated.Episodes, trend, built.Atr1mAtCutoff, snapshot.QuotePrice, snapshot.QuoteAt,
                         snapshot.OptionalLiquidity, build.Quality.BlockersForCandidate.Concat(gate.Blockers).ToImmutableArray()), sessionPolicy);
                    var candidates = StructuralLifecycle.ApplyLive(
                        StructuralLifecycle.ApplyLatch(latch, detected.Candidates, gate.AllowNewTrigger, sessionPolicy,
                            evaluated.Zones), snapshot.QuotePrice, now);
                    foreach (var candidate in candidates)
                    {
                        var reasons = candidate.RejectionCodes
                            .Concat(candidate.Planning.ReasonCodes)
                            .Concat(candidate.Evidence?.GateReasons ?? ImmutableArray<string>.Empty)
                            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
                        var contributions = candidate.Evidence?.FeatureContributions ?? ImmutableArray<string>.Empty;
                        var structuralReady = candidate.Disposition == CandidateDisposition.Ready;
                        var costComplete = candidate.Evidence?.CostComplete == true;
                        var expectedValueReady = candidate.Evidence?.ExpectedNetR is > 0;
                        candidateDiagnostics[candidate.EventId] = new ReplayCandidateDiagnostic(
                            symbol, MarketRules.TradingDate(sessionStart), candidate.EventId,
                            candidate.TriggerBarStart, candidate.Side, candidate.Regime?.Key ?? "UNCOLLECTED",
                            candidate.Disposition, structuralReady,
                            structuralReady && costComplete && expectedValueReady,
                            costComplete, liquiditySource?.IsModeled == true,
                            liquiditySource?.SourceName ?? "MISSING",
                            candidate.Evidence?.ExpectedNetR,
                            reasons, contributions, Forecast: candidate.Evidence?.Forecast,
                            RegimeAssessment: candidate.Evidence?.RegimeAssessment);
                    }
                    var preferred = CandidateSelection.SelectPreferred(candidates);

                    if (!exitedThisPoll && pending is null && preferred is { Disposition: CandidateDisposition.Ready, Plan: not null }
                        && ShouldQueuePending(pendingEventIds, new PendingEntry(preferred.EventId, symbol,
                            preferred.Side, preferred.TriggerBarStart, preferred.TriggerBarStart.Add(barSpan),
                            preferred.ExpiresAt, (double)preferred.Plan.Stop, (double)preferred.Plan.Target,
                            (double)preferred.Plan.EntryReference, preferred.Plan.PlanId, preferred.Plan.PolicyHash)))
                    {
                        var context = StructuralSimulation.Freeze(preferred.Plan, preferred.EventId,
                            trend.State.ToString(), trend.SignedTrend, preferred.EntryQuality,
                            snapshot.AnalysisAsOf, snapshot.QuoteAt);
                        var pendingEntry = new PendingEntry(preferred.EventId, symbol, preferred.Side,
                            preferred.TriggerBarStart, preferred.TriggerBarStart.Add(barSpan), preferred.ExpiresAt,
                            (double)preferred.Plan.Stop, (double)preferred.Plan.Target,
                            (double)preferred.Plan.EntryReference, preferred.Plan.PlanId, preferred.Plan.PolicyHash);
                        pending = new PendingReplayEntry(pendingEntry, context, preferred);
                    }
                    latch = StructuralLifecycle.Commit(latch, cutoff, candidates, evaluated.RetiredZoneIds,
                        StructuralLifecycle.EventSignature(candidates,
                            CandidateSelection.SelectPreferred(candidates)?.EventId), null, consumeOnReady: false);
                }

                result[symbol] = ReplayPendingBars(result[symbol], symbol, bars, processedBars);
                result[symbol] = SimulationEngine.CloseExpiredSessions(result[symbol], sessionEnd);
                daily[symbol].Add(Daily(bars));
            }

        var trades = result.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
        if (liquiditySource?.IsModeled == true)
            trades = trades.ToImmutableDictionary(x => x.Key,
                x => x.Value.Select(ApplyModeledRealizedSpread).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var realized = trades.Values.SelectMany(x => x).Where(x => x.ExitAt is not null && x.PnlPercent is not null &&
                x.Structure?.EntryEventId is not null)
            .GroupBy(x => x.Structure!.EntryEventId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var diagnostics = candidateDiagnostics.Values
            .Select(x => realized.TryGetValue(x.EventId, out var trade) && trade.Structure?.PlanSnapshot.RiskPercent is > 0
                ? x with { RealizedNetR = trade.PnlPercent / trade.Structure.PlanSnapshot.RiskPercent }
                : x)
            .OrderBy(x => x.SessionDate).ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.SignalAt).ThenBy(x => x.EventId, StringComparer.Ordinal).ToImmutableArray();
        return new ReplayRun(trades, diagnostics,
            coverage.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value.Build(x.Key)).ToImmutableArray(),
            SummarizeGates(diagnostics));
    }

    public static ReplayGateSummary SummarizeGates(IEnumerable<ReplayCandidateDiagnostic> diagnostics)
    {
        var rows = diagnostics.ToArray();
        var rejected = rows.Where(x => !x.FinalApproved).ToArray();
        var failures = rejected.ToDictionary(x => x.EventId, FailureReasons, StringComparer.Ordinal);
        var exclusive = rejected.Select(x => failures[x.EventId][0]).GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var overlapping = rejected.SelectMany(x => failures[x.EventId]).GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var gates = overlapping.Keys.Concat(exclusive.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(GateOrder).ThenBy(x => x, StringComparer.Ordinal)
            .Select(x => new ReplayGateCount(x, overlapping.GetValueOrDefault(x), exclusive.GetValueOrDefault(x)))
            .ToImmutableArray();
        return new ReplayGateSummary(rows.Length, rows.Count(x => x.StructuralReady),
            rows.Count(x => x.FinalApproved), rejected.Length,
            rows.Count(x => x.Side == TradeSide.Long), rows.Count(x => x.Side == TradeSide.Short),
            overlapping.Values.Sum() > rejected.Length, GateAttributionOrder, gates);
    }

    static ImmutableArray<string> FailureReasons(ReplayCandidateDiagnostic row)
    {
        var reasons = row.RejectionReasons.ToHashSet(StringComparer.Ordinal);
        if (row.StructuralReady)
        {
            if (!row.CostComplete) reasons.Add(StructuralPlanner.MissingLiquidityCost);
            if (row.ExpectedNetR is not > 0) reasons.Add("EXPECTED_NET_R_NON_POSITIVE");
        }
        if (reasons.Count == 0)
            reasons.Add($"DISPOSITION_{row.Disposition.ToString().ToUpperInvariant()}");
        return reasons.OrderBy(GateOrder).ThenBy(x => x, StringComparer.Ordinal).ToImmutableArray();
    }

    static int GateOrder(string reason) => reason switch
    {
        StructureSnapshotFactory.BlockerInvalidBars or StructureSnapshotFactory.BlockerBarGap or
            StructureSnapshotFactory.BlockerBarConflict => 0,
        TrendEvaluator.BlockerTrendUnavailable => 10,
        TrendEvaluator.BlockerMissing5mStructure => 11,
        EntryQualityEvaluator.ReasonAtrUnavailable => 12,
        EntryQualityEvaluator.ReasonNoVolumeBaseline => 13,
        SetupDetector.BlockerOutsideSession or SetupDetector.BlockerStaleLatestBar or
            SetupDetector.BlockerMissingQuote or SetupDetector.BlockerStaleQuote or
            SetupDetector.BlockerQuoteInFuture or SetupDetector.BlockerAfterEntryCutoff => 20,
        SetupDetector.CodeTrendDirectionOpposesLong or SetupDetector.CodeTrendDirectionOpposesShort or
            SetupDetector.CodeTrendDeeplyOpposesRebound or SetupDetector.CodeTrendDeeplyOpposesShortRebound or
            SetupDetector.CodeTransitionPullbackBlocked or SetupDetector.CodeTransitionBreakoutBlocked => 30,
        StructuralPlanner.NoInvalidationStructure or StructuralPlanner.NoTargetStructure or
            StructuralPlanner.NoTargetRoom or StructuralPlanner.EntryInsideResistance => 40,
        EntryQualityEvaluator.ReasonTrendUnavailable or EntryQualityEvaluator.ReasonMissingRequiredComponent or
            EntryQualityEvaluator.ReasonZeroRequiredComponent => 50,
        StructuralPlanner.InvalidEntryReference or StructuralPlanner.PriceBelowMinimumSupported or
            StructuralPlanner.UnsupportedPriceTick or StructuralPlanner.RiskTooWide or
            StructuralPlanner.StopNotBelowEntry or StructuralPlanner.StopNotAboveEntry or
            StructuralPlanner.StopNotPositive or StructuralPlanner.StopInsideCost or
            StructuralPlanner.StopInsideNoise or StructuralPlanner.CostExceedsRoom or
            StructuralPlanner.InsufficientRewardToRisk or StructuralPlanner.ExcessiveRewardToRisk or
            StructuralPlanner.MissingLiquidityCost or StructuralPlanner.ShortBorrowCostMissing => 60,
        "EXPECTED_NET_R_NON_POSITIVE" or "EXPECTED_VALUE_TRAINED_THRESHOLD" => 70,
        _ => 100
    };

    sealed class CoverageAccumulator
    {
        int _sessions;
        int _expected;
        int _actual;
        readonly HashSet<long> _sourceSpanTicks = [];
        readonly HashSet<long> _replaySpanTicks = [];
        readonly HashSet<string> _enginePaths = new(StringComparer.Ordinal);
        readonly HashSet<string> _limitations = new(StringComparer.Ordinal);

        public void Add(ImmutableArray<Candle> bars, DateTimeOffset sessionStart, DateTimeOffset sessionEnd,
            TimeSpan replaySpan, ReplayTimeframeContract contract)
        {
            _sessions++;
            var sourceSpan = InferSourceSpan(bars);
            _sourceSpanTicks.Add(sourceSpan.Ticks);
            _replaySpanTicks.Add(replaySpan.Ticks);
            _enginePaths.Add(contract.EnginePath);
            _limitations.Add(contract.Limitation);
            var expected = sourceSpan > TimeSpan.Zero ? (int)((sessionEnd - sessionStart).Ticks / sourceSpan.Ticks) : 0;
            var actual = bars.Select(x => x.Timestamp).Distinct()
                .Count(x => x >= sessionStart && x < sessionEnd && x + sourceSpan <= sessionEnd);
            _expected += expected;
            _actual += Math.Min(actual, expected);
        }

        public ReplaySourceCoverage Build(string symbol)
        {
            var minutes = _sourceSpanTicks.Count == 1
                ? TimeSpan.FromTicks(_sourceSpanTicks.Single()).TotalMinutes : 0;
            var replayMinutes = _replaySpanTicks.Count == 1
                ? TimeSpan.FromTicks(_replaySpanTicks.Single()).TotalMinutes : 0;
            var status = _sourceSpanTicks.Count != 1 ? "mixed-unsupported"
                : _enginePaths.SetEquals(["one-minute-v5"]) ? "supported"
                : _enginePaths.SetEquals(["native-five-minute-v1"]) ? "native-supported" : "unsupported";
            var timingStatus = _sourceSpanTicks.SetEquals(_replaySpanTicks) ? "aligned" : "misinferred";
            return new ReplaySourceCoverage(symbol, _sessions, _expected, _actual,
                Math.Max(0, _expected - _actual), _expected == 0 ? 0 : (double)_actual / _expected,
                minutes, status, replayMinutes, timingStatus,
                _enginePaths.Count == 1 ? _enginePaths.Single() : "mixed",
                string.Join(' ', _limitations.Order(StringComparer.Ordinal)));
        }
    }

    static TimeSpan InferSourceSpan(ImmutableArray<Candle> bars)
    {
        var ticks = bars.Zip(bars.Skip(1), (left, right) => (right.Timestamp - left.Timestamp).Ticks)
            .Where(x => x > 0 && x <= TimeSpan.FromMinutes(30).Ticks).ToArray();
        return ticks.Length == 0 ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(ticks.Min());
    }

    static SimTrade ApplyModeledRealizedSpread(SimTrade trade)
    {
        if (trade.PnlPercent is not { } pnl || trade.Structure?.PlanSnapshot is not { } plan || trade.EntryPrice <= 0)
            return trade;
        var borrow = plan.BorrowCostPerShare ?? 0m;
        var spread = Math.Max(0m, plan.ExtraCostPerShare - borrow);
        return spread <= 0 ? trade : trade with
        {
            PnlPercent = Math.Round(pnl - (double)(spread / (decimal)trade.EntryPrice * 100m), 2)
        };
    }

    public static DateTimeOffset EntryTime(DateTimeOffset triggerConfirmedAt, DateTimeOffset analysisAsOf) =>
        triggerConfirmedAt < analysisAsOf ? analysisAsOf : triggerConfirmedAt;

    public static List<SimTrade> ReplayPendingBars(IReadOnlyList<SimTrade> trades, string symbol,
        IReadOnlyList<Candle> bars, int processedBars) =>
        SimulationEngine.ReplayBars(trades, symbol, bars.Skip(Math.Clamp(processedBars, 0, bars.Count)).ToArray());

    static Candle Daily(ImmutableArray<Candle> bars) => new(bars[0].Timestamp, bars[0].Open,
        bars.Max(x => x.High), bars.Min(x => x.Low), bars[^1].Close, bars.Sum(x => x.Volume));
}
