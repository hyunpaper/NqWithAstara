using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

/// <summary>shadow 수집 대상을 고정 대상·관심종목·최근 24h 직접 회사 대상 빈도 순으로 고른다(§8 1단계, #309).</summary>
public sealed class ScoreCoreTargetSelector(ILocalStore localStore, NewsScoreRecordReader news, ScoreCoreOptions options)
{
    public static readonly TimeSpan NewsWindow = TimeSpan.FromHours(24);
    public static readonly IReadOnlyList<(string TargetId, ImpactTargetKind TargetKind)> FixedTargets =
    [
        ("MARKET", ImpactTargetKind.Market),
        ("OIL", ImpactTargetKind.Asset),
        ("TREASURY", ImpactTargetKind.Asset),
    ];

    public async Task<IReadOnlyList<(string TargetId, ImpactTargetKind TargetKind)>> SelectAsync(
        DateTimeOffset asOf, CancellationToken ct)
    {
        var watchlist = (await localStore.Read("watchlist.json", new List<WatchItem>()))
            .Select(x => x.Symbol?.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(x => (TargetId: x, TargetKind: ImpactTargetKind.Company));
        var frequent = (await news.IncludedAsync(asOf, NewsWindow, ct))
            .SelectMany(record => (record.Relevance?.Targets ?? [])
                .Where(x => string.Equals(x.Kind.Trim(), "company", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Relation.Trim(), "direct", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.Id))
                .Select(x => (Id: x.Id.Trim().ToUpperInvariant(),
                    Group: string.IsNullOrWhiteSpace(record.ClassifiedFrom) ? record.Id : record.ClassifiedFrom)))
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => (Id: x.Key, Count: x.Select(y => y.Group).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => (TargetId: x.Id, TargetKind: ImpactTargetKind.Company));
        return FixedTargets.Concat(watchlist).Concat(frequent)
            .DistinctBy(x => (x.TargetId, x.TargetKind))
            .Take(options.TargetLimit)
            .ToArray();
    }
}
