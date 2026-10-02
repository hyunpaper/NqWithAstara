using System.Collections.Immutable;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.ScoreCore;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ScoreCoreApplicationTests
{
    static readonly DateTimeOffset AsOf = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AssemblerAcceptsOnlyEvidenceObservedByAsOf()
    {
        var source = new StubEvidenceSource("assessed-news", request => new(
            "assessed-news", request.AsOf, false,
            [Evidence("known", AsOf.AddMinutes(-5)), Evidence("future", AsOf.AddMinutes(1))], "partial"));

        var result = await new AsOfEvidenceAssembler([source]).AssembleAsync(
            new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.Single(result.Evidence);
        Assert.Equal("known", result.Evidence[0].EvidenceId);
        Assert.Contains(result.Exclusions, x => x.EvidenceId == "future" && x.Reason == "future_evidence");
        Assert.Equal(1, result.Coverage[0].AcceptedCount);
    }

    [Fact]
    public async Task HistoricalReplayWithoutPointInTimeSnapshotIsUnavailable()
    {
        var calls = 0;
        var source = new StubEvidenceSource("news", request =>
        {
            calls++;
            return new("news", request.AsOf, false, [], "ready");
        });

        var result = await new AsOfEvidenceAssembler([source]).AssembleAsync(
            new("NVDA", ImpactTargetKind.Company, AsOf, ScoreCapturePurpose.HistoricalReplay));

        Assert.Equal("unavailable", result.Status);
        Assert.Equal(1, calls);
        Assert.Contains(result.Exclusions, x => x.Reason == "historical_point_in_time_unavailable");
    }

    [Fact]
    public async Task HistoricalReplayWithPointInTimeSourcePreservesEvidence()
    {
        var source = new StubEvidenceSource("event-archive", request => new(
            "event-archive", request.AsOf, true, [Evidence("archived", request.AsOf.AddMinutes(-5))], "ready"));

        var result = await new AsOfEvidenceAssembler([source]).AssembleAsync(
            new("NVDA", ImpactTargetKind.Company, AsOf, ScoreCapturePurpose.HistoricalReplay));

        Assert.Equal("shadow", result.Status);
        Assert.Single(result.Evidence);
        Assert.Equal(1, result.Coverage[0].AcceptedCount);
    }

    [Fact]
    public async Task UnknownEvidenceReachesDomainSnapshotAndRemainsExcludedWithCount()
    {
        var unknown = Evidence("unknown", AsOf.AddMinutes(-5)) with
        {
            ImpactDirection = ImpactDirection.Unknown,
            Severity = null
        };
        var source = new StubEvidenceSource("events", request => new(
            "events", request.AsOf, false, [unknown], "ready"));

        var snapshot = await new ScoreCoreSnapshotService(new([source]), new MemorySnapshotStore())
            .CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.Equal(1, snapshot.Score.InputCount);
        Assert.Equal(1, snapshot.Score.UnknownCount);
        Assert.Contains(snapshot.Score.ExcludedEvidence, x =>
            x.EvidenceId == "unknown" && x.Reason == "impact_direction_unknown");
        Assert.DoesNotContain(snapshot.AssemblyExclusions, x => x.EvidenceId == "unknown");
    }

    [Fact]
    public async Task MissingNewsAssessmentRemainsCoverageGapInsteadOfSentimentImpact()
    {
        var source = new StubEvidenceSource("assessed-news", request => new(
            "assessed-news", request.AsOf, false, [], "insufficient_data", "news_assessment_missing"));
        var store = new MemorySnapshotStore();

        var snapshot = await new ScoreCoreSnapshotService(new([source]), store).CaptureAsync(
            new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.Equal("insufficient_data", snapshot.Status);
        Assert.Equal(ScoreCalibrationStatus.Uncalibrated, snapshot.Score.CalibrationStatus);
        Assert.All(snapshot.Score.Horizons, x => Assert.Null(x.SignedEvidence));
        Assert.Contains(snapshot.Coverage, x => x.Reason == "news_assessment_missing");
    }

    [Fact]
    public async Task CaptureIsDeterministicAndAppendIsIdempotent()
    {
        var source = new StubEvidenceSource("events", request => new(
            "events", request.AsOf, true, [Evidence("one", AsOf.AddMinutes(-5))], "ready"));
        var store = new MemorySnapshotStore();
        var service = new ScoreCoreSnapshotService(new([source]), store);

        var first = await service.CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));
        var second = await service.CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.Equal(first.CaptureId, second.CaptureId);
        Assert.Equal(first.Score.SnapshotId, second.Score.SnapshotId);
        Assert.Single(store.Values);
        Assert.Equal("shadow", first.Status);
        Assert.Equal(ScoreCalibrationStatus.Uncalibrated, first.Score.CalibrationStatus);
    }

    [Fact]
    public async Task ChangedEvidencePayloadProducesDifferentCaptureId()
    {
        var quality = 0.8;
        var source = new StubEvidenceSource("events", request => new(
            "events", request.AsOf, true,
            [Evidence("same-id", AsOf.AddMinutes(-5)) with
            {
                Quality = new(0.9, quality, 1, 0.7)
            }], "ready"));
        var service = new ScoreCoreSnapshotService(new([source]), new MemorySnapshotStore());

        var first = await service.CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));
        quality = 0.6;
        var second = await service.CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.NotEqual(first.CaptureId, second.CaptureId);
        Assert.Equal(first.Score.SnapshotId, second.Score.SnapshotId);
    }

    [Fact]
    public async Task OrchestratorUsesStableTargetOrderAndDoesNotChangeEntryPolicy()
    {
        var store = new MemorySnapshotStore();
        var service = new ScoreCoreSnapshotService(new([]), store);

        var result = await new ScoreCoreShadowCaptureOrchestrator(service).CaptureAsync(
            [("NVDA", ImpactTargetKind.Company), ("MARKET", ImpactTargetKind.Market),
             ("NVDA", ImpactTargetKind.Company)], AsOf);

        Assert.Equal(2, result.Length);
        Assert.Equal("MARKET", result[0].Score.TargetId);
        Assert.Equal("NVDA", result[1].Score.TargetId);
        Assert.All(result, x => Assert.Equal("insufficient_data", x.Status));
    }

    static EventEvidence Evidence(string id, DateTimeOffset observedAt) => new(
        id, "event-1", "source-id", "공식 원문", "https://example.com/item", "positive",
        "company_contract", new("NVDA", ImpactTargetKind.Company, TargetDirectness.Direct),
        ImpactHorizon.Session, ImpactDirection.Favorable, "확인된 계약 수요",
        "계약 규모와 당사자가 확인됨", FactVerification.Verified, MarketExpectationStatus.Above,
        0.6, null, null, observedAt.AddMinutes(-2), observedAt.AddMinutes(-1), observedAt, [],
        new(0.9, 0.8, 1, 0.7), "assessment-v1");

    sealed class StubEvidenceSource(string name, Func<ScoreEvidenceRequest, ScoreEvidenceBatch> read)
        : IScoreEvidenceSource
    {
        public string Name => name;
        public Task<ScoreEvidenceBatch> ReadAsync(ScoreEvidenceRequest request, CancellationToken ct) =>
            Task.FromResult(read(request));
    }

    internal sealed class MemorySnapshotStore : IScoreCoreSnapshotStore
    {
        public Dictionary<string, ScoreCoreShadowSnapshot> Values { get; } = [];

        public Task<ScoreSnapshotAppendResult> AppendAsync(ScoreCoreShadowSnapshot snapshot, CancellationToken ct)
        {
            if (Values.ContainsKey(snapshot.CaptureId))
            {
                return Task.FromResult(ScoreSnapshotAppendResult.AlreadyExists);
            }
            Values.Add(snapshot.CaptureId, snapshot);
            return Task.FromResult(ScoreSnapshotAppendResult.Appended);
        }

        public Task<ScoreCoreShadowSnapshot?> FindAsync(string captureId, CancellationToken ct) =>
            Task.FromResult(Values.GetValueOrDefault(captureId));

        public Task<ScoreCoreShadowSnapshot?> FindLatestAsync(ImpactTargetKind kind, string targetId,
            CancellationToken ct) =>
            Task.FromResult(Values.Values.Where(x => x.Score.TargetKind == kind &&
                    string.Equals(x.Score.TargetId, targetId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Score.AsOf).FirstOrDefault());

        public Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct) =>
            Task.FromResult(0);
    }
}
