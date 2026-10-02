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
    public bool EtfEnabled { get; set; } = true;
    public int EtfIntervalSeconds { get; set; } = 300;
    public int EtfDivergenceWarnRuns { get; set; } = 3;
    public EtfProxyOptions[]? EtfProxies { get; set; }
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
    public TimeSpan EtfInterval => TimeSpan.FromSeconds(Math.Clamp(EtfIntervalSeconds, 60, 7200));
    public int EtfDivergenceRuns => Math.Clamp(EtfDivergenceWarnRuns, 1, 100);

    public static readonly EtfProxyDefinition[] DefaultEtfProxies =
    [
        new(TreasuryTenor.Y2, "SHY", 1.9),
        new(TreasuryTenor.Y10, "IEF", 7.5),
        new(TreasuryTenor.Y30, "TLT", 16.5),
    ];

    public IReadOnlyList<EtfProxyDefinition> ResolvedEtfProxies()
    {
        if (!EtfEnabled) return [];
        if (EtfProxies is not { Length: > 0 } configured) return DefaultEtfProxies;
        var list = new List<EtfProxyDefinition>(configured.Length);
        foreach (var item in configured)
        {
            if (TreasuryTenors.Parse(item.Tenor) is not { } tenor || string.IsNullOrWhiteSpace(item.Symbol)) continue;
            var duration = double.IsFinite(item.Duration) && item.Duration > 0 ? item.Duration
                : DefaultEtfProxies.First(x => x.Tenor == tenor).Duration;
            var symbol = item.Symbol.Trim().ToUpperInvariant();
            if (list.Any(x => x.Symbol == symbol)) continue;
            list.Add(new EtfProxyDefinition(tenor, symbol, duration));
        }
        return list;
    }
}

/// <summary>`Rates:EtfProxies[]` 항목. Duration이 0이면 같은 만기의 기본 듀레이션을 쓴다(#325).</summary>
public sealed class EtfProxyOptions
{
    public string? Tenor { get; set; }
    public string? Symbol { get; set; }
    public double Duration { get; set; }
}
