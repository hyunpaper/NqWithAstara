using System.Collections.Concurrent;
using Astra.Server.Domain;
namespace Astra.Server.Application;

public sealed class MonitorPollingService(ILocalStore store, IMarketDataGateway toss, IRealtimeMarketStream stream,
    MonitorRuntimeState runtime, TimeProvider clock, IMonitorDiagnostics diagnostics,
    StructureAnalysisService? structure = null, StructureLiquidityFeed? liquidity = null,
    StructureAlertPublisher? alerts = null, SymbolMetadataService? metadata = null,
    FeeRateCheckService? feeCheck = null, TradeTapeFallbackService? tradeTape = null) : IMonitorSignals
{
    public bool Running => runtime.Snapshot().Running; public long Generation => runtime.Snapshot().Generation;
    public string ConnectionStatus => runtime.Snapshot().ConnectionStatus; public string ConnectionMessage => runtime.Snapshot().ConnectionMessage;
    public MarketSession Market => runtime.Snapshot().Market; public DateTimeOffset UpdatedAt => runtime.Snapshot().UpdatedAt;
    public ConcurrentDictionary<string, SignalView> Signals { get; } = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<Candle> Data)> _daily = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, SetupLatch> _setups = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, BreakoutLatch> _breakouts = new(StringComparer.OrdinalIgnoreCase);
    public bool TryGet(string symbol, out SignalView signal) => Signals.TryGetValue(symbol, out signal!);
    // 이슈 #67: 일봉 캐시도 다른 종목 캐시와 같은 규칙으로 정리한다 — 삭제된 종목·세션 경계를 넘겨 재사용하지 않는다.
    public void Remove(string symbol) { Signals.TryRemove(symbol, out _); _setups.TryRemove(symbol, out _); _breakouts.TryRemove(symbol, out _); _daily.TryRemove(symbol, out _); structure?.Remove(symbol); metadata?.Remove(symbol); }
    public void Clear() { Signals.Clear(); _setups.Clear(); _breakouts.Clear(); _daily.Clear(); structure?.Clear(); alerts?.Clear(); metadata?.Clear(); }

    public async Task PollAsync(CancellationToken ct)
    {
        var gen = runtime.Snapshot().Generation;
        try
        {
            var market = await toss.Session(clock.GetLocalNow(), ct); if (!runtime.TryCommit(gen, s => s with { Market = market })) return;
            if (!market.IsOpen || market.End is { } end && clock.GetLocalNow() >= end)
            {
                await ReconcileExpiredTrades(gen, ct);
                runtime.TryCommit(gen, () => { Signals.Clear(); _setups.Clear(); _breakouts.Clear(); _daily.Clear(); structure?.Clear(); alerts?.Clear(); metadata?.Clear(); });
                runtime.TryCommit(gen, s => s with { ConnectionStatus = "connected", ConnectionMessage = "미국 정규장 외에는 신호를 생성하지 않습니다.", UpdatedAt = clock.GetUtcNow() }); return;
            }
            // 이슈 #130: 폴링 서비스가 세션 진입을 감지하는 지점 — 세션당 1회 수수료 정합을 확인한다.
            if (feeCheck is not null && market.Start is { } sessionStart) await feeCheck.CheckOnSessionEntryAsync(sessionStart, ct);
            var watch = await store.Read("watchlist.json", new List<WatchItem>()); var oldTrades = await store.Read("simtrades.json", new List<SimTrade>()); if (!runtime.IsCurrent(gen)) return;
            // Reconcile any previous-session records after giving their final bars a chance
            // to produce a deterministic stop/target outcome.
            if (oldTrades.Any(x => SimulationEngine.IsExpired(x, clock.GetUtcNow())))
            {
                await ReconcileExpiredTrades(gen, ct);
                oldTrades = await store.Read("simtrades.json", new List<SimTrade>());
            }
            var watched = watch.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
            var items = watch.Concat(oldTrades.Where(x => x.Status == "OPEN" && !watched.ContainsKey(x.Symbol)).Select(x => new WatchItem(x.Symbol, x.Symbol)))
                .DistinctBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase).ToArray();
            // #132: 종목 메타는 신규 심볼이 생길 때만 배치 1회 조회한다(STOCK 5/s). 실패는 결측으로 둔다.
            if (metadata is not null) await metadata.EnsureAsync(items.Select(x => x.Symbol), ct);
            var ok = 0; var warmup = 0; var invalid = 0; var failed = 0;
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
            {
                var outcome = await ProcessSymbolAsync(item, watched.ContainsKey(item.Symbol), market, gen, token);
                if (outcome == PollOutcome.Ok) Interlocked.Increment(ref ok);
                else if (outcome == PollOutcome.Warmup) Interlocked.Increment(ref warmup);
                else if (outcome == PollOutcome.Invalid) Interlocked.Increment(ref invalid);
                else if (outcome == PollOutcome.Failed) Interlocked.Increment(ref failed);
            });
            CommitConnection(gen, items.Length, ok, warmup, invalid, failed);
        }
        catch (Exception ex) { runtime.TryCommit(gen, () => Signals.Clear()); runtime.TryCommit(gen, s => s with { ConnectionStatus = "error", ConnectionMessage = ex.Message, UpdatedAt = clock.GetUtcNow() }); diagnostics.PollFailed("market", ex); }
    }

    async Task<PollOutcome> ProcessSymbolAsync(WatchItem item, bool watched, MarketSession market, long gen, CancellationToken token)
    {
        try
        {
            var all = await toss.Candles(item.Symbol, token); var quote = await toss.Price(item.Symbol, token); var now = clock.GetLocalNow();
            var bars = MarketRules.CompletedRegularBars(all, market, quote.At); var age = now - quote.At;
            var validQuote = double.IsFinite(quote.Price) && quote.Price > 0 && age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromMinutes(3) && market.Start <= quote.At && quote.At < market.End && now < market.End;
            if (!runtime.IsCurrent(gen)) return PollOutcome.Ignored;
            if (!validQuote || bars.Any(x => !ValidBar(x))) { runtime.TryCommit(gen, () => Signals.TryRemove(item.Symbol, out _)); return PollOutcome.Invalid; }
            if (bars.Length < 30 || quote.At - bars[^1].Timestamp > TimeSpan.FromMinutes(3))
            {
                if (!await UpdateTrades(gen, t => SimulationEngine.Process(t, item.Symbol, bars, quote.Price, quote.At, 50, 0, []), token)) return PollOutcome.Ignored;
                if (!watched) return PollOutcome.Ok;
                runtime.TryCommit(gen, () => Signals.TryRemove(item.Symbol, out _)); return PollOutcome.Warmup;
            }

            // ws 틱이 끊겼으면 REST 체결 내역으로 체결강도를 보정한다(이슈 #133). 표시·보정 전용이다.
            if (tradeTape is not null) await tradeTape.RefreshAsync(item.Symbol, token);
            var result = Indicators.Evaluate(bars); var score = result.Score; var reasons = result.Reasons; double? buyShare = null;
            if (stream.Flow(item.Symbol, quote.At - TimeSpan.FromMinutes(5), quote.At) is { } flow && flow.Buy + flow.Sell > 0) { buyShare = Math.Round((double)(flow.Buy / (flow.Buy + flow.Sell)) * 100, 1); if (buyShare >= 60) { score = Math.Min(100, score + 5); reasons = [.. reasons, $"체결강도 매수 우위 ({buyShare:0}%)"]; } else if (buyShare <= 40) { score = Math.Max(0, score - 5); reasons = [.. reasons, $"체결강도 매도 우위 ({buyShare:0}%)"]; } }
            if (!watched)
            {
                return await UpdateTrades(gen, t => SimulationEngine.Process(t, item.Symbol, bars, quote.Price, quote.At, score, result.Indicators.Vwap, []), token) ? PollOutcome.Ok : PollOutcome.Ignored;
            }
            var action = score >= 70 ? "BUY" : score <= 30 ? "SELL" : "WATCH";
            IReadOnlyList<Candle>? daily = null; if (_daily.TryGetValue(item.Symbol, out var dh) && clock.GetUtcNow() - dh.At < TimeSpan.FromMinutes(30)) daily = dh.Data; else try { daily = await toss.DailyCandles(item.Symbol, token); _daily[item.Symbol] = (clock.GetUtcNow(), daily); } catch (HttpRequestException) { }
            var decisionNow = clock.GetLocalNow();
            if (!runtime.IsCurrent(gen) || decisionNow >= market.End || decisionNow - quote.At > TimeSpan.FromMinutes(3)) return PollOutcome.Ignored;
            var levels = PriceLevels.Compute(bars, daily, result.Indicators.Atr); Position? p;
            using (await runtime.EnterControlAsync(token)) { if (!runtime.IsCurrent(gen)) return PollOutcome.Ignored; p = await store.Update("positions.json", new Dictionary<string, Position>(), d => { d.TryGetValue(item.Symbol, out var saved); if (saved is { Target: null }) { saved = MarketRules.FreezeRisk(saved, result.Indicators.Atr, levels); d[item.Symbol] = saved; } return (d, saved); }); }
            object? pos = p is null ? null : new { p.EntryPrice, p.Quantity, p.Target, p.Stop, p.TargetBasis, p.StopBasis, pnlPercent = Math.Round((quote.Price / p.EntryPrice - 1) * 100 - MarketRules.RoundTripFeePercent, 2), status = p.Stop.HasValue && quote.Price <= p.Stop ? "STOP" : p.Target.HasValue && quote.Price >= p.Target ? "TARGET" : "HOLD" };
            var bar = bars[^1]; var nearClose = market.End - decisionNow < TimeSpan.FromMinutes(40); _setups.TryGetValue(item.Symbol, out var os);
            var st = SignalLifecycle.UpdateSetup(os, Indicators.DetectSetup(bars, result.Indicators, score), bar, quote.Price, score, result.Indicators.Vwap, clock.GetUtcNow(), market.Start, !nearClose);
            var priorLevels = PriceLevels.Compute(bars[..^1], daily, result.Indicators.Atr); var ext = (bar.Close - result.Indicators.Vwap) / Math.Max(result.Indicators.VwapSd, result.Indicators.Atr > 0 ? result.Indicators.Atr : 1e-9);
            var hit = PriceLevels.DetectBreakout(bars, priorLevels, result.Indicators.RelativeVolume); if (hit is not null && (score < 55 || ext < 0 || ext > 1.5 || buyShare is null or < 50)) hit = null;
            _breakouts.TryGetValue(item.Symbol, out var ob); var bt = SignalLifecycle.UpdateBreakout(ob, hit, bar, quote.Price, clock.GetUtcNow(), market.Start, !nearClose);
            var kinds = new List<string>(); if (st.Emit && st.Display is "SETUP" or "REBOUND") kinds.Add(st.Display); if (bt.Emit) kinds.Add("BREAKOUT");
            // §18 active: 신규 시뮬 진입은 v5 구조 계획이 소유하므로 v4 진입 생성만 중단한다(진입은 아래
            // ObserveStructureAsync → StructureAnalysisService가 커밋). v4 신호 표시·기존 OPEN 청산은 그대로이고,
            // v5 오류/UNAVAILABLE이어도 v4 진입으로 자동 fallback하지 않는다(§16B).
            var v4OwnsNewEntries = structure is null || structure.Mode != StructureEngineMode.Active;
            var entries = !v4OwnsNewEntries
                ? Array.Empty<SimulationEntry>()
                : kinds.Select(k => { var plan = PriceLevels.Enter(quote.Price, 1, result.Indicators.Atr, levels); return new SimulationEntry(k, quote.Price, plan.Target ?? quote.Price, plan.Stop ?? quote.Price, plan.TargetBasis, plan.StopBasis, score, Math.Round(ext, 2), Math.Round(result.Indicators.RelativeVolume, 2), buyShare, Math.Round(result.Indicators.Rsi, 1), reasons, clock.GetUtcNow(), market.End, bar.Timestamp); }).ToArray();
            // 이슈 #106: 이 poll에서 종결된 거래가 있으면 같은 틱이 v5 신규 진입가가 되지 않도록 아래로 전달한다.
            var exited = false;
            if (!await UpdateTrades(gen, t => { var next = SimulationEngine.Process(t, item.Symbol, bars, quote.Price, quote.At, score, result.Indicators.Vwap, entries); exited = ClosedInThisPoll(t, next, item.Symbol); return next; }, token,
                    () => clock.GetLocalNow() < market.End && clock.GetLocalNow() - quote.At <= TimeSpan.FromMinutes(3) && (entries.Length == 0 || market.End - clock.GetLocalNow() >= TimeSpan.FromMinutes(40)))) return PollOutcome.Ignored;
            if (!runtime.TryCommit(gen, () =>
            {
                if (st.State is null) _setups.TryRemove(item.Symbol, out _); else _setups[item.Symbol] = st.State;
                if (bt.State is null) _breakouts.TryRemove(item.Symbol, out _); else _breakouts[item.Symbol] = bt.State;
            })) return PollOutcome.Ignored;
            var ind = new { rsi = Math.Round(result.Indicators.Rsi, 2), emaFast = Math.Round(result.Indicators.EmaFast, 4), emaSlow = Math.Round(result.Indicators.EmaSlow, 4), vwap = Math.Round(result.Indicators.Vwap, 4), atr = Math.Round(result.Indicators.Atr, 4), relativeVolume = Math.Round(result.Indicators.RelativeVolume, 2) }; var es = Indicators.EmaSeries(bars.Select(x => x.Close).ToArray(), 9); var vs = Indicators.VwapSeries(bars); var skip = Math.Max(0, bars.Length - 60); var chart = bars.Skip(skip).Select((x, i) => (object)new { time = x.Timestamp, close = x.Close, ema = Math.Round(es[skip + i], 4), vwap = Math.Round(vs[skip + i], 4) }).ToArray();
            var view = new SignalView(item.Symbol, item.Name, quote.Price, Math.Round((quote.Price / bars[0].Open - 1) * 100, 2), score, action, reasons, quote.At, false, ind, chart, pos, result.Indicators.Atr, st.Display, st.DisplayAt, bt.Display, bt.DisplayAt); runtime.TryCommit(gen, () => Signals[item.Symbol] = view);
            // ── v5 구조 엔진(설계 §12): v4 결과·저장은 위에서 이미 확정됐다. 아래는 별도 경로이며 v4 값을 읽지도 바꾸지도 않는다.
            // off는 계산을 유발하지 않고, shadow는 관측만 한다. 오류는 종목 단위로 격리해 v4 신호를 훼손하지 않는다(§16).
            await ObserveStructureAsync(item.Symbol, all, daily, quote, market, gen, exited, token);
            return PollOutcome.Ok;
        }
        catch (Exception ex) { diagnostics.MarketDataFailed(item.Symbol, "poll", ex); runtime.TryCommit(gen, () => Signals.TryRemove(item.Symbol, out _)); return PollOutcome.Failed; }
    }

    /// <summary>
    /// v4/v5 파이프라인의 유일한 접점(설계 §2, §12.2). 봉·시세는 v4가 이미 쓴 원본 응답을 그대로 재사용하고,
    /// 호가만 <see cref="StructureLiquidityFeed"/>(= LiquidityQueryService 캐시·in-flight 중복 제거)를 통해
    /// 받아 넘긴다(이슈 #41). 호가가 없으면 null로 넘겨 기존 결측 경로를 그대로 태운다.
    /// v5 계산·저장·호가 조회 실패는 v4 신호·거래·알림에 영향을 주지 않도록 종목 단위로 격리한다.
    /// </summary>
    async Task ObserveStructureAsync(string symbol, IReadOnlyList<Candle> bars, IReadOnlyList<Candle>? daily,
        (double Price, DateTimeOffset At) quote, MarketSession market, long gen, bool exitedThisPoll,
        CancellationToken ct)
    {
        // off는 계산도 조회도 유발하지 않는다(§16B 모드 게이트) — 호가 조회는 이 줄 아래에서만 일어난다.
        if (structure is null || structure.Mode == StructureEngineMode.Off) return;
        try
        {
            // 미배선 시절과 동일하게 동작하도록 feed가 없으면 결측(null)이다. 추정 spread를 만들지 않는다.
            var book = liquidity is null ? null : await liquidity.TryGetAsync(symbol, ct);
            await structure.ObserveAsync(new StructureObservationRequest(symbol, gen, market, bars, daily,
                quote.Price, quote.At, book, exitedThisPoll), ct);
        }
        catch (Exception ex) { diagnostics.MarketDataFailed(symbol, "structure-v5", ex); }
    }

    void CommitConnection(long gen, int count, int ok, int warmup, int invalid, int failed)
    {
        var warming = warmup > 0 && invalid == 0 && failed == 0;
        var message = count == 0 ? "관심종목을 추가하세요." : ok > 0 ? "Toss Open API 연결됨" : warming
            ? "지표 워밍업 중 · 완료된 정규장 1분봉 30개가 모이면 신호를 계산합니다."
            : invalid > 0 ? "시세가 오래되었거나 유효하지 않습니다." : "모든 종목 데이터 조회에 실패했습니다.";
        runtime.TryCommit(gen, s => s with { ConnectionStatus = count == 0 || ok > 0 || warming ? "connected" : "error", ConnectionMessage = message, UpdatedAt = clock.GetUtcNow() });
    }
    async Task<bool> UpdateTrades(long gen, Func<List<SimTrade>, List<SimTrade>> update, CancellationToken ct, Func<bool>? stillValid = null)
    {
        using (await runtime.EnterControlAsync(ct))
        {
            if (!runtime.IsCurrent(gen) || stillValid is not null && !stillValid()) return false;
            await store.Update("simtrades.json", new List<SimTrade>(), t => (update(t), true));
            return runtime.IsCurrent(gen);
        }
    }
    async Task ReconcileExpiredTrades(long gen, CancellationToken ct)
    {
        var trades = await store.Read("simtrades.json", new List<SimTrade>());
        var symbols = trades.Where(x => SimulationEngine.IsExpired(x, clock.GetUtcNow()))
            .Select(x => x.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fetched = new ConcurrentDictionary<string, Candle[]>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(symbols, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (symbol, token) =>
        {
            if (!runtime.IsCurrent(gen)) return;
            try
            {
                fetched[symbol] = (await toss.Candles(symbol, token)).Where(ValidBar).OrderBy(x => x.Timestamp).ToArray();
            }
            catch (Exception ex) { diagnostics.MarketDataFailed(symbol, "final-bars", ex); }
        });
        await UpdateTrades(gen, current =>
        {
            foreach (var (symbol, bars) in fetched) current = SimulationEngine.ReplayBars(current, symbol, bars);
            return SimulationEngine.CloseExpiredSessions(current, clock.GetUtcNow());
        }, ct);
    }
    // 이슈 #106: 이 poll에서 OPEN이던 이 심볼의 거래가 종결됐는지. 봉 replay·틱 청산 어느 경로든 종결이면 참이다.
    static bool ClosedInThisPoll(IReadOnlyList<SimTrade> before, IReadOnlyList<SimTrade> after, string symbol)
    {
        var open = before.Where(x => x.Status == "OPEN" && x.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return open.Count > 0 && after.Any(x => x.Status != "OPEN" && open.Contains(x.Id));
    }
    static bool ValidBar(Candle x) => double.IsFinite(x.Open) && double.IsFinite(x.High) && double.IsFinite(x.Low) && double.IsFinite(x.Close) && double.IsFinite(x.Volume) && x.Open > 0 && x.High > 0 && x.Low > 0 && x.Close > 0 && x.Volume >= 0 && x.High >= Math.Max(x.Open, x.Close) && x.Low <= Math.Min(x.Open, x.Close) && x.High >= x.Low;
    enum PollOutcome { Ignored, Ok, Warmup, Invalid, Failed }
}
