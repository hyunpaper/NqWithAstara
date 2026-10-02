using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Application.ScoreCore;
using Astra.Server.Domain.News;
using Astra.Server.Domain.ScoreCore;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ScoreCoreNewsEvidenceTests
{
    static readonly DateTimeOffset AsOf = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task OnlyIncludedRecordsClassifiedByAsOfBecomeEvidence()
    {
        var store = Store(
            Record("include", AsOf.AddHours(-1)),
            Record("review", AsOf.AddHours(-1), NewsRelevanceDecisions.Review),
            Record("exclude", AsOf.AddHours(-1), NewsRelevanceDecisions.Exclude),
            Record("future", AsOf.AddMinutes(1)),
            Record("stale", AsOf.AddHours(-49)));

        var batch = await Source(store).ReadAsync(new("NVDA", ImpactTargetKind.Company, AsOf), default);

        Assert.Equal("include", Assert.Single(batch.Evidence).EvidenceId);
        Assert.False(batch.SupportsHistoricalPointInTime);
        Assert.Equal(NewsScoreEvidenceSource.SourceName, batch.Source);
    }

    [Fact]
    public async Task LaterReclassificationAfterAsOfDoesNotLeakIntoEarlierCapture()
    {
        var first = Record("same", AsOf.AddHours(-1));
        var reclassified = Record("same", AsOf.AddMinutes(10), NewsRelevanceDecisions.Exclude);
        var store = Store(first, reclassified);

        var batch = await Source(store).ReadAsync(new("NVDA", ImpactTargetKind.Company, AsOf), default);

        Assert.Equal(AsOf.AddHours(-1), Assert.Single(batch.Evidence).ObservedAt);
    }

    [Fact]
    public async Task MissingPublishedAtIsExcludedByAssembler()
    {
        var store = Store(Record("unknown-time", AsOf.AddHours(-1)) with { CreatedAt = DateTimeOffset.MinValue });

        var result = await new AsOfEvidenceAssembler([Source(store)])
            .AssembleAsync(new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.Empty(result.Evidence);
        Assert.Contains(result.Exclusions, x => x.EvidenceId == "unknown-time" && x.Reason == "published_at_missing");
    }

    [Fact]
    public async Task RepostsInOneGroupCountAsOneUniqueEvent()
    {
        var store = Store(
            Record("lead", AsOf.AddHours(-1)),
            Record("copy-1", AsOf.AddHours(-1)) with { ClassifiedFrom = "lead" },
            Record("copy-2", AsOf.AddHours(-1)) with { ClassifiedFrom = "lead" });
        var service = new ScoreCoreSnapshotService(
            new AsOfEvidenceAssembler([Source(store), new MacroCalendarEvidenceSource()]),
            new ScoreCoreApplicationTests.MemorySnapshotStore());

        var snapshot = await service.CaptureAsync(new("NVDA", ImpactTargetKind.Company, AsOf));

        Assert.Equal(3, snapshot.EvidenceCount);
        Assert.Equal(1, snapshot.UniqueEventCount);
        Assert.Equal(3, snapshot.Score.UnknownCount);
        Assert.Equal("insufficient_data", snapshot.Status);
        Assert.Contains(snapshot.Coverage, x => x.Source == MacroCalendarEvidenceSource.SourceName &&
            x.Status == "unavailable" && x.Reason == MacroCalendarEvidenceSource.UnavailableReason);
    }

    [Fact]
    public async Task DirectionIsAlwaysUnknownAndSentimentNeverBecomesSeverity()
    {
        var store = Store(
            Record("strong-positive", AsOf.AddHours(-1)) with { Sentiment = NewsSentiments.Positive, Strength = 5 },
            Record("strong-negative", AsOf.AddHours(-1)) with { Sentiment = NewsSentiments.Negative, Strength = 5 });

        var batch = await Source(store).ReadAsync(new("NVDA", ImpactTargetKind.Company, AsOf), default);
        var snapshot = ScoreCoreAggregator.Aggregate("NVDA", ImpactTargetKind.Company, AsOf, batch.Evidence);

        Assert.Equal(2, batch.Evidence.Length);
        Assert.All(batch.Evidence, x =>
        {
            Assert.Equal(ImpactDirection.Unknown, x.ImpactDirection);
            Assert.Null(x.Severity);
            Assert.Null(x.PositiveSeverity);
            Assert.Null(x.NegativeSeverity);
            Assert.False(x.SourceVerified);
        });
        Assert.All(snapshot.Horizons, x => Assert.Null(x.SignedEvidence));
        Assert.Equal(0, snapshot.IncludedCount);
    }

    [Fact]
    public async Task TargetsMapKindAndRelationAndIgnoreOtherTargets()
    {
        var record = Record("multi", AsOf.AddHours(-1)) with
        {
            Relevance = Assessment(NewsRelevanceDecisions.Include,
                new("nvda", "company", "indirect", "공급망"), new("OIL", "asset", "direct", "원유"),
                new("X", "unknown-kind", "direct", "기타"))
        };
        var store = Store(record);
        var source = Source(store);

        var company = await source.ReadAsync(new("NVDA", ImpactTargetKind.Company, AsOf), default);
        var asset = await source.ReadAsync(new("OIL", ImpactTargetKind.Asset, AsOf), default);
        var missing = await source.ReadAsync(new("TSLA", ImpactTargetKind.Company, AsOf), default);

        Assert.Equal(TargetDirectness.Indirect, Assert.Single(company.Evidence).Target.Directness);
        Assert.Equal("NVDA", company.Evidence[0].Target.Id);
        Assert.Equal(TargetDirectness.Direct, Assert.Single(asset.Evidence).Target.Directness);
        Assert.Empty(missing.Evidence);
        Assert.Equal("no_included_news", missing.Reason);
    }

    static NewsScoreEvidenceSource Source(MemoryNewsStore store)
        => new(new NewsScoreRecordReader(store), new ScoreCoreOptions());

    internal static MemoryNewsStore Store(params NewsRecord[] records)
    {
        var store = new MemoryNewsStore();
        foreach (var record in records)
        {
            var file = record.ClassifiedAt.UtcDateTime.ToString("yyyy-MM-dd") + ".jsonl";
            if (!store.Files.TryGetValue(file, out var lines)) store.Files[file] = lines = [];
            lines.Add(JsonSerializer.Serialize(record, Json));
        }
        return store;
    }

    internal static NewsRecord Record(string id, DateTimeOffset classifiedAt,
        string decision = NewsRelevanceDecisions.Include, string target = "NVDA")
        => new(id, "제목 " + id, "sbhnews", classifiedAt.AddMinutes(-10), [], [], [target],
            NewsSentiments.Neutral, 1, "이유", "qwen", 0, classifiedAt,
            CollectedAt: classifiedAt.AddMinutes(-5),
            Relevance: Assessment(decision, new NewsEventTarget(target, "company", "direct", "text:" + target)));

    static NewsRelevanceAssessment Assessment(string decision, params NewsEventTarget[] targets)
        => new("sbh-relevance-v1", decision, "company_contract", "회사", "계약 체결", targets, "근거 문장", "사유");
}
