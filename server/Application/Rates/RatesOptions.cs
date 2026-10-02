namespace Astra.Server.Application.Rates;

/// <summary>`Rates` 설정 섹션. Enabled 기본 false이며 false면 외부 금리 출처를 호출하지 않는다(#316).</summary>
public sealed class RatesOptions
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 60;
    public int OffHoursIntervalSeconds { get; set; } = 600;
    public int RequestSpacingMs { get; set; } = 1000;
    public int StaleAfterSeconds { get; set; } = 900;
    public int RetentionDays { get; set; } = 90;
    public int DailyLookbackDays { get; set; } = 30;
    public int MaxBackoffMinutes { get; set; } = 15;
    public double DirectionMinBp { get; set; } = 2;
    public double BaselineGapWarnBp { get; set; } = 5;
    public string YahooChartUrl { get; set; } = "https://query1.finance.yahoo.com/v8/finance/chart/";
    public string FredCsvUrl { get; set; } = "https://fred.stlouisfed.org/graph/fredgraph.csv";
    public string UserAgent { get; set; } = "Astra/1.0 (+https://github.com/hyunpaper/NqWithAstara)";

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(IntervalSeconds, 15, 3600));
    public TimeSpan OffHoursInterval => TimeSpan.FromSeconds(Math.Clamp(OffHoursIntervalSeconds, 60, 7200));
    public TimeSpan RequestSpacing => TimeSpan.FromMilliseconds(Math.Clamp(RequestSpacingMs, 0, 10_000));
    public TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Clamp(StaleAfterSeconds, 60, 86_400));
    public TimeSpan MaxBackoff => TimeSpan.FromMinutes(Math.Clamp(MaxBackoffMinutes, 1, 240));
    public int Retention => Math.Clamp(RetentionDays, 1, 3650);
    public int DailyLookback => Math.Clamp(DailyLookbackDays, 7, 365);
}
