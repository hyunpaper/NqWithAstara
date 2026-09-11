namespace Astra.Server.Domain.Validation;

// 이슈 #28 — 보수적 비용 민감도. 오프라인 시나리오이며 원본 실현 손익을 덮어쓰지 않는다.
//
// 현 정책은 호가가 없을 때 spread=0으로 자격을 평가한다(#28 배경). 그 가정이 낙관적일 때 결과가 어떻게 변하는지를
// 별도 필드로만 보여준다. 시나리오 값은 사전 정의되어 있고, 결과가 좋아지는 값을 찾아 바꾸지 않는다.

/// <summary>
/// 왕복 추가 비용(%p) 시나리오. <see cref="MissingLiquidityOnly"/>=true면 호가 결측 집단에만 적용한다 —
/// 스프레드가 실제로 관측된 거래에 가상의 비용을 또 얹지 않기 위해서다.
/// </summary>
public sealed record CostScenario(string Name, string Description, double ExtraRoundTripCostPercent,
    bool MissingLiquidityOnly);

/// <summary>
/// 시나리오 결과. <see cref="RealizedMeanPercent"/>는 저장된 실현 손익 그대로이고
/// <c>Scenario*</c> 필드만 가정이 반영된 값이다. 두 값은 절대 합치지 않는다.
/// </summary>
public sealed record CostScenarioResult(string Name, string Description, double ExtraRoundTripCostPercent,
    bool MissingLiquidityOnly, int Samples, int AdjustedSamples, double? RealizedMeanPercent,
    double? ScenarioMeanPercent, double? ScenarioMedianPercent, double? RealizedWinRatePercent,
    double? ScenarioWinRatePercent, string Note);

public static class CostSensitivity
{
    public const string Note = "오프라인 시나리오다. 저장된 실현 손익을 대체하지 않으며 운영 비용 모델도 바꾸지 않는다.";

    /// <summary>사전 정의 시나리오. 임계값 탐색이 아니라 고정된 보수 가정 세 가지다.</summary>
    public static readonly CostScenario[] Default =
    [
        new("missing-liquidity-10bps", "호가 결측 건에 왕복 0.10%p 추가", 0.10, true),
        new("missing-liquidity-25bps", "호가 결측 건에 왕복 0.25%p 추가", 0.25, true),
        new("all-trades-25bps", "모든 진입에 왕복 0.25%p 추가", 0.25, false)
    ];

    public static IReadOnlyList<CostScenarioResult> Evaluate(IReadOnlyList<LinkedCandidate> candidates,
        IReadOnlyList<CostScenario>? scenarios = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var realized = candidates.Where(x => x is not null && x.HasRealizedPnl).ToArray();
        return (scenarios ?? Default).Select(scenario => Apply(realized, scenario)).ToArray();
    }

    static CostScenarioResult Apply(IReadOnlyList<LinkedCandidate> realized, CostScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (realized.Count == 0)
            return new CostScenarioResult(scenario.Name, scenario.Description, scenario.ExtraRoundTripCostPercent,
                scenario.MissingLiquidityOnly, 0, 0, null, null, null, null, null, Note);

        var original = realized.Select(x => x.RealizedPnlPercent!.Value).ToArray();
        var adjusted = new double[realized.Count];
        var adjustedSamples = 0;
        for (var i = 0; i < realized.Count; i++)
        {
            var applies = !scenario.MissingLiquidityOnly ||
                realized[i].Liquidity == LiquidityClass.Missing ||
                realized[i].Trade?.MissingLiquidityCost == true;
            adjusted[i] = applies ? original[i] - scenario.ExtraRoundTripCostPercent : original[i];
            if (applies) adjustedSamples++;
        }

        var sorted = adjusted.Order().ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2d;
        return new CostScenarioResult(scenario.Name, scenario.Description, scenario.ExtraRoundTripCostPercent,
            scenario.MissingLiquidityOnly, realized.Count, adjustedSamples,
            ValidationEvaluator.Round(original.Average()), ValidationEvaluator.Round(adjusted.Average()),
            ValidationEvaluator.Round(median),
            ValidationEvaluator.Round(original.Count(x => x > 0) * 100d / original.Length),
            ValidationEvaluator.Round(adjusted.Count(x => x > 0) * 100d / adjusted.Length), Note);
    }
}
