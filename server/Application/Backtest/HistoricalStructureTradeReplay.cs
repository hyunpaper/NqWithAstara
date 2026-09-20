using System.Collections.Immutable;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

public sealed class HistoricalStructureTradeReplay(IBarStore store, StructurePolicy policy,
    IHistoricalLiquiditySource? liquiditySource = null)
{
    /// <summary>replay에서 생성된 후보의 1단계 후보화·2단계 최종 게이트 결과를 보존한다.</summary>
    public sealed record ReplayCandidateDiagnostic(string Symbol, DateOnly SessionDate,
        string EventId, DateTimeOffset SignalAt, TradeSide Side, string Regime,
        CandidateDisposition Disposition, bool StructuralReady, bool FinalApproved,
        bool CostComplete, bool CostModeled, string CostSource,
        ImmutableArray<string> RejectionReasons, ImmutableArray<string> FeatureContributions);

    public sealed record ReplayRun(ImmutableDictionary<string, ImmutableArray<SimTrade>> Trades,
        ImmutableArray<ReplayCandidateDiagnostic> Candidates);

    public sealed record ReplayPendingResolution(bool Clear, EntryConfirmation? Confirmation, string Reason);

    /// <summary>운영 replay loop와 테스트가 공유하는 pending 수명주기 판정이다.</summary>
    public static ReplayPendingResolution ResolvePending(PendingEntry pending, Candle current, DateTimeOffset observedAt)
    {
        if (current.Timestamp == pending.ConfirmationBarStart)
        {
            var confirmation = PendingEntryPolicy.Confirm(pending, current, observedAt,
                current.Close, "REPLAY_CONFIRMATION_BAR_CLOSE");
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

        foreach (var day in days)
            foreach (var symbol in symbols)
            {
                ct.ThrowIfCancellationRequested();
                var bars = ConfluenceReplay.Parse(await store.ReadLinesAsync(day, symbol, ct))
                    .Select(x => new Candle(x.Start, (double)x.Open, (double)x.High, (double)x.Low,
                        (double)x.Close, (double)x.Volume)).ToImmutableArray();
                if (bars.Length == 0) continue;
                var barSpan = bars.Length > 1 ? bars[1].Timestamp - bars[0].Timestamp : TimeSpan.FromMinutes(1);
                if (barSpan <= TimeSpan.Zero || barSpan > TimeSpan.FromMinutes(30)) barSpan = TimeSpan.FromMinutes(1);
                var sessionStart = bars[0].Timestamp;
                var sessionEnd = sessionStart.AddHours(6.5);
                var market = new MarketSession(true, "과거 replay", null, sessionStart, sessionEnd);
                var previousZones = ImmutableArray<PriceZone>.Empty;
                var retired = ImmutableArray<string>.Empty;
                var latch = StructuralLatch.Empty(symbol, sessionStart, replayPolicy.PolicyHash);
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
                        var resolution = ResolvePending(queued.Pending, current, now);
                        if (resolution.Confirmation is { Decision: PendingEntryDecision.Confirmed } confirmation)
                        {
                            var entered = StructuralSimulation.Enter(result[symbol], new StructuralEntryRequest(symbol,
                                queued.Candidate.TriggerBarStart, now, sessionEnd, queued.Context,
                                completedStarts, sessionStart, queued.Candidate.Plan?.TargetZoneSnapshot.Aliases, confirmation,
                                RequireCompleteLiquidityCost: replayPolicy.RequireCompleteLiquidityCost), replayPolicy);
                            result[symbol] = entered.Trades;
                        }
                        pendingEventIds.Add(queued.Pending.EntryEventId);
                        pending = null;
                    }
                    else if (pending is { } unresolved)
                    {
                        var resolution = ResolvePending(unresolved.Pending, current, now);
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
                        now, now, 1, replayPolicy, liquidity, barSpan);
                    if (build.Snapshot is null || build.LastCompletedBarStart is null ||
                        build.Status != StructureAnalysisStatus.Available) continue;

                    var snapshot = build.Snapshot;
                    var cutoff = build.LastCompletedBarStart.Value;
                    var candidateFive = BarAggregator.Aggregate(build.Bars.Bars, snapshot.SessionStart, cutoff, replayPolicy);
                    var built = ZoneBuilder.Build(new ZoneBuildRequest(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, cutoff, build.Bars.Bars, candidateFive, build.DailyBars, previousZones,
                        retired), replayPolicy);
                    var evaluated = ZoneEvaluator.Evaluate(built.Zones, new ZoneEvaluationRequest(snapshot.SessionStart,
                        cutoff, build.Bars.Bars, previousZones, built.RetiredZoneIds), replayPolicy);
                    previousZones = evaluated.Zones;
                    retired = evaluated.RetiredZoneIds;
                    var trend = TrendEvaluator.Evaluate(TrendRequest.Create(symbol, snapshot.SessionStart,
                        snapshot.AnalysisAsOf, build.Bars.Bars, build.FiveMinuteBars), replayPolicy);
                    var gate = StructuralLifecycle.Gate(latch, cutoff, cutoff.Add(barSpan), barSpan,
                        now, replayPolicy);
                    var detected = SetupDetector.Detect(SetupDetectionRequest.Create(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, snapshot.AnalysisAsOf, now, build.Bars.Bars, evaluated.Zones,
                        evaluated.Episodes, trend, built.Atr1mAtCutoff, snapshot.QuotePrice, snapshot.QuoteAt,
                        snapshot.OptionalLiquidity, build.Quality.BlockersForCandidate.Concat(gate.Blockers).ToImmutableArray()), replayPolicy);
                    var candidates = StructuralLifecycle.ApplyLive(
                        StructuralLifecycle.ApplyLatch(latch, detected.Candidates, gate.AllowNewTrigger, replayPolicy,
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
                            reasons, contributions);
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
        var diagnostics = candidateDiagnostics.Values
            .OrderBy(x => x.SessionDate).ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.SignalAt).ThenBy(x => x.EventId, StringComparer.Ordinal).ToImmutableArray();
        if (liquiditySource?.IsModeled == true)
            trades = trades.ToImmutableDictionary(x => x.Key,
                x => x.Value.Select(ApplyModeledRealizedSpread).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        return new ReplayRun(trades, diagnostics);
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
