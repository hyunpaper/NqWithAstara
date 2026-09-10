using Astra.Server.Application;

namespace Astra.Server;

/// <summary>Host lifecycle adapter; polling policy lives in the Application layer.</summary>
public sealed class MonitorService(MonitorPollingService polling) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            if (polling.Running) await polling.PollAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
