namespace Astra.Server.Domain;

/// <summary>
/// 완료 봉 세기 규칙의 단일 구현. #117 손절 쿨다운과 #111 재진입 태그가 같은 규칙을 쓴다 —
/// 청산이 일어난 봉을 0번째로 보고, 그 뒤로 트리거 봉까지 닫힌 완료 봉만 센다. 시계를 보지 않는다(§4).
/// </summary>
public static class BarCounting
{
    /// <summary>분 경계로 자른 봉 시작 시각.</summary>
    public static DateTimeOffset BarStart(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset);

    /// <summary>
    /// 청산 봉(0번째) 이후 트리거 봉까지의 완료 봉 수. 완료 봉 근거가 없으면 null이며 0으로 대체하지 않는다(§16A).
    /// </summary>
    public static int? CompletedBarsSince(DateTimeOffset exitedAt,
        IReadOnlyList<DateTimeOffset>? completedBarStarts, DateTimeOffset triggerBarStart)
    {
        if (completedBarStarts is not { Count: > 0 } bars) return null;
        var exitBar = BarStart(exitedAt);
        return bars.Count(x => x > exitBar && x <= triggerBarStart);
    }
}
