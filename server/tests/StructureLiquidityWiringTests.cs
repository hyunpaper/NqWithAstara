using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 이슈 #41 — 폴링 → 구조 엔진 호가 배선. D6와 같은 결정적 합성 데이터(임의 값이며 실제 종목 추천이 아니다)를
/// 쓰되, 호가가 공급될 때 계획 비용에 실제 spread가 반영되는지와 결측·실패·off에서 종전 동작이 그대로인지를
/// 고정한다. 라이브 외부 API를 쓰지 않고 fake gateway + MovableClock만 쓴다.
/// </summary>
public sealed class StructureLiquidityWiringTests
{
    static readonly StructurePolicy P = D6.PolicyWithoutTheReboundTrendFloor;

    sealed record Harness(MonitorPollingService Poller, RecordingStore Store, MonitorRuntimeState Runtime,
        StructureAnalysisService Structure, MovableClock Clock, CountingOrderBookGateway Books,
        LiquidityQueryService Liquidity, SilentDiagnostics Diagnostics);

    static Harness Build(StructureEngineMode mode, bool wireLiquidity = true, params string[] symbols)
    {
        var clock = new MovableClock(Fx.At(60));
        var store = new RecordingStore();
        foreach (var symbol in symbols.Length == 0 ? [Fx.Symbol] : symbols)
            store.Watch.Add(new WatchItem(symbol, "테스트"));
        var runtime = new MonitorRuntimeState();
        var diagnostics = new SilentDiagnostics();
        var structure = new StructureAnalysisService(store,
            new StructureObservationWriter(new MemoryObservationStore(), P), runtime, clock, diagnostics,
            new StructureEngineOptions(mode), P, new StructuralTradeEntryService(store));
        var books = new CountingOrderBookGateway(clock);
        var liquidity = new LiquidityQueryService(store, books, runtime, clock);
        var gateway = new ScriptedGateway(D6.Session, () => D6.CompletedBars(clock.Now),
            () => (D6.QuotePrice(clock.Now), clock.Now), () => D6.Daily());
        var poller = new MonitorPollingService(store, gateway, new QuietStream(), runtime, clock, diagnostics,
            structure, wireLiquidity ? new StructureLiquidityFeed(liquidity, diagnostics) : null);
        runtime.CommitStart();
        return new Harness(poller, store, runtime, structure, clock, books, liquidity, diagnostics);
    }

    static async Task PollAt(Harness harness, int minute, int seconds = 0)
    {
        harness.Clock.Now = Fx.At(minute).AddSeconds(seconds);
        await harness.Poller.PollAsync(default);
    }

    /// <summary>D6 fixture의 REBOUND 트리거(m64)를 m65 poll이 평가한 뒤의 대표 계획.</summary>
    static async Task<(Harness Harness, StructurePlanDto Plan)> PlannedAsync(StructureEngineMode mode,
        bool wireLiquidity = true, Action<Harness>? arrange = null)
    {
        var harness = Build(mode, wireLiquidity);
        arrange?.Invoke(harness);
        await PollAt(harness, 64);                       // watermark seeding — 신규 트리거 없음(§16B)
        await PollAt(harness, 65);                       // 트리거 봉(m64) 평가 → REBOUND 계획
        Assert.True(harness.Structure.TryGetPublished(Fx.Symbol, out var view));
        var planned = view.Candidates.SingleOrDefault(x => x.Plan is not null);
        Assert.True(planned is not null, Describe(view));
        return (harness, planned!.Plan!);
    }

    static string Describe(StructureAnalysisView view) =>
        $"summary={view.CandidateSummary} warnings=[{string.Join(",", view.Warnings)}] candidates=[" +
        string.Join(" | ", view.Candidates.Select(x => $"{x.Kind}:{x.State}:rej[{string.Join(",", x.RejectionCodes)}]")) + "]";

    /// <summary>
    /// 이슈 #41 본체: 호가가 공급되면 계획의 missingLiquidity=false이고 유효 spread가 비용(extraCost)으로
    /// 그대로 들어간다. 같은 데이터에서 배선이 없으면 종전대로 MISSING_LIQUIDITY_COST다.
    /// </summary>
    [Fact]
    public async Task APolledOrderBookPutsTheRealSpreadIntoThePlanCost()
    {
        var (wired, plan) = await PlannedAsync(StructureEngineMode.Active);

        Assert.False(plan.MissingLiquidity);
        Assert.Equal(0.02m, plan.ValidSpread);                              // ask 99.62 - bid 99.60
        Assert.Equal(0.02m, plan.ExtraCostPerShare);
        Assert.DoesNotContain(StructuralPlanner.MissingLiquidityCost, plan.ReasonCodes);
        Assert.Empty(wired.Diagnostics.Failures);

        // 배선 없는 같은 데이터 = 이슈가 보고한 종전 동작.
        var (_, baseline) = await PlannedAsync(StructureEngineMode.Active, wireLiquidity: false);
        Assert.True(baseline.MissingLiquidity);
        Assert.Null(baseline.ValidSpread);
        Assert.Equal(0m, baseline.ExtraCostPerShare);
        Assert.Contains(StructuralPlanner.MissingLiquidityCost, baseline.ReasonCodes);

        // 비용 정책·공식은 그대로다. spread가 extraCost/frontRun으로 들어가 낙관 편향이 사라질 뿐이다(§9.3).
        Assert.Equal(baseline.EntryReference, plan.EntryReference);
        Assert.Equal(baseline.FeePerShare, plan.FeePerShare);
        Assert.Equal(baseline.EligibilityCostModelVersion, plan.EligibilityCostModelVersion);
        Assert.Equal(baseline.RealizedFillCostModelVersion, plan.RealizedFillCostModelVersion);
        Assert.Equal(0.02m, plan.FrontRunBuffer);                           // frontRun = max(floor, spread)
        Assert.Equal(plan.Target - plan.EntryReference - plan.FeePerShare - plan.ExtraCostPerShare, plan.NetReward);
        Assert.Equal(plan.EntryReference - plan.Stop + plan.FeePerShare + plan.ExtraCostPerShare, plan.NetRisk);
        Assert.True(plan.NetReward < baseline.NetReward, $"netReward {plan.NetReward} < {baseline.NetReward}");
        Assert.True(plan.NetRisk > baseline.NetRisk, $"netRisk {plan.NetRisk} > {baseline.NetRisk}");
        Assert.True(plan.NetR < baseline.NetR, $"netR {plan.NetR} < {baseline.NetR}");
    }

    /// <summary>active 진입도 같은 계획을 쓴다 — 동결된 거래가 spread 반영 계획에서 나온다.</summary>
    [Fact]
    public async Task TheEnteredTradeFreezesTheSpreadAwarePlan()
    {
        var (harness, plan) = await PlannedAsync(StructureEngineMode.Active);

        var trade = Assert.Single(harness.Store.Trades);
        Assert.Equal(plan.PlanId, trade.Structure!.PlanSnapshot.PlanId);
        Assert.Equal((double)plan.Stop, trade.Stop, 10);
        Assert.Equal((double)plan.Target, trade.Target, 10);
    }

    /// <summary>shadow도 같은 호가를 보지만 진입은 v4 소유 그대로다(모드 게이트 무변경).</summary>
    [Fact]
    public async Task ShadowSeesTheSpreadButStillDoesNotEnter()
    {
        var (harness, plan) = await PlannedAsync(StructureEngineMode.Shadow);

        Assert.False(plan.MissingLiquidity);
        Assert.Equal(0.02m, plan.ValidSpread);
        Assert.Empty(harness.Store.Trades);
        Assert.Equal(StructureAnalysisService.EntryOwnerV4, harness.Structure.EntryOwner);
    }

    /// <summary>
    /// 조회 실패·교차 호가·지연 호가는 전부 종전 결측 경로로 떨어진다. 추정 spread를 만들지 않고
    /// 예외가 폴링 밖으로 새지 않으며 v4 신호도 그대로 남는다(§16 v5 격리).
    /// </summary>
    [Theory]
    [InlineData("http")]        // Toss 호가 조회 실패 (LiquidityQueryService가 삼키는 예외)
    [InlineData("unexpected")]  // 예상 밖 예외 (feed가 삼켜야 한다)
    [InlineData("crossed")]     // 교차 호가 → invalid
    [InlineData("stale")]       // 30초 초과 지연 호가 → invalid
    [InlineData("nonusd")]      // 비USD 호가 → invalid
    public async Task AnUnusableOrderBookKeepsTheExistingMissingPath(string mode)
    {
        var (harness, plan) = await PlannedAsync(StructureEngineMode.Active, arrange: h =>
        {
            switch (mode)
            {
                case "http": h.Books.Failure = new HttpRequestException("Toss 5xx"); break;
                case "unexpected": h.Books.Failure = new ArgumentException("계약 위반"); break;
                case "crossed": h.Books.Bid = 99.70m; h.Books.Ask = 99.60m; break;
                case "stale": h.Books.Age = TimeSpan.FromSeconds(45); break;
                case "nonusd": h.Books.Currency = "KRW"; break;
            }
        });

        Assert.True(plan.MissingLiquidity);
        Assert.Null(plan.ValidSpread);
        Assert.Equal(0m, plan.ExtraCostPerShare);
        Assert.Contains(StructuralPlanner.MissingLiquidityCost, plan.ReasonCodes);
        Assert.Single(harness.Store.Trades);                                 // v5 진입·v4 신호 경로는 그대로
        Assert.True(harness.Poller.TryGet(Fx.Symbol, out _));
        // 예상 밖 예외만 진단에 남고, 그마저도 v4/v5 경로를 중단시키지 않는다.
        Assert.All(harness.Diagnostics.Failures,
            x => Assert.Equal(StructureLiquidityFeed.DiagnosticsOperation, x.Scope));
        Assert.Equal(mode == "unexpected", harness.Diagnostics.Failures.Count > 0);
    }

    /// <summary>
    /// 폴링 1회는 종목당 호가를 한 번만 조회한다. 캐시를 공유하므로 같은 시각의 `/api/liquidity` 요청도
    /// 새 외부 호출을 만들지 않는다(§9.1 "전체 관심종목에 5초 호가 조회를 추가하지 않는다").
    /// </summary>
    [Fact]
    public async Task OnePollFetchesEachSymbolsOrderBookAtMostOnce()
    {
        var harness = Build(StructureEngineMode.Active, true, Fx.Symbol, "OTHER");
        await PollAt(harness, 64);

        Assert.Equal(2, harness.Books.Calls);
        Assert.Equal(1, harness.Books.Symbols.Count(x => x == Fx.Symbol));
        Assert.Equal(1, harness.Books.Symbols.Count(x => x == "OTHER"));

        // 같은 시각의 조회 API는 캐시를 그대로 재사용한다 — 폴링이 호출량을 두 배로 만들지 않는다.
        var (status, response) = await harness.Liquidity.GetAsync(Fx.Symbol, default);
        Assert.Equal(200, status);
        Assert.Equal(StructureLiquidityFeed.StatusReady, response!.Status);
        Assert.Equal(2, harness.Books.Calls);
    }

    /// <summary>off는 계산도 호가 조회도 유발하지 않는다(§16B 모드 게이트).</summary>
    [Fact]
    public async Task OffModeNeverTouchesTheOrderBook()
    {
        var harness = Build(StructureEngineMode.Off);
        await PollAt(harness, 64);
        await PollAt(harness, 65);

        Assert.Equal(0, harness.Books.Calls);
        Assert.False(harness.Structure.TryGetPublished(Fx.Symbol, out _));
        Assert.True(harness.Poller.TryGet(Fx.Symbol, out _));                // v4는 정상 동작
    }

    /// <summary>정규장 외·모니터링 중지에서는 조회 자체가 없고 응답은 결측이다(없는 값을 만들지 않는다).</summary>
    [Fact]
    public async Task OutsideTheRegularSessionTheFeedStaysMissingWithoutAnyCall()
    {
        var harness = Build(StructureEngineMode.Active);
        var feed = new StructureLiquidityFeed(harness.Liquidity, harness.Diagnostics);
        harness.Clock.Now = Fx.SessionEnd.AddMinutes(5);

        Assert.Null(await feed.TryGetAsync(Fx.Symbol, default));
        Assert.Equal(0, harness.Books.Calls);

        // 관심종목이 아니면(404) 조회하지 않는다.
        harness.Clock.Now = Fx.At(60);
        Assert.Null(await feed.TryGetAsync("NOPE", default));
        Assert.Equal(0, harness.Books.Calls);
    }

    /// <summary>ready가 아닌 응답은 어떤 값도 구조 입력으로 옮기지 않는다(변환 지점의 단일 규칙).</summary>
    [Theory]
    [InlineData("stopped")]
    [InlineData("marketClosed")]
    [InlineData("invalid")]
    [InlineData("unavailable")]
    public void OnlyReadyResponsesBecomeStructureLiquidity(string status)
    {
        var summary = new LiquiditySummary(99.60m, 99.62m, 0.02m, 2.01m, 400, 300, 14.3m);
        Assert.Null(StructureLiquidityFeed.Map(new LiquidityResponse(Fx.Symbol, status, summary, Fx.At(60),
            Fx.At(60), "test", "test", null)));

        var ready = StructureLiquidityFeed.Map(new LiquidityResponse(Fx.Symbol, "ready", summary, Fx.At(60),
            Fx.At(60), "test", "test", null));
        Assert.Equal(new StructureLiquidity(99.60m, 99.62m, Fx.At(60), 400, 300), ready);

        // ready여도 값이 비어 있으면 결측이다.
        Assert.Null(StructureLiquidityFeed.Map(new LiquidityResponse(Fx.Symbol, "ready", null, Fx.At(60),
            Fx.At(60), "test", "test", null)));
        Assert.Null(StructureLiquidityFeed.Map(new LiquidityResponse(Fx.Symbol, "ready", summary, null,
            Fx.At(60), "test", "test", null)));
    }
}

/// <summary>호가 조회 횟수를 세는 fake gateway. 폴링이 종목당 한 번만 조회하는 것을 증명한다.</summary>
sealed class CountingOrderBookGateway(MovableClock clock) : IOrderBookGateway
{
    public int Calls;
    public List<string> Symbols { get; } = [];
    public Exception? Failure { get; set; }
    public decimal Bid { get; set; } = 99.60m;
    public decimal Ask { get; set; } = 99.62m;
    public TimeSpan Age { get; set; } = TimeSpan.Zero;
    public string Currency { get; set; } = "USD";

    public Task<OrderBookSnapshot> OrderBook(string symbol, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        lock (Symbols) Symbols.Add(symbol);
        if (Failure is { } failure) return Task.FromException<OrderBookSnapshot>(failure);
        return Task.FromResult(new OrderBookSnapshot(clock.Now - Age, Currency,
            [new OrderBookLevel(Ask, 300)], [new OrderBookLevel(Bid, 400)]));
    }
}
