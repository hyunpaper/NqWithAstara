using System.Collections.Immutable;

namespace Astra.Server.Domain.Confluence;

/// <summary>
/// K4 측정 파이프라인 상수 (C5, #169). 어떤 값도 성과로 탐색하지 않았고 미검증 기본값이다.
/// </summary>
public sealed record MeasurementPolicy
{
    /// <summary>"신호 발생"으로 세는 |score| 하한. 미검증 임계이며 성과 탐색으로 고른 값이 아니다(C5).</summary>
    public double SignalThreshold { get; init; } = .3;

    /// <summary>이 표본 수 미만이면 status는 unverified로 남고 가중치는 1.0이다(C5).</summary>
    public int MinimumSample { get; init; } = 50;

    /// <summary>Benjamini-Hochberg의 q (기법 10개 동시 검정, C5).</summary>
    public double FalseDiscoveryRate { get; init; } = .05;

    /// <summary>왕복 수수료(%)</summary>
    public double RoundTripFeePercent { get; init; } = .2;

    /// <summary>저장 호가가 없어 기대값에서 빼는 스프레드 기본값(%). 관측된 스프레드가 아니다(C5).</summary>
    public double DefaultSpreadPercent { get; init; } = .01;

    /// <summary>측정 지평(봉 수). 전부 보고하고 가중치는 그중 하나로만 만든다(C5).</summary>
    public ImmutableArray<int> Horizons { get; init; } = [5, 10, 20];

    public int DefaultHorizonBars { get; init; } = 10;

    /// <summary>워크포워드 측정 창(주).</summary>
    public int MeasurementWeeks { get; init; } = 4;

    /// <summary>워크포워드 적용 창(주).</summary>
    public int ApplyWeeks { get; init; } = 1;

    public static readonly MeasurementPolicy Default = new();

    /// <summary>기대값에서 빼는 왕복 비용(비율).</summary>
    public double CostFraction => (RoundTripFeePercent + DefaultSpreadPercent) / 100;
}

/// <summary>측정 4주 → 적용 1주 워크포워드 창 (C5, #169). 날짜 폴더 기준이며 경계는 양끝 포함이다.</summary>
public sealed record MeasurementWindow(DateOnly From, DateOnly To, DateOnly ApplyFrom, DateOnly ApplyTo)
{
    public bool Contains(DateOnly day) => day >= From && day <= To;

    public bool Applies(DateOnly day) => day >= ApplyFrom && day <= ApplyTo;
}

/// <summary>워크포워드 창 계산 (C5, #169). 적용 주 시작일 이전 4주만 측정에 쓴다.</summary>
public static class ConfluenceWalkForward
{
    public static MeasurementWindow ForApplyWeek(DateOnly applyFrom, MeasurementPolicy? policy = null)
    {
        var settings = policy ?? MeasurementPolicy.Default;
        if (settings.MeasurementWeeks < 1) throw new ArgumentOutOfRangeException(nameof(policy));
        if (settings.ApplyWeeks < 1) throw new ArgumentOutOfRangeException(nameof(policy));
        return new MeasurementWindow(
            applyFrom.AddDays(-7 * settings.MeasurementWeeks),
            applyFrom.AddDays(-1),
            applyFrom,
            applyFrom.AddDays(7 * settings.ApplyWeeks - 1));
    }

    /// <summary>날짜 폴더 이름 목록에서 측정 창 안의 날짜만 오름차순으로 고른다.</summary>
    public static ImmutableArray<DateOnly> DaysInWindow(IEnumerable<string> days, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(days);
        var result = ImmutableArray.CreateBuilder<DateOnly>();
        foreach (var day in days)
            if (DateOnly.TryParseExact(day, "yyyy-MM-dd", out var parsed) && parsed >= from && parsed <= to)
                result.Add(parsed);
        result.Sort();
        return result.ToImmutable();
    }
}
