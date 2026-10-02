namespace Astra.Server.Application.ScoreCore;

/// <summary>`ScoreCore` 설정 섹션. Enabled 기본 false이며 false면 shadow 수집을 하지 않는다(§8 1단계, #309).</summary>
public sealed class ScoreCoreOptions
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 5;
    public int MaxTargets { get; set; } = 50;
    public int LookbackHours { get; set; } = 48;
    public int RetentionDays { get; set; } = 30;

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(IntervalMinutes, 1, 1440));
    public TimeSpan Lookback => TimeSpan.FromHours(Math.Clamp(LookbackHours, 1, 24 * 14));
    public int TargetLimit => Math.Clamp(MaxTargets, 1, 500);
    public int Retention => Math.Clamp(RetentionDays, 1, 3650);
}
