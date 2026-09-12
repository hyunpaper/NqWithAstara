using System.Collections.Immutable;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Confluence;

/// <summary>합산에 들어간 기법 한 개의 기여 (C4, #167). Contributing=false면 warmup이거나 c=0이다.</summary>
public sealed record ConfluenceContribution(string Name, double Score, double Confidence, double Weight,
    bool Warmup, bool Contributing, string? CorrelationGroup,
    ImmutableSortedDictionary<string, double?> Evidence);

/// <summary>
/// 완료 봉 한 개의 컨플루언스 점수 (C4, #167). 기여 기법이 하나도 없으면 Score는 null이며 0으로 대체하지 않는다.
/// </summary>
public sealed record ConfluenceScore(string Symbol, DateTimeOffset BarEnd, double? Score,
    ImmutableArray<ConfluenceContribution> Contributing, int WarmupCount, string PolicyHash, string WeightsVersion);

/// <summary>
/// C4 합산: `Σ w·c·s / Σ w·c`. 초기 가중치는 전부 1.0(미검증)이며 상관군은 군 안에서 1/n로 나눈다.
/// warmup·c=0은 분모에서 자연히 빠진다. 순수 함수이며 시계를 읽지 않는다.
/// </summary>
public static class ConfluenceAggregator
{
    public const double DefaultWeight = 1.0;
    public const string DefaultWeightsVersion = "uniform.1";

    public static ConfluenceScore Aggregate(string symbol, DateTimeOffset barEnd,
        IReadOnlyList<TechniqueSignal> signals, ConfluencePolicy policy,
        IReadOnlyDictionary<string, double>? weights = null, string? weightsVersion = null)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(policy);

        var contributing = signals
            .Where(x => !x.Warmup && x.Confidence > 0 && double.IsFinite(x.Score) && double.IsFinite(x.Confidence))
            .ToArray();
        var groupSizes = contributing
            .Select(x => policy.CorrelationGroupOf(x.Name))
            .Where(x => x is not null)
            .GroupBy(x => x!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

        double numerator = 0, denominator = 0;
        var rows = ImmutableArray.CreateBuilder<ConfluenceContribution>(signals.Count);
        foreach (var signal in signals)
        {
            var group = policy.CorrelationGroupOf(signal.Name);
            var included = contributing.Contains(signal);
            var baseWeight = weights is not null && weights.TryGetValue(signal.Name, out var configured) &&
                             double.IsFinite(configured) && configured >= 0
                ? configured
                : DefaultWeight;
            var size = group is not null && groupSizes.TryGetValue(group, out var count) && count > 0 ? count : 1;
            var weight = included ? baseWeight / size : baseWeight;
            if (included)
            {
                numerator += weight * signal.Confidence * signal.Score;
                denominator += weight * signal.Confidence;
            }
            rows.Add(new ConfluenceContribution(signal.Name, signal.Score, signal.Confidence,
                IndicatorRounding.Ratio(weight), signal.Warmup, included, group, signal.Evidence));
        }

        var score = denominator > 0 ? IndicatorRounding.Ratio(numerator / denominator) : (double?)null;
        return new ConfluenceScore(symbol, barEnd, score, rows.ToImmutable(),
            signals.Count(x => x.Warmup), policy.PolicyHash, weightsVersion ?? DefaultWeightsVersion);
    }
}
