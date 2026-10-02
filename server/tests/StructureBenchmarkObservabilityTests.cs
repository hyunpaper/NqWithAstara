using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Structure;
using Xunit;

public sealed class StructureBenchmarkObservabilityTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    static readonly StructurePolicy HalfRPolicy = D2.PreCycle45 with
    {
        EnableTwoRFeeBreakEvenStop = true,
        CapStructuralTargetAtTwoR = true,
        EnableHalfRFeeBreakEvenStopForPositiveBenchmark = true
    };

    static StructuralTradePlan PlanA()
    {
        var evaluation = StructuralPlanner.Evaluate(D2.ExampleA(), D6.WiringPolicy);
        Assert.True(evaluation.Viable);
        return evaluation.Plan! with { Target = evaluation.Plan!.EntryReference + 20m };
    }

    static StructuralEntryRequest Request(string eventId, double? benchmarkReturnPercent) =>
        new(Fx.Symbol, Fx.At(39), Fx.At(40), Fx.SessionEnd,
            StructuralSimulation.Freeze(PlanA(), eventId, "UP", 40, 60, Fx.At(40), Fx.At(40), HalfRPolicy),
            BenchmarkReturnPercent: benchmarkReturnPercent);

    [Fact]
    public void 벤치마크가_있으면_AVAILABLE과_수익률_그리고_half_R_정책이_기록된다()
    {
        var trade = StructuralSimulation.Enter([], Request("TEST|bench-available", .25), HalfRPolicy).Trade!;

        var benchmark = Assert.IsType<EntryBenchmarkTags>(trade.Structure!.Benchmark);
        Assert.Equal(StructuralSimulation.BenchmarkAvailable, benchmark.Status);
        Assert.Equal(.25, benchmark.ReturnPercent);
        Assert.Equal(StructuralSimulation.HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion,
            trade.Structure.StructuralExitPolicyVersion);
    }

    [Fact]
    public void 벤치마크가_없으면_UNAVAILABLE과_기본_청산_정책이_기록되고_동작은_같다()
    {
        var missing = StructuralSimulation.Enter([], Request("TEST|bench-missing", null), HalfRPolicy).Trade!;
        var reference = StructuralSimulation.Enter([], Request("TEST|bench-missing", null), HalfRPolicy).Trade!;

        var benchmark = Assert.IsType<EntryBenchmarkTags>(missing.Structure!.Benchmark);
        Assert.Equal(StructuralSimulation.BenchmarkUnavailable, benchmark.Status);
        Assert.Null(benchmark.ReturnPercent);
        Assert.Equal(StructuralSimulation.TwoRTargetAndFeeBreakEvenExitPolicyVersion,
            missing.Structure.StructuralExitPolicyVersion);
        Assert.Equal(reference.Stop, missing.Stop);
        Assert.Equal(reference.Target, missing.Target);
        Assert.Equal(reference.EntryPrice, missing.EntryPrice);
    }

    [Fact]
    public void 벤치마크가_음수여도_출처_상태는_AVAILABLE이다()
    {
        var trade = StructuralSimulation.Enter([], Request("TEST|bench-negative", -.4), HalfRPolicy).Trade!;

        Assert.Equal(StructuralSimulation.BenchmarkAvailable, trade.Structure!.Benchmark!.Status);
        Assert.Equal(-.4, trade.Structure.Benchmark.ReturnPercent);
        Assert.Equal(StructuralSimulation.TwoRTargetAndFeeBreakEvenExitPolicyVersion,
            trade.Structure.StructuralExitPolicyVersion);
    }

    [Fact]
    public void 벤치마크_필드가_없는_기존_simtrades_json은_null로_복원된다()
    {
        var trade = StructuralSimulation.Enter([], Request("TEST|bench-legacy", .1), HalfRPolicy).Trade!;
        var node = JsonNode.Parse(JsonSerializer.Serialize(new List<SimTrade> { trade }, Json))!.AsArray();
        var structure = node[0]!["structure"]!.AsObject();
        Assert.True(structure.Remove("benchmark"));

        var legacy = Assert.Single(JsonSerializer.Deserialize<List<SimTrade>>(node.ToJsonString(), Json)!);

        Assert.Null(legacy.Structure!.Benchmark);
        Assert.Equal(trade.Structure!.EntryEventId, legacy.Structure.EntryEventId);
        Assert.Equal(trade.Structure.StructuralExitPolicyVersion, legacy.Structure.StructuralExitPolicyVersion);
        Assert.Equal(trade.Structure.PlanSnapshot.PlanId, legacy.Structure.PlanSnapshot.PlanId);
        Assert.Equal(trade.Structure.PlanSnapshot.PolicyHash, legacy.Structure.PlanSnapshot.PolicyHash);
        Assert.Equal(trade.Stop, legacy.Stop);
        Assert.Equal(trade.Target, legacy.Target);
    }

    [Fact]
    public void 벤치마크_필드는_simtrades_json_왕복에서_보존된다()
    {
        var trade = StructuralSimulation.Enter([], Request("TEST|bench-roundtrip", .1), HalfRPolicy).Trade!;

        var restored = Assert.Single(JsonSerializer.Deserialize<List<SimTrade>>(
            JsonSerializer.Serialize(new List<SimTrade> { trade }, Json), Json)!);

        Assert.Equal(trade.Structure!.Benchmark, restored.Structure!.Benchmark);
        Assert.Equal(trade.Structure.StructuralExitPolicyVersion, restored.Structure.StructuralExitPolicyVersion);
        Assert.Equal(trade.Structure.Reentry, restored.Structure.Reentry);
    }

    [Fact]
    public void 관측_note는_벤치마크_상태와_청산_정책_버전을_담는다()
    {
        var available = StructuralSimulation.Enter([], Request("TEST|note-a", .1), HalfRPolicy).Trade!;
        var missing = StructuralSimulation.Enter([], Request("TEST|note-b", null), HalfRPolicy).Trade!;

        Assert.Equal(
            [StructureAnalysisService.NoteEntryBenchmarkAvailable,
             StructureAnalysisService.NoteExitPolicyPrefix + StructuralSimulation.HalfRPositiveBenchmarkFeeBreakEvenExitPolicyVersion],
            StructureAnalysisService.EntryObservabilityNotes(available).ToArray());
        Assert.Equal(
            [StructureAnalysisService.NoteEntryBenchmarkUnavailable,
             StructureAnalysisService.NoteExitPolicyPrefix + StructuralSimulation.TwoRTargetAndFeeBreakEvenExitPolicyVersion],
            StructureAnalysisService.EntryObservabilityNotes(missing).ToArray());
        Assert.Empty(StructureAnalysisService.EntryObservabilityNotes(null));
    }

    [Fact]
    public async Task health는_당일_v5_진입_중_벤치마크_결측_건수만_센다()
    {
        var today = DateTimeOffset.Parse("2026-10-02T14:00:00Z");
        var yesterday = today.AddDays(-1);
        var tradingDate = MarketRules.TradingDate(today);
        var todayMissing = StructuralSimulation.Enter([], Request("TEST|h-1", null) with { EnteredAt = today }, HalfRPolicy).Trade!;
        var todayAvailable = StructuralSimulation.Enter([], Request("TEST|h-2", .1) with { EnteredAt = today }, HalfRPolicy).Trade!;
        var yesterdayMissing = StructuralSimulation.Enter([], Request("TEST|h-3", null) with { EnteredAt = yesterday }, HalfRPolicy).Trade!;
        var legacy = todayMissing with
        {
            Id = "legacy", Structure = todayMissing.Structure! with { EntryEventId = "TEST|h-4", Benchmark = null }
        };
        var v4 = new SimTrade("v4", "SOXL", "SETUP", today, 100, 110, 90, null, null, "OPEN", null, null, null, 100, Logic: "v4");
        var store = new MemoryStore();
        await store.Write("simtrades.json", new List<SimTrade> { todayMissing, todayAvailable, yesterdayMissing, legacy, v4 });

        var health = await new StructuralEntryHealthService(store).HealthAsync(tradingDate);

        var json = JsonSerializer.SerializeToElement(health, Json);
        Assert.Equal("2026-10-02", json.GetProperty("tradingDate").GetString());
        Assert.Equal(3, json.GetProperty("entries").GetInt32());
        Assert.Equal(1, json.GetProperty("benchmarkMissingEntries").GetInt32());
    }

    [Fact]
    public async Task health는_거래_파일이_없으면_0을_반환한다()
    {
        var health = await new StructuralEntryHealthService(new MemoryStore()).HealthAsync(new DateOnly(2026, 10, 2));

        var json = JsonSerializer.SerializeToElement(health, Json);
        Assert.Equal(0, json.GetProperty("entries").GetInt32());
        Assert.Equal(0, json.GetProperty("benchmarkMissingEntries").GetInt32());
    }

    sealed class MemoryStore : ILocalStore
    {
        readonly Dictionary<string, string> data = [];
        public Task<T> Read<T>(string file, T fallback) => Task.FromResult(data.TryGetValue(file, out var json)
            ? JsonSerializer.Deserialize<T>(json, Json)! : fallback);
        public Task Write<T>(string file, T value) { data[file] = JsonSerializer.Serialize(value, Json); return Task.CompletedTask; }
        public async Task<TResult> Update<T, TResult>(string file, T fallback, Func<T, (T Data, TResult Result)> change)
        {
            var current = await Read(file, fallback); var changed = change(current);
            await Write(file, changed.Data); return changed.Result;
        }
    }
}
