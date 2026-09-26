using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>
/// 이슈 #28 — Application 조회 서비스의 데이터 감사·연결 경로 검증.
/// 관측 JSONL은 운영에서 쓰는 것과 같은 <see cref="StructureObservationRecord"/> 직렬화 형태로 만든다
/// (운영 실데이터를 커밋하지 않고도 실제 필드 계약을 통과시키기 위해서다).
/// </summary>
public sealed class ValidationServiceTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly DateTimeOffset Now = new(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);

    static StructurePlanDto Plan(bool missingLiquidity = false) =>
        new("plan-1", "PULLBACK", 100m, 99.6m, 99.4m, 101.5m, "z-support", 99.4m, 99.7m, "z-resist", 101.5m,
            101.9m, .05m, "session-atr", .02m, 1.3m, .8m, 1.6m, .6, .1m, .02m,
            missingLiquidity ? null : .04m, missingLiquidity, "cost-eligibility.1", "cost-fill.1",
            Vx.At(0, 40), Vx.At(0, 45), Vx.Engine, Vx.Policy, [], "지지 반응 후 저항 하단 앞 계획");

    static StructureCandidateDto Candidate(string eventId, string state, bool planned, double? quality = 60,
        string[]? rejections = null) =>
        new(eventId, "PULLBACK", "zone-1", Vx.At(0, 39), Vx.At(0, 40), Vx.At(0, 40), Vx.At(0, 45), state, quality,
            100m, planned ? 99.6m : null, planned ? 99.4m : null, planned ? 101.5m : null, planned ? 1.6m : null,
            planned ? Plan() : null, [], rejections ?? [], [], false, true);

    static StructureObservationRecord Record(string observationId, StructureCandidateDto[] candidates,
        int minute = 41, string detail = "full") =>
        new(observationId, "v5-observation.1", Vx.Symbol, Vx.At(0, minute), Vx.SessionOn(0), Vx.At(0, minute),
            Vx.At(0, minute), Vx.At(0, minute - 1), Vx.Policy, Vx.Engine, "active", "v5", detail, "available",
            "ENTERED", candidates.FirstOrDefault()?.EventId,
            new StructureTrendDto("UP", 40, 30, 50, .6, .12, 100.1, 99.9, 100, .3, false, 45, Vx.At(0, minute),
                [], [], []),
            null, null, candidates, [], []);

    static string File => StructureObservationWriter.FileName(MarketRules.TradingDate(Vx.SessionOn(0)));

    static (ValidationQueryService Service, MemoryObservationStore Observations, RecordingStore Store) Build(
        params string[] lines)
    {
        var observations = new MemoryObservationStore();
        foreach (var line in lines) observations.AppendAsync(File, line, CancellationToken.None).GetAwaiter().GetResult();
        var store = new RecordingStore();
        return (new ValidationQueryService(store, observations, new MovableClock(Now)), observations, store);
    }

    static string Line(StructureObservationRecord record) => JsonSerializer.Serialize(record, Json);

    [Fact]
    public async Task ObservationFileAndTradeStoreAreJoinedIntoOneChain()
    {
        var (service, _, store) = Build(Line(Record("obs-1", [Candidate("E1", "ENTERED", true)])));
        store.Seed(Vx.Trade("t-1", "E1", 1.5));

        var (status, report) = await service.GetAsync(null, CancellationToken.None);

        Assert.Equal(200, status);
        Assert.NotNull(report);
        Assert.Equal(1, report!.Data.FilesFound);
        Assert.Equal(1, report.Data.Lines);
        Assert.Equal(0, report.Data.ParseFailures);
        Assert.Equal(1, report.Link.DistinctEvents);
        Assert.Equal(1, report.Link.TradesLinked);
        Assert.Equal(0, report.Link.TradesUnlinked);
        Assert.Equal(1, report.Evaluation.Overall.Scope.RealizedPnl);
        Assert.Equal(1.5, report.Evaluation.Overall.Pnl.MeanPercent);
        Assert.Equal(Vx.Engine, report.EngineVersion);
        Assert.NotEmpty(report.Data.Contract);
    }

    /// <summary>손상된 줄은 삭제하지 않고 실패 건수로 남긴다(§16). 파싱 실패가 조회를 깨뜨리지 않는다.</summary>
    [Fact]
    public async Task CorruptedLinesAreCountedInsteadOfThrowing()
    {
        var (service, _, _) = Build("{ not json", Line(Record("obs-1", [Candidate("E1", "READY", true)])));

        var (status, report) = await service.GetAsync(null, CancellationToken.None);

        Assert.Equal(200, status);
        Assert.Equal(2, report!.Data.Lines);
        Assert.Equal(1, report.Data.ParseFailures);
        Assert.Equal(1, report.Link.DistinctEvents);
    }

    /// <summary>파일이 없는 날은 "없음"으로 남는다 — 0건 성과로 바꾸지 않는다.</summary>
    [Fact]
    public async Task DaysWithoutAnObservationFileAreReportedAsMissing()
    {
        var (service, _, _) = Build(Line(Record("obs-1", [Candidate("E1", "READY", true)])));

        var (_, report) = await service.GetAsync(5, CancellationToken.None);

        Assert.Equal(5, report!.Data.DaysScanned);
        Assert.Equal(1, report.Data.FilesFound);
        Assert.Equal(4, report.Data.DaysWithoutFile.Count);
        Assert.Equal(5, report.Data.Files.Count);
    }

    /// <summary>계획이 없는 후보는 비용 가정 필드가 비어 있다 — 결측 비율을 그대로 보고한다.</summary>
    [Fact]
    public async Task FieldGapsExposeCostAssumptionsThatWereNeverCollected()
    {
        var (service, _, _) = Build(Line(Record("obs-1",
            [Candidate("E1", "REJECTED", false, null, ["NET_R_BELOW_MIN"]), Candidate("E2", "READY", true)])));

        var (_, report) = await service.GetAsync(null, CancellationToken.None);

        var cost = Assert.Single(report!.Data.FieldGaps, x => x.Field == "plan.costAssumptions");
        Assert.Equal(1, cost.Missing);
        Assert.Equal(2, cost.Total);
        var quality = Assert.Single(report.Data.FieldGaps, x => x.Field == "entryQuality");
        Assert.Equal(1, quality.Missing);
        Assert.Contains(LinkCodes.CostAssumptionOnlyForPlannedCandidates, report.Limitations);
    }

    [Fact]
    public async Task TradeWithoutObservationIsSurfacedAsALimitation()
    {
        var (service, _, store) = Build();
        store.Seed(Vx.Trade("t-1", "E1", 2.0));

        var (_, report) = await service.GetAsync(null, CancellationToken.None);

        Assert.Equal(0, report!.Link.DistinctEvents);
        Assert.Equal(1, report.Link.TradesUnlinked);
        Assert.Contains($"{LinkCodes.TradeWithoutObservation}:t-1", report.Link.Conflicts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ValidationQueryService.MaxWindowDays + 1)]
    public async Task WindowOutsideTheAllowedRangeIsRejected(int days)
    {
        var (service, _, _) = Build();

        var (status, report) = await service.GetAsync(days, CancellationToken.None);

        Assert.Equal(400, status);
        Assert.Null(report);
    }

    /// <summary>조회는 읽기 전용이다 — 어떤 파일에도 쓰지 않는다.</summary>
    [Fact]
    public async Task QueryNeverWritesToAnyStore()
    {
        var (service, observations, store) = Build(Line(Record("obs-1", [Candidate("E1", "ENTERED", true)])));
        store.RejectWrites = true;
        var appendsBefore = observations.Appends;
        var writesBefore = observations.TextWrites;

        await service.GetAsync(null, CancellationToken.None);

        Assert.Empty(store.Writes);
        Assert.Equal(appendsBefore, observations.Appends);
        Assert.Equal(writesBefore, observations.TextWrites);
    }
}

/// <summary>이슈 #28 — additive `/api/validation` 엔드포인트의 HTTP 계약.</summary>
public sealed class ValidationEndpointTests(AstraHostFixture host) : IClassFixture<AstraHostFixture>
{
    [Fact]
    public async Task ValidationEndpointReturnsCamelCaseReport()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/validation");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        foreach (var name in new[] { "generatedAt", "asOf", "windowDays", "engineVersion", "policyHash", "data",
            "link", "evaluation", "walkForward", "riskFrequency", "costScenarios", "limitations",
            "probabilityCalibration" })
            Assert.True(root.TryGetProperty(name, out _), $"missing property: {name}");
        Assert.False(root.TryGetProperty("Data", out _));

        // 표본이 없으면 승률·평균은 null이고 결론은 검증 불가다(0%로 표시하지 않는다).
        var overall = root.GetProperty("evaluation").GetProperty("overall");
        Assert.Equal("InsufficientSample", overall.GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, overall.GetProperty("observedWinRatePercent").ValueKind);
        Assert.NotEmpty(root.GetProperty("evaluation").GetProperty("caveats").EnumerateArray());
        Assert.Equal(ProbabilityCalibrationEvaluator.InsufficientData,
            root.GetProperty("probabilityCalibration").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ValidationEndpointRejectsAnInvalidWindow()
    {
        using var client = host.Factory.CreateClient();
        var response = await client.GetAsync("/api/validation?days=0");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
