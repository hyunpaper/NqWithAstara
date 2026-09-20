using System.Collections.Immutable;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

public sealed record HistoricalReplayCostProfile(decimal AssumedSpreadPerShare, double BidSize, double AskSize)
{
    public static readonly HistoricalReplayCostProfile Conservative = new(.05m, 1d, 1d);

    public StructureLiquidity Liquidity(decimal reference, DateTimeOffset at) => new(
        reference - AssumedSpreadPerShare / 2m, reference + AssumedSpreadPerShare / 2m, at, BidSize, AskSize);
}

public sealed class HistoricalStructureTradeReplay(IBarStore store, StructurePolicy policy,
    HistoricalReplayCostProfile? costProfile = null)
{
    readonly HistoricalReplayCostProfile _costProfile = costProfile ?? HistoricalReplayCostProfile.Conservative;
    public async Task<ImmutableDictionary<string, ImmutableArray<SimTrade>>> RunAsync(DateOnly from, DateOnly to,
        IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var days = (await store.ListDaysAsync(ct)).Where(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", out var day)
            && day >= from && day <= to).Order().ToArray();
        var result = symbols.ToImmutableDictionary(x => x, _ => new List<SimTrade>(),
            StringComparer.OrdinalIgnoreCase).ToBuilder();
        var daily = symbols.ToDictionary(x => x, _ => new List<Candle>(), StringComparer.OrdinalIgnoreCase);

        foreach (var day in days)
            foreach (var symbol in symbols)
            {
                ct.ThrowIfCancellationRequested();
                var bars = ConfluenceReplay.Parse(await store.ReadLinesAsync(day, symbol, ct))
                    .Select(x => new Candle(x.Start, (double)x.Open, (double)x.High, (double)x.Low,
                        (double)x.Close, (double)x.Volume)).ToImmutableArray();
                if (bars.Length == 0) continue;
                var sessionStart = bars[0].Timestamp;
                var sessionEnd = sessionStart.AddHours(6.5);
                var market = new MarketSession(true, "과거 replay", null, sessionStart, sessionEnd);
                var previousZones = ImmutableArray<PriceZone>.Empty;
                var retired = ImmutableArray<string>.Empty;
                var latch = StructuralLatch.Empty(symbol, sessionStart, policy.PolicyHash);
                var processedBars = 0;
                EntryCandidate? pending = null;

                for (var index = 0; index < bars.Length && bars[index].Timestamp.AddMinutes(1) < sessionEnd; index++)
                {
                    var current = bars[index];
                    var openBeforeBar = result[symbol].Count(x => x.Status == "OPEN");
                    result[symbol] = SimulationEngine.ReplayBars(result[symbol], symbol, [current]);
                    var exitedThisPoll = result[symbol].Count(x => x.Status == "OPEN") < openBeforeBar;
                    processedBars = index + 1;
                    var now = current.Timestamp.AddMinutes(1);

                    if (pending is { Plan: { } pendingPlan } && !exitedThisPoll && current.Open > (double)pendingPlan.Stop)
                    {
                        var context = StructuralSimulation.Freeze(pendingPlan, pending.EventId,
                            "REPLAY_CONFIRMATION", null, pending.EntryQuality, now, now);
                        var entered = StructuralSimulation.Enter(result[symbol], new StructuralEntryRequest(symbol,
                            pending.TriggerBarStart, now, sessionEnd, context,
                            bars.Take(index + 1).Select(x => x.Timestamp).ToArray(), sessionStart,
                            pendingPlan.TargetZoneSnapshot.Aliases), policy);
                        result[symbol] = entered.Trades;
                    }
                    pending = null;
                    var prefix = bars.Take(index + 1).ToArray();
                    var build = StructureSnapshotFactory.Create(symbol, market, prefix, daily[symbol], current.Close,
                        now, now, 1, policy);
                    if (build.Snapshot is null || build.LastCompletedBarStart is null ||
                        build.Status != StructureAnalysisStatus.Available) continue;

                    var snapshot = build.Snapshot;
                    var cutoff = build.LastCompletedBarStart.Value;
                    var candidateFive = BarAggregator.Aggregate(build.Bars.Bars, snapshot.SessionStart, cutoff, policy);
                    var built = ZoneBuilder.Build(new ZoneBuildRequest(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, cutoff, build.Bars.Bars, candidateFive, build.DailyBars, previousZones,
                        retired), policy);
                    var evaluated = ZoneEvaluator.Evaluate(built.Zones, new ZoneEvaluationRequest(snapshot.SessionStart,
                        cutoff, build.Bars.Bars, previousZones, built.RetiredZoneIds), policy);
                    previousZones = evaluated.Zones;
                    retired = evaluated.RetiredZoneIds;
                    var trend = TrendEvaluator.Evaluate(TrendRequest.Create(symbol, snapshot.SessionStart,
                        snapshot.AnalysisAsOf, build.Bars.Bars, build.FiveMinuteBars), policy);
                    var gate = StructuralLifecycle.Gate(latch, cutoff, cutoff.AddMinutes(1), TimeSpan.FromMinutes(1),
                        now, policy);
                    var detected = SetupDetector.Detect(SetupDetectionRequest.Create(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, snapshot.AnalysisAsOf, now, build.Bars.Bars, evaluated.Zones,
                        evaluated.Episodes, trend, built.Atr1mAtCutoff, snapshot.QuotePrice, snapshot.QuoteAt,
                        _costProfile.Liquidity(snapshot.QuotePrice ?? (decimal)current.Close, now),
                        build.Quality.BlockersForCandidate.Concat(gate.Blockers).ToImmutableArray()), policy);
                    var candidates = StructuralLifecycle.ApplyLive(
                        StructuralLifecycle.ApplyLatch(latch, detected.Candidates, gate.AllowNewTrigger, policy,
                            evaluated.Zones), snapshot.QuotePrice, now);
                    var preferred = CandidateSelection.SelectPreferred(candidates);

                    if (!exitedThisPoll && preferred is { Disposition: CandidateDisposition.Ready, Plan: not null })
                    {
                        pending = preferred;
                    }
                    latch = StructuralLifecycle.Commit(latch, cutoff, candidates, evaluated.RetiredZoneIds,
                        StructuralLifecycle.EventSignature(candidates,
                            CandidateSelection.SelectPreferred(candidates)?.EventId), null, consumeOnReady: false);
                }

                result[symbol] = ReplayPendingBars(result[symbol], symbol, bars, processedBars);
                result[symbol] = SimulationEngine.CloseExpiredSessions(result[symbol], sessionEnd);
                daily[symbol].Add(Daily(bars));
            }

        return result.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public static DateTimeOffset EntryTime(DateTimeOffset triggerConfirmedAt, DateTimeOffset analysisAsOf) =>
        triggerConfirmedAt < analysisAsOf ? analysisAsOf : triggerConfirmedAt;

    public static List<SimTrade> ReplayPendingBars(IReadOnlyList<SimTrade> trades, string symbol,
        IReadOnlyList<Candle> bars, int processedBars) =>
        SimulationEngine.ReplayBars(trades, symbol, bars.Skip(Math.Clamp(processedBars, 0, bars.Count)).ToArray());

    static Candle Daily(ImmutableArray<Candle> bars) => new(bars[0].Timestamp, bars[0].Open,
        bars.Max(x => x.High), bars.Min(x => x.Low), bars[^1].Close, bars.Sum(x => x.Volume));
}
