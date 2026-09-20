using Astra.Server.Domain.Structure;

namespace Astra.Server.Domain;

/// <summary>시간순 feature와 결과. 관측 시각 이후의 결과를 학습 구간에서만 사용한다.</summary>
public sealed record ExpectedValueObservation(DateTimeOffset ObservedAt, string Bucket,
    double FeatureScore, double NetR, bool CostComplete, TradeSide Side, string Regime);

public sealed record ExpectedValueThreshold(double Value, int TrainingRows, double TrainingExpectedNetR,
    int ValidationRows, double? ValidationExpectedNetR, string SelectionBasis);

/// <summary>train 결과로 임계값을 하나 고정한 뒤 이후 validation에는 재선택하지 않는다.</summary>
public static class WalkForwardExpectedValue
{
    public static StructurePolicy ApplyToPolicy(StructurePolicy policy, ExpectedValueThreshold selected)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy with { ExpectedValueFeatureThreshold = selected.Value };
    }

    public static bool Allows(StructurePolicy policy, double featureScore) =>
        policy.ExpectedValueFeatureThreshold is not { } threshold || featureScore >= threshold;

    public static ExpectedValueThreshold Select(IReadOnlyList<ExpectedValueObservation> training,
        IReadOnlyList<double> candidates, int minimumRows = 1)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) throw new ArgumentException("임계값 후보가 비어 있습니다.", nameof(candidates));
        var ordered = training.OrderBy(x => x.ObservedAt).ToArray();
        var best = candidates
            .Distinct().OrderBy(x => x)
            .Select(value =>
            {
                var rows = ordered.Where(x => x.FeatureScore >= value).ToArray();
                var expected = rows.Length == 0 ? double.NegativeInfinity : rows.Average(x => x.NetR);
                return (value, rows.Length, expected);
            })
            .Where(x => x.Length >= minimumRows)
            .OrderByDescending(x => x.expected)
            .ThenByDescending(x => x.Length)
            .ThenBy(x => x.value)
            .FirstOrDefault();
        if (best.Length < minimumRows)
            throw new InvalidOperationException("학습 구간에 최소 거래 수를 만족하는 임계값이 없습니다.");
        return new(best.value, best.Length, best.expected, 0, null,
            $"훈련 구간 평균 expectedNetR 내림차순, 거래 수 {minimumRows}건 이상, 동률은 낮은 임계값");
    }

    public static ExpectedValueThreshold EvaluateValidation(ExpectedValueThreshold selected,
        IReadOnlyList<ExpectedValueObservation> validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var rows = validation.Where(x => x.FeatureScore >= selected.Value)
            .OrderBy(x => x.ObservedAt).ToArray();
        return selected with
        {
            ValidationRows = rows.Length,
            ValidationExpectedNetR = rows.Length == 0 ? null : rows.Average(x => x.NetR)
        };
    }
}
