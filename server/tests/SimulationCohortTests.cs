using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Xunit;

/// <summary>
/// 이슈 #27 — v5 시뮬 코호트 집계(Domain 순수 함수) 검증.
/// 원천은 FrozenStructureContext뿐이며(§10/§11), 컨텍스트·품질이 없으면 추정하지 않고 미수집으로 남긴다.
/// fixture 축: v4-only / v5-only / 혼합 / 빈 데이터 / OPEN-only / 컨텍스트 누락.
/// </summary>
public sealed class SimulationCohortTests
{
    static FrozenPlanSnapshot Plan(string kind = "PULLBACK", decimal netR = 1.6m, bool missingLiquidity = false,
        string engine = "v5-structure.1", string policyHash = "hash-A") =>
        new("plan-1", kind, 100m, 99.6m, 99.4m, 101.5m, "z-support", 99.4m, 99.7m, "z-resist", 101.5m, 101.9m,
            .05m, "session-atr", .02m, 1.3m, .8m, netR, .6, .1m, .02m, missingLiquidity ? null : .04m,
            missingLiquidity, "cost-eligibility.1", "cost-fill.1", Fx.At(40), Fx.At(45), engine, policyHash,
            missingLiquidity ? ["MISSING_LIQUIDITY_COST"] : [], "지지 반응 후 저항 하단 앞 계획");

    static FrozenStructureContext Context(double? quality = 60, string trend = "UP", string kind = "PULLBACK",
        bool missingLiquidity = false, decimal netR = 1.6m, string engine = "v5-structure.1",
        string policyHash = "hash-A", string eventId = "TEST|event") =>
        new(eventId, Plan(kind, netR, missingLiquidity, engine, policyHash), trend, trend == "UNKNOWN" ? null : 40.0,
            quality, Fx.At(40), Fx.At(40), StructuralSimulation.ExitPolicyVersion);

    static SimTrade V5(string id, string status = "TARGET", double? pnl = 1.2, FrozenStructureContext? context = null,
        bool contextMissing = false, bool? exitEstimated = null) =>
        new(id, Fx.Symbol, "PULLBACK", Fx.At(40), 100, 101.5, 99.4, null, null, status,
            status == "OPEN" ? null : 101.5, status == "OPEN" ? null : Fx.At(50), status == "OPEN" ? null : pnl,
            101.5, Logic: "v5-structure.1", ExitEstimated: exitEstimated,
            Structure: contextMissing ? null : context ?? Context(eventId: "TEST|" + id));

    static SimTrade V4(string id, string status = "TARGET", double? pnl = 1.8) =>
        new(id, Fx.Symbol, "SETUP", Fx.At(10), 100, 102, 99, null, null, status,
            status == "OPEN" ? null : 102, status == "OPEN" ? null : Fx.At(20), status == "OPEN" ? null : pnl,
            102, Score: 80, Logic: "v4");

    static SimulationCohortGroup GroupOf(StructureCohortReport report, string dimension) =>
        Assert.Single(report.Groups, g => g.Dimension == dimension);

    /// <summary>빈 데이터: 표본 없음은 0%/0.0이 아니라 null(미표시)이어야 한다.</summary>
    [Fact]
    public void EmptyTradesProduceAnEmptyReportWithoutFakeZeros()
    {
        var report = SimulationCohorts.Build([]);
        Assert.Equal(0, report.V5Stats.Total);
        Assert.Equal(0, report.ContextMissing);
        Assert.Null(report.V5Stats.WinRate);
        Assert.Null(report.V5Stats.AvgPnl);
        Assert.Null(report.V5Stats.AvgPlannedNetR);
        Assert.Equal(0, report.V5Stats.PlannedNetRSamples);
        Assert.All(report.Groups, g => Assert.Empty(g.Cohorts));
        Assert.Equal(11, report.Groups.Count);
    }

    /// <summary>v4-only: v4/legacy 거래는 구조 코호트에 들어가지 않는다(기존 ByVersion이 담당).</summary>
    [Fact]
    public void V4OnlyTradesNeverEnterTheStructureCohorts()
    {
        var report = SimulationCohorts.Build([V4("a"), V4("b", "OPEN"), V4("c", "STOP", -1.1)]);
        Assert.Equal(0, report.V5Stats.Total);
        Assert.Equal(0, report.ContextMissing);
        Assert.All(report.Groups, g => Assert.Empty(g.Cohorts));
    }

    /// <summary>
    /// v5-only: 승률 분모는 손익 유효 청산 건이며, 손익 결측·추정 청산은 따로 센다.
    /// 계획 netR 평균은 동결 계획에서만 오고 실현 손익과 섞이지 않는다.
    /// </summary>
    [Fact]
    public void V5StatsUseValidClosedAsTheWinRateDenominator()
    {
        var report = SimulationCohorts.Build([
            V5("w1", "TARGET", 1.2),
            V5("l1", "STOP", -0.9),
            V5("m1", "EOD", pnl: null),                       // 손익 결측 → 분모 제외
            V5("e1", "STOP", -0.5, exitEstimated: true),
            V5("o1", "OPEN")
        ]);
        var s = report.V5Stats;
        Assert.Equal(5, s.Total);
        Assert.Equal(1, s.Open);
        Assert.Equal(4, s.Closed);
        Assert.Equal(3, s.ValidClosed);
        Assert.Equal(1, s.Wins);
        Assert.Equal(Math.Round(100d / 3, 1), s.WinRate);
        Assert.Equal(Math.Round((1.2 - .9 - .5) / 3, 2), s.AvgPnl);
        Assert.Equal(1, s.MissingPnl);
        Assert.Equal(1, s.EstimatedExits);
        Assert.Equal(1.6, s.AvgPlannedNetR);                  // 전 거래 동일 계획 netR
        Assert.Equal(5, s.PlannedNetRSamples);                // OPEN에도 계획은 존재한다
    }

    /// <summary>품질 구간: EntryQuality는 중립 구간으로 나뉘고, null은 구간이 아니라 미수집이다.</summary>
    [Fact]
    public void EntryQualityBandsSeparateUncollectedFromLowScores()
    {
        var report = SimulationCohorts.Build([
            V5("q1", context: Context(quality: 10, eventId: "TEST|q1")),
            V5("q2", context: Context(quality: 30, eventId: "TEST|q2")),
            V5("q3", context: Context(quality: 74.9, eventId: "TEST|q3")),
            V5("q4", context: Context(quality: 75, eventId: "TEST|q4")),
            V5("q5", context: Context(quality: null, eventId: "TEST|q5"))
        ]);
        var group = GroupOf(report, "entryQuality");
        Assert.Equal(new[] { "Q0_25", "Q25_50", "Q50_75", "Q75_100", SimulationCohorts.QualityUncollectedKey },
            group.Cohorts.Select(x => x.Key));
        var uncollected = group.Cohorts.Single(x => x.Key == SimulationCohorts.QualityUncollectedKey);
        Assert.False(uncollected.Collected);                  // 0점 구간(Q0_25)과 절대 합치지 않는다
        Assert.True(group.Cohorts.Single(x => x.Key == "Q0_25").Collected);
        Assert.Equal(report.V5Stats.Total, group.Cohorts.Sum(x => x.Stats.Total));
    }

    /// <summary>데이터 품질: MISSING_LIQUIDITY_COST 집단은 정상 비용 집단과 분리 집계된다.</summary>
    [Fact]
    public void MissingLiquidityCostTradesFormTheirOwnCohort()
    {
        var report = SimulationCohorts.Build([
            V5("n1", context: Context(eventId: "TEST|n1")),
            V5("n2", "STOP", -0.8, Context(eventId: "TEST|n2")),
            V5("x1", context: Context(missingLiquidity: true, eventId: "TEST|x1"))
        ]);
        var group = GroupOf(report, "dataQuality");
        Assert.Equal(2, group.Cohorts.Count);
        Assert.Equal(2, group.Cohorts.Single(x => x.Key == SimulationCohorts.CostOkKey).Stats.Total);
        var missing = group.Cohorts.Single(x => x.Key == SimulationCohorts.MissingLiquidityKey);
        Assert.Equal(1, missing.Stats.Total);
        Assert.True(missing.Collected);                       // 수집은 됐고, 비용 산정이 결측인 코호트다
    }

    /// <summary>추세·셋업·엔진 버전·정책 해시·청산/비용 모델 차원이 동결 값 그대로 갈라진다.</summary>
    [Fact]
    public void FrozenTrendSetupAndVersionDimensionsSliceByStoredValuesOnly()
    {
        var report = SimulationCohorts.Build([
            V5("t1", context: Context(trend: "UP", kind: "PULLBACK", eventId: "TEST|t1")),
            V5("t2", context: Context(trend: "DOWN", kind: "REBOUND", engine: "v5-structure.2",
                policyHash: "hash-B", eventId: "TEST|t2")),
            V5("t3", context: Context(trend: "UNKNOWN", kind: "BREAKOUT", eventId: "TEST|t3"))
        ]);
        Assert.Equal(new[] { "UP", "DOWN", "UNKNOWN" },
            GroupOf(report, "trend").Cohorts.Select(x => x.Key));
        Assert.Equal("판정 불가", GroupOf(report, "trend").Cohorts.Single(x => x.Key == "UNKNOWN").Label);
        Assert.Equal(new[] { "BREAKOUT", "PULLBACK", "REBOUND" },
            GroupOf(report, "setup").Cohorts.Select(x => x.Key).Order());
        Assert.Equal(new[] { "v5-structure.1", "v5-structure.2" },
            GroupOf(report, "engineVersion").Cohorts.Select(x => x.Key).Order());
        Assert.Equal(new[] { "hash-A", "hash-B" },
            GroupOf(report, "policyHash").Cohorts.Select(x => x.Key).Order());
        Assert.Equal(StructuralSimulation.ExitPolicyVersion,
            Assert.Single(GroupOf(report, "exitPolicy").Cohorts).Key);
        Assert.Equal("cost-eligibility.1 / cost-fill.1",
            Assert.Single(GroupOf(report, "costModel").Cohorts).Key);
    }

    /// <summary>혼합 + 집계 일치: 모든 차원에서 코호트 합계 = v5 전체이고 v4는 어디에도 섞이지 않는다.</summary>
    [Fact]
    public void EveryDimensionSumsBackToTheV5Total()
    {
        var report = SimulationCohorts.Build([
            V4("v4a"), V4("v4b", "OPEN"),
            V5("a"), V5("b", "OPEN"), V5("c", "STOP", -1.0, Context(quality: null, trend: "RANGE",
                kind: "REBOUND", missingLiquidity: true, eventId: "TEST|c")),
            V5("d", contextMissing: true)
        ]);
        Assert.Equal(4, report.V5Stats.Total);
        Assert.All(report.Groups, g => Assert.Equal(4, g.Cohorts.Sum(x => x.Stats.Total)));
        Assert.All(report.Groups, g => Assert.Equal(1, g.Cohorts.Sum(x => x.Stats.Open)));
    }

    /// <summary>OPEN-only: 청산 0건이면 승률·평균 손익은 null이고(0% 아님) 계획 netR만 표본을 가진다.</summary>
    [Fact]
    public void OpenOnlyTradesReportNoWinRateInsteadOfZero()
    {
        var report = SimulationCohorts.Build([V5("o1", "OPEN"), V5("o2", "OPEN",
            context: Context(quality: 80, eventId: "TEST|o2"))]);
        var s = report.V5Stats;
        Assert.Equal(2, s.Open);
        Assert.Equal(0, s.Closed);
        Assert.Null(s.WinRate);
        Assert.Null(s.AvgPnl);
        Assert.Equal(2, s.PlannedNetRSamples);
        Assert.NotNull(s.AvgPlannedNetR);
    }

    /// <summary>
    /// 컨텍스트 누락: v5 거래인데 동결 컨텍스트가 없으면 현재 재계산 값으로 채우지 않고
    /// 모든 차원에서 CONTEXT_MISSING 코호트(미수집)로만 나타난다.
    /// </summary>
    [Fact]
    public void AMissingFrozenContextIsUncollectedInEveryDimension()
    {
        var report = SimulationCohorts.Build([V5("ok"), V5("broken", contextMissing: true)]);
        Assert.Equal(1, report.ContextMissing);
        Assert.All(report.Groups, g =>
        {
            var missing = g.Cohorts.Single(x => x.Key == SimulationCohorts.ContextMissingKey);
            Assert.False(missing.Collected);
            Assert.Equal(1, missing.Stats.Total);
            Assert.Equal(0, missing.Stats.PlannedNetRSamples); // 계획 netR도 추정하지 않는다
            Assert.Null(missing.Stats.AvgPlannedNetR);
            Assert.Equal(SimulationCohorts.ContextMissingKey, g.Cohorts[^1].Key); // 항상 마지막에 배치
        });
    }

    /// <summary>
    /// Application 조회: /api/sim 확장이 additive인지 — 기존 summary/byVersion/trades 의미는 그대로 두고
    /// structure 섹션이 camelCase로 추가된다(기존 응답 호환).
    /// </summary>
    [Fact]
    public async Task SimReportAddsTheStructureSectionWithoutChangingExistingFields()
    {
        var store = new MemoryStore();
        await store.Write("simtrades.json", new List<SimTrade> { V4("v4a"), V5("v5a") });
        var report = await new SimulationReportQueryService(store, TimeProvider.System).GetAsync();

        Assert.Equal(2, report.Summary.Total);                       // 기존 전체 요약은 v4+v5 그대로
        Assert.Equal(new[] { "v4", "v5-structure.1" }, report.ByVersion.Select(x => x.Version).Order());
        Assert.Equal(1, report.Structure.V5Stats.Total);             // 신규 섹션은 v5만
        Assert.Contains(report.Trades, t => t.Structure is not null);

        var json = JsonSerializer.SerializeToElement(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.TryGetProperty("summary", out _));
        Assert.True(json.TryGetProperty("byVersion", out _));
        Assert.True(json.TryGetProperty("trades", out _));
        var structure = json.GetProperty("structure");
        Assert.True(structure.TryGetProperty("v5Stats", out _));
        Assert.True(structure.TryGetProperty("groups", out _));
        Assert.Equal(0, structure.GetProperty("contextMissing").GetInt32());
    }

    sealed class MemoryStore : ILocalStore
    {
        readonly Dictionary<string, object> _values = [];
        public Task<T> Read<T>(string file, T fallback) =>
            Task.FromResult(_values.TryGetValue(file, out var value) ? (T)value : fallback);
        public Task Write<T>(string file, T data) { _values[file] = data!; return Task.CompletedTask; }
        public Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
        {
            var next = change(_values.TryGetValue(file, out var value) ? (T)value : fallback);
            _values[file] = next.Data!;
            return Task.FromResult(next.Result);
        }
    }
}
