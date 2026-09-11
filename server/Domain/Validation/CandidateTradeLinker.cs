using Astra.Server;

namespace Astra.Server.Domain.Validation;

// 이슈 #28 — 후보 → 결정 → 거래 → 결과 체인을 만드는 순수 Domain 링커.
// 시계·저장소·HTTP에 의존하지 않으며 평가 시각(AsOf)은 항상 호출자가 명시한다(§4, ArchitectureBoundaryTests).
//
// 연결 키: (Symbol, SessionStart, EventId, EngineVersion, PolicyHash, Mode).
//   - SessionStart는 관측에서만 온다. SimTrade는 세션 시작을 저장하지 않으므로 달력으로 추측하지 않는다.
//   - Mode(shadow/active)는 실행 성격이 다른 별개 집단이라 키에 포함한다.
// 시간 규율: AsOf 이후에 기록된 관측·진입은 아예 입력에서 제외하고, AsOf 이후 청산은 "아직 모름"으로 절단한다.

public static class CandidateTradeLinker
{
    public static LinkResult Link(IReadOnlyList<ObservationRow> observations, IReadOnlyList<SimTrade> trades,
        LinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(trades);
        ArgumentNullException.ThrowIfNull(options);

        var conflicts = new SortedSet<string>(StringComparer.Ordinal);
        var limitations = new SortedSet<string>(StringComparer.Ordinal);

        // ── 1) 관측 행 선별: 미래 행 제외, ObservationId 중복 제외, 선택적 버전/모드 필터 ──────────────
        var afterCutoff = 0;
        var duplicateIds = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ObservationRow>(observations.Count);
        foreach (var row in observations)
        {
            if (row is null) continue;
            if (row.ObservedAt > options.AsOf || row.AnalysisAsOf > options.AsOf) { afterCutoff++; continue; }
            if (options.EngineVersion is { } engine && !Same(row.EngineVersion, engine)) continue;
            if (options.PolicyHash is { } hash && !Same(row.PolicyHash, hash)) continue;
            if (options.Mode is { } mode && !Same(row.Mode, mode)) continue;
            if (!seenIds.Add(row.ObservationId))
            {
                // 재시작·재실행으로 같은 줄이 두 번 append된 경우다. 첫 줄만 쓰고 사실을 남긴다(§16).
                duplicateIds++;
                conflicts.Add($"{LinkCodes.DuplicateObservationId}:{row.ObservationId}");
                continue;
            }
            rows.Add(row);
        }

        // ── 2) 후보 행 펼치기 + 이벤트 단위 묶기(반복 poll dedup) ──────────────────────────────────
        var candidateRows = 0;
        var groups = new Dictionary<string, EventGroup>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var candidate in row.Candidates ?? [])
            {
                if (candidate is null || string.IsNullOrEmpty(candidate.EventId)) continue;
                candidateRows++;
                // 관측 시각보다 뒤에 확정된 트리거는 성립하지 않는다. 기록을 고치지 않고 충돌로 남긴다.
                if (candidate.TriggerConfirmedAt > row.ObservedAt)
                    conflicts.Add($"{LinkCodes.ObservationTimeOrder}:{row.ObservationId}");
                var key = candidate.EventId;
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = new EventGroup(key);
                group.Add(row, candidate);
            }
        }

        // ── 3) 버전/모드/종목이 섞인 이벤트는 평가에서 제외한다(버전 혼합 방지) ──────────────────────
        var dropped = 0;
        var kept = new List<EventGroup>(groups.Count);
        foreach (var group in groups.Values)
        {
            var mixed = false;
            if (group.Versions.Count > 1) { conflicts.Add($"{LinkCodes.EventVersionMixed}:{group.EventId}"); mixed = true; }
            if (group.Modes.Count > 1) { conflicts.Add($"{LinkCodes.EventModeMixed}:{group.EventId}"); mixed = true; }
            if (group.Symbols.Count > 1) { conflicts.Add($"{LinkCodes.EventSymbolMixed}:{group.EventId}"); mixed = true; }
            if (mixed) { dropped++; continue; }
            kept.Add(group);
        }

        // ── 4) 거래 선별과 연결 ────────────────────────────────────────────────────────────────
        var v5 = trades.Where(x => x is not null && StructuralSimulation.OwnsTrade(x)).ToArray();
        var tradesAfterCutoff = v5.Count(x => x.EnteredAt > options.AsOf);
        var usable = v5.Where(x => x.EnteredAt <= options.AsOf && x.Structure is not null).ToArray();
        var byEvent = new Dictionary<string, List<SimTrade>>(StringComparer.Ordinal);
        foreach (var trade in usable)
        {
            var eventId = trade.Structure!.EntryEventId;
            if (string.IsNullOrEmpty(eventId)) continue;
            if (!byEvent.TryGetValue(eventId, out var list)) byEvent[eventId] = list = [];
            list.Add(trade);
        }

        var linkedTradeIds = new HashSet<string>(StringComparer.Ordinal);
        var censored = 0;
        var summaryOnly = 0;
        var results = new List<LinkedCandidate>(kept.Count);
        foreach (var group in kept)
        {
            var final = group.Final;
            var row = group.FinalRow;
            LinkedTrade? trade = null;

            if (byEvent.TryGetValue(group.EventId, out var matches))
            {
                // 가장 먼저 진입한 거래 하나만 본다. 같은 이벤트의 재시도는 StructuralSimulation이 이미 막는다.
                var candidateTrade = matches.OrderBy(x => x.EnteredAt).ThenBy(x => x.Id, StringComparer.Ordinal).First();
                var plan = candidateTrade.Structure!.PlanSnapshot;
                if (!candidateTrade.Symbol.Equals(row.Symbol, StringComparison.OrdinalIgnoreCase))
                    conflicts.Add($"{LinkCodes.TradeSymbolMismatch}:{candidateTrade.Id}");
                else if (!Same(plan.EngineVersion, row.EngineVersion) || !Same(plan.PolicyHash, row.PolicyHash))
                    conflicts.Add($"{LinkCodes.TradeVersionMismatch}:{candidateTrade.Id}");
                else
                {
                    trade = Project(candidateTrade, options.AsOf);
                    if (trade.StoredStatus != "OPEN" && !trade.OutcomeKnown) censored++;
                    linkedTradeIds.Add(candidateTrade.Id);
                }
            }

            if (group.SummaryOnly) summaryOnly++;

            results.Add(new LinkedCandidate(group.EventId, row.Symbol, MarketRules.TradingDate(row.SessionStart),
                row.SessionStart, row.Mode, row.EngineVersion, row.PolicyHash, final.Kind, row.TrendState,
                Finite(final.EntryQuality), Outcome(final.State), final.State ?? string.Empty,
                group.FirstObservedAt, group.LastObservedAt, group.ObservationCount,
                Math.Max(group.ObservationCount - 1, 0), group.SummaryOnly,
                Codes(final.RejectionCodes), Primary(final.RejectionCodes), final.MissingLiquidityCost,
                final.ValidSpread, LiquidityClass.Of(final.MissingLiquidityCost, final.ValidSpread),
                final.EligibilityCostModelVersion, final.RealizedFillCostModelVersion,
                Codes(row.Warnings), trade));
        }

        results.Sort((a, b) =>
        {
            var bySession = a.SessionStart.CompareTo(b.SessionStart);
            if (bySession != 0) return bySession;
            var bySymbol = string.CompareOrdinal(a.Symbol, b.Symbol);
            return bySymbol != 0 ? bySymbol : string.CompareOrdinal(a.EventId, b.EventId);
        });

        var unlinked = usable.Where(x => !linkedTradeIds.Contains(x.Id))
            .OrderBy(x => x.EnteredAt).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => Project(x, options.AsOf))
            .ToArray();
        foreach (var trade in unlinked) conflicts.Add($"{LinkCodes.TradeWithoutObservation}:{trade.TradeId}");

        // ── 5) 한계 목록 — 표본이 좋아 보여도 이 한계는 사라지지 않는다 ───────────────────────────
        limitations.Add(LinkCodes.TradeRetentionCapped);
        limitations.Add(LinkCodes.ObservationRetentionCapped);
        limitations.Add(LinkCodes.SubBarStateNotRetained);
        if (results.Any(x => x.MissingLiquidityCost is null))
            limitations.Add(LinkCodes.CostAssumptionOnlyForPlannedCandidates);
        if (results.Any(x => x.Outcome is CandidateOutcome.Rejected or CandidateOutcome.Invalidated))
            limitations.Add(LinkCodes.RejectedPathNotRetained);
        if (summaryOnly > 0) limitations.Add(LinkCodes.SummaryObservationOmitsEvidence);

        var sessions = results.Select(x => x.TradingDate).Distinct().Order().ToArray();
        var audit = new LinkAudit(observations.Count, afterCutoff, duplicateIds, candidateRows,
            candidateRows - groups.Count, results.Count, dropped, summaryOnly,
            results.Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count(), sessions.Length,
            sessions.Length == 0 ? null : sessions[0], sessions.Length == 0 ? null : sessions[^1],
            trades.Count, v5.Length, linkedTradeIds.Count, unlinked.Length, tradesAfterCutoff, censored,
            conflicts.ToArray(), limitations.ToArray());

        return new LinkResult(results, unlinked, audit);
    }

    /// <summary>
    /// 저장된 거래를 평가 시각 기준으로 투영한다. AsOf 이후 청산은 결과를 아직 모르는 것이므로
    /// 손익을 지우고 상태를 OPEN으로 본다 — 미래 값이 코호트 평균에 들어가지 않게 하는 지점이다.
    /// 원본 <see cref="SimTrade"/>는 바꾸지 않으며 저장된 상태는 <see cref="LinkedTrade.StoredStatus"/>에 남는다.
    /// </summary>
    static LinkedTrade Project(SimTrade trade, DateTimeOffset asOf)
    {
        var context = trade.Structure!;
        var plan = context.PlanSnapshot;
        var resolved = trade.Status != "OPEN" && trade.ExitAt is { } exit && exit <= asOf;
        var known = resolved && trade.PnlPercent is { } pnl && double.IsFinite(pnl);
        return new LinkedTrade(trade.Id, trade.Symbol, resolved ? trade.Status : "OPEN", trade.Status,
            trade.EnteredAt, resolved ? trade.ExitAt : null, known ? trade.PnlPercent : null, known,
            resolved ? trade.ExitEstimated : null, plan.MissingLiquidity, plan.ValidSpread, plan.NetR,
            plan.EligibilityCostModelVersion, plan.RealizedFillCostModelVersion, plan.EngineVersion,
            plan.PolicyHash, plan.Kind, context.TrendAtEntry, Finite(context.EntryQualityAtEntry),
            context.EntryEventId);
    }

    static CandidateOutcome Outcome(string? state) => (state ?? string.Empty).ToUpperInvariant() switch
    {
        "WAIT" => CandidateOutcome.Wait,
        "READY" => CandidateOutcome.Ready,
        "REJECTED" => CandidateOutcome.Rejected,
        "INVALIDATED" => CandidateOutcome.Invalidated,
        "EXPIRED" => CandidateOutcome.Expired,
        "ENTERED" => CandidateOutcome.Entered,
        _ => CandidateOutcome.Unknown
    };

    /// <summary>대표 거절 사유. 사람이 고르지 않고 ordinal 최소값으로 고정해 재현 가능하게 만든다.</summary>
    static string? Primary(IReadOnlyList<string>? codes) =>
        codes is null || codes.Count == 0 ? null : codes.Where(x => !string.IsNullOrEmpty(x))
            .Order(StringComparer.Ordinal).FirstOrDefault();

    static IReadOnlyList<string> Codes(IReadOnlyList<string>? codes) =>
        codes is null ? [] : codes.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();

    static double? Finite(double? value) => value is { } x && double.IsFinite(x) ? x : null;

    static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// 한 EventId의 관측 묶음. 같은 이벤트를 15초마다 다시 본 행은 여기서 하나로 접히고
    /// 최종 상태는 가장 나중 관측(AnalysisAsOf → ObservedAt → ObservationId 순)에서 가져온다.
    /// </summary>
    sealed class EventGroup(string eventId)
    {
        ObservationRow? _finalRow;
        ObservationCandidateRow? _final;

        public string EventId { get; } = eventId;
        public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Modes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ObservationCount { get; private set; }
        public bool SummaryOnly { get; private set; } = true;
        public DateTimeOffset FirstObservedAt { get; private set; } = DateTimeOffset.MaxValue;
        public DateTimeOffset LastObservedAt { get; private set; } = DateTimeOffset.MinValue;
        public ObservationRow FinalRow => _finalRow!;
        public ObservationCandidateRow Final => _final!;

        public void Add(ObservationRow row, ObservationCandidateRow candidate)
        {
            ObservationCount++;
            Versions.Add(row.EngineVersion + "|" + row.PolicyHash);
            Modes.Add(row.Mode ?? string.Empty);
            Symbols.Add(row.Symbol);
            if (string.Equals(row.Detail, "full", StringComparison.Ordinal)) SummaryOnly = false;
            if (row.ObservedAt < FirstObservedAt) FirstObservedAt = row.ObservedAt;
            if (row.ObservedAt > LastObservedAt) LastObservedAt = row.ObservedAt;
            if (_finalRow is null || Newer(row)) { _finalRow = row; _final = candidate; }
        }

        bool Newer(ObservationRow row)
        {
            var current = _finalRow!;
            if (row.AnalysisAsOf != current.AnalysisAsOf) return row.AnalysisAsOf > current.AnalysisAsOf;
            if (row.ObservedAt != current.ObservedAt) return row.ObservedAt > current.ObservedAt;
            return string.CompareOrdinal(row.ObservationId, current.ObservationId) > 0;
        }
    }
}
