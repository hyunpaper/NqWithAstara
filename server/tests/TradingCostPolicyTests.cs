using Astra.Server;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Astra.Server.Tests;

public sealed class TradingCostPolicyTests
{
    [Fact]
    public void RoundTripFeeDefaultHasASingleSource()
    {
        Assert.Equal(TradingCostDefaults.RoundTripFeePercent, StructurePolicy.Default.RoundTripFeePercent);
        Assert.Equal(TradingCostDefaults.RoundTripFeePercent, MeasurementPolicy.Default.RoundTripFeePercent);
        Assert.Equal(TradingCostDefaults.RoundTripFeePercent, MarketRules.RoundTripFeePercent);
    }

    [Fact]
    public void StartupCheckWarnsWhenBoundStructurePolicyDiffersFromDefault()
    {
        var logger = new CaptureLogger<TradingCostPolicyCheckService>();

        _ = new TradingCostPolicyCheckService(
            StructurePolicy.Default with { RoundTripFeePercent = TradingCostDefaults.RoundTripFeePercent + .1 },
            logger);

        var warning = Assert.Single(logger.Messages);
        Assert.Contains("TRADING_COST_DEFAULT_MISMATCH", warning);
        Assert.Contains("StructurePolicy.RoundTripFeePercent", warning);
    }

    [Fact]
    public void StartupCheckIsQuietWhenAllDefaultsMatch()
    {
        var logger = new CaptureLogger<TradingCostPolicyCheckService>();

        _ = new TradingCostPolicyCheckService(StructurePolicy.Default, logger);

        Assert.Empty(logger.Messages);
    }

    sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }
}
