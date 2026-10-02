using Astra.Server.Application;

namespace Astra.Server;

/// <summary>호스트 기동 완료 후 자동 시작을 1회 수행하는 어댑터 (#324).</summary>
public sealed class MonitorAutoStartLifetime(MonitorAutoStartService autoStart, IHostApplicationLifetime lifetime, ILogger<MonitorAutoStartLifetime> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = RunAsync(lifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var result = await autoStart.RunOnceAsync(ct);
            if (result.Outcome == MonitorAutoStartOutcome.Started) log.LogInformation("모니터링을 자동 시작했습니다 (Monitor:AutoStart).");
            else if (result.Warning is not null) log.LogWarning("{Message}", result.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogWarning(e, "모니터링 자동 시작 중 예기치 않은 오류"); }
    }
}
