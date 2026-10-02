using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Astra.Server.Domain.News;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

/// <summary>뉴스 저장 기록을 asOf 시점 기준으로 읽는다. 관련성 include만 남긴다(§6, #309).</summary>
public sealed class NewsScoreRecordReader(INewsStore store)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim _gate = new(1, 1);
    (DateTimeOffset AsOf, TimeSpan Lookback, IReadOnlyList<NewsRecord> Records)? _cache;

    public async Task<IReadOnlyList<NewsRecord>> IncludedAsync(DateTimeOffset asOf, TimeSpan lookback,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } cached && cached.AsOf == asOf && cached.Lookback == lookback) return cached.Records;
            var from = asOf - lookback;
            var latest = new Dictionary<string, NewsRecord>(StringComparer.Ordinal);
            for (var day = DateOnly.FromDateTime(from.UtcDateTime); day <= DateOnly.FromDateTime(asOf.UtcDateTime);
                 day = day.AddDays(1))
            {
                foreach (var line in await store.ReadLinesAsync(
                             day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl", ct))
                {
                    if (Parse(line) is not { } record) continue;
                    if (record.ClassifiedAt > asOf || record.ClassifiedAt < from) continue;
                    if (latest.TryGetValue(record.Id, out var prior) && prior.ClassifiedAt > record.ClassifiedAt) continue;
                    latest[record.Id] = record;
                }
            }
            var records = latest.Values.Where(x => x.Relevance?.Included == true)
                .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            _cache = (asOf, lookback, records);
            return records;
        }
        finally { _gate.Release(); }
    }

    static NewsRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<NewsRecord>(line, Json);
            return string.IsNullOrWhiteSpace(record?.Id) ? null : record;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>관련성 include 뉴스를 대상별 사건 근거로 변환한다. 방향은 Unknown 고정(§3.0, #309).</summary>
public sealed class NewsScoreEvidenceSource(NewsScoreRecordReader reader, ScoreCoreOptions options)
    : IScoreEvidenceSource
{
    public const string SourceName = "news";
    public string Name => SourceName;

    public async Task<ScoreEvidenceBatch> ReadAsync(ScoreEvidenceRequest request, CancellationToken ct)
    {
        var records = await reader.IncludedAsync(request.AsOf, options.Lookback, ct);
        var evidence = records.SelectMany(record => Evidence(record)
                .Where(x => x.Target.Kind == request.TargetKind &&
                            string.Equals(x.Target.Id, request.TargetId, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableArray();
        return new(SourceName, request.AsOf, false, evidence,
            evidence.Length > 0 ? "ready" : "insufficient_data",
            evidence.Length > 0 ? null : "no_included_news");
    }

    public static IEnumerable<EventEvidence> Evidence(NewsRecord record)
    {
        if (record.Relevance is not { Included: true } relevance) yield break;
        var published = record.CreatedAt == DateTimeOffset.MinValue ? (DateTimeOffset?)null : record.CreatedAt;
        var groupId = string.IsNullOrWhiteSpace(record.ClassifiedFrom) ? record.Id : record.ClassifiedFrom;
        var classifier = $"{relevance.PolicyVersion}:{relevance.Classifier}";
        foreach (var target in (relevance.Targets ?? [])
                     .GroupBy(x => (Id: x.Id.Trim().ToUpperInvariant(), Kind: x.Kind.Trim().ToLowerInvariant()))
                     .Select(x => x.First()))
        {
            if (TargetKind(target.Kind) is not { } kind || string.IsNullOrWhiteSpace(target.Id)) continue;
            yield return new EventEvidence(record.Id, groupId, record.Source, record.Source, record.Url,
                record.Sentiment, relevance.EventKind,
                new ImpactTarget(target.Id.Trim().ToUpperInvariant(), kind, Directness(target.Relation), target.Evidence),
                ImpactHorizon.Session, ImpactDirection.Unknown, relevance.Action, relevance.EvidenceSpan,
                FactVerification.Unknown, MarketExpectationStatus.Unknown, null, null, null, published,
                record.CollectedAt ?? record.ClassifiedAt, record.ClassifiedAt, ImmutableArray<string>.Empty,
                new EvidenceQuality(null, null, null, null), classifier, SourceVerified: false);
        }
    }

    public static ImpactTargetKind? TargetKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "company" => ImpactTargetKind.Company,
        "sector" => ImpactTargetKind.Sector,
        "asset" => ImpactTargetKind.Asset,
        "market" => ImpactTargetKind.Market,
        _ => null
    };

    static TargetDirectness Directness(string relation) => relation.Trim().ToLowerInvariant() switch
    {
        "direct" => TargetDirectness.Direct,
        "indirect" => TargetDirectness.Indirect,
        _ => TargetDirectness.Unresolved
    };
}
