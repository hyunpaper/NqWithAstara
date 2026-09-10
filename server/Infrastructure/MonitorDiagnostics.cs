using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

public sealed class MonitorDiagnostics(ILogger<MonitorDiagnostics> logger) : IMonitorDiagnostics
{
    public void PollFailed(string scope, Exception exception)
        => logger.LogWarning(exception, "Monitor polling failed in {Scope}: {Type}", scope, exception.GetType().Name);

    public void MarketDataFailed(string symbol, string operation, Exception exception)
        => logger.LogWarning(exception, "Market data {Operation} failed for {Symbol}: {Type}", operation, symbol, exception.GetType().Name);
}
