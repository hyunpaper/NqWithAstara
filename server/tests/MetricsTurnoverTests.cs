using Astra.Server;
using Astra.Server.Application;
using Xunit;

public sealed class MetricsTurnoverTests
{
    static StockInfo Info(decimal? shares) =>
        new("AAPL", "테스트", "STOCK", "NASDAQ", true, "NORMAL", null, shares);

    static readonly DailyMetrics Daily = new(null, null, null, 250_000, null, null, null, null, null, 50);

    [Fact]
    public void TurnoverIsTodayVolumeOverSharesOutstanding()
        => Assert.Equal(25.0, MetricsQueryService.Turnover(Daily, Info(1_000_000m)));

    [Fact]
    public void TurnoverIsNullWhenSharesOrVolumeAreMissing()
    {
        Assert.Null(MetricsQueryService.Turnover(Daily, Info(null)));
        Assert.Null(MetricsQueryService.Turnover(Daily, Info(0m)));
        Assert.Null(MetricsQueryService.Turnover(Daily, null));
        Assert.Null(MetricsQueryService.Turnover(null, Info(1_000_000m)));
    }
}
