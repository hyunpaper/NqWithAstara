using Astra.Server.Application.Rates;

namespace Astra.Server.Infrastructure.Rates;

/// <summary>금리 수집 호스트 어댑터. Rates:Enabled=false면 아무 것도 하지 않고, 실패는 수집기 안에서 격리된다(#316).</summary>
public sealed class RatesCollectorService(RatesOptions options, RatesCollector collector, TimeProvider clock)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await collector.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { }
            try { await Task.Delay(collector.NextDelay(clock.GetUtcNow()), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
