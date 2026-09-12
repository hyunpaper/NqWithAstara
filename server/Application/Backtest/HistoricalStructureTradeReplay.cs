using System.Collections.Immutable;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

public sealed class HistoricalStructureTradeReplay(IBarStore store, StructurePolicy policy)
{
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

                for (var index = 0; index < bars.Length && bars[index].Timestamp.AddMinutes(1) < sessionEnd; index++)
                {
                    var current = bars[index];
                    result[symbol] = SimulationEngine.ReplayBars(result[symbol], symbol, [current]);
                    var now = current.Timestamp.AddMinutes(1);
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
                    var detected = SetupDetector.Detect(SetupDetectionRequest.Create(symbol, snapshot.SessionStart,
                        snapshot.SessionEnd, snapshot.AnalysisAsOf, now, build.Bars.Bars, evaluated.Zones,
                        evaluated.Episodes, trend, built.Atr1mAtCutoff, snapshot.QuotePrice, snapshot.QuoteAt,
                        null, build.Quality.BlockersForCandidate), policy);

                    foreach (var candidate in detected.Candidates.Where(x =>
                                 x.Disposition == CandidateDisposition.Ready && x.Plan is not null))
                    {
                        var context = StructuralSimulation.Freeze(candidate.Plan!, candidate.EventId,
                            trend.State.ToString(), trend.SignedTrend, candidate.EntryQuality,
                            snapshot.AnalysisAsOf, snapshot.QuoteAt);
                        var entered = StructuralSimulation.Enter(result[symbol], new StructuralEntryRequest(symbol,
                            candidate.TriggerBarStart, candidate.TriggerConfirmedAt, snapshot.SessionEnd, context,
                            build.Bars.Bars.Select(x => x.Start).ToArray(), snapshot.SessionStart,
                            candidate.Plan!.TargetZoneSnapshot.Aliases), policy);
                        result[symbol] = entered.Trades;
                    }
                }

                result[symbol] = SimulationEngine.ReplayBars(result[symbol], symbol, bars);
                result[symbol] = SimulationEngine.CloseExpiredSessions(result[symbol], sessionEnd);
                daily[symbol].Add(Daily(bars));
            }

        return result.ToImmutableDictionary(x => x.Key, x => x.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    static Candle Daily(ImmutableArray<Candle> bars) => new(bars[0].Timestamp, bars[0].Open,
        bars.Max(x => x.High), bars.Min(x => x.Low), bars[^1].Close, bars.Sum(x => x.Volume));
}
