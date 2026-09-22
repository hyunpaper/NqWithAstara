using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

public sealed record ReplayTimeframeContract(double SourceBarMinutes, string EnginePath, bool Supported,
    string Limitation);

public static class ReplayTimeframePolicy
{
    public static ReplayTimeframeContract Contract(TimeSpan sourceSpan) => sourceSpan.TotalMinutes switch
    {
        1 => new(1, "one-minute-v5", true, "운영 1분 구조 엔진과 같은 봉 계약입니다."),
        5 => new(5, "native-five-minute-v1", true,
            "5분 원천 전용 기간 환산 정책이며 운영 1분 엔진 패리티로 해석하지 않습니다."),
        _ => new(sourceSpan.TotalMinutes, "unsupported", false,
            "지원 주기는 1분 또는 5분이며 다른 주기는 성과 집계에서 제외합니다.")
    };

    public static StructurePolicy Apply(StructurePolicy policy, ReplayTimeframeContract contract)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(contract);
        if (!contract.Supported) throw new InvalidOperationException(contract.Limitation);
        if (contract.SourceBarMinutes == 1) return policy;
        var factor = (int)contract.SourceBarMinutes;
        int Scale(int value, int minimum = 1) => Math.Max(minimum, (int)Math.Ceiling((double)value / factor));
        var fast = Scale(policy.EmaFastPeriod, 2);
        var slow = Math.Max(fast + 1, Scale(policy.EmaSlowPeriod, 3));
        return policy with
        {
            Version = $"{policy.Version}.replay-native-5m.1",
            PivotLeft = Scale(policy.PivotLeft),
            PivotRight = Scale(policy.PivotRight),
            Minimum1mBars = Scale(policy.Minimum1mBars, 6),
            StopReentryCooldownBars = Scale(policy.StopReentryCooldownBars),
            ReactionWindowBars = Scale(policy.ReactionWindowBars),
            EpisodeExitBars = Scale(policy.EpisodeExitBars),
            AtrPeriod = Scale(policy.AtrPeriod, 3),
            EmaFastPeriod = fast,
            EmaSlowPeriod = slow,
            TrendSlopeLookbackBars = Scale(policy.TrendSlopeLookbackBars),
            EfficiencyLookbackBars = Scale(policy.EfficiencyLookbackBars, 4),
            RelativeVolumeLookbackBars = Scale(policy.RelativeVolumeLookbackBars, 4),
            RegimeVolatilityLookbackBars = Scale(policy.RegimeVolatilityLookbackBars, 4),
            TrendStateHoldBars = Scale(policy.TrendStateHoldBars),
            StructureDirectionAtrScaleBars = Scale(policy.StructureDirectionAtrScaleBars)
        };
    }
}
