using Astra.Server.Application.Opening;
using Astra.Server.Domain.Opening;

namespace Astra.Server;

/// <summary>개장 프로파일 워밍업 호스트 어댑터(#375). 기동 시 1회, 이후 개장 전(09:00~09:29 ET) 평일에 갱신한다. Enabled=false면 아무 것도 하지 않는다.</summary>
public sealed class OpeningProfileWarmupService(OpeningProfileWarmup warmup, OpeningScanPolicy policy, TimeProvider clock)
    : BackgroundService
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    static readonly TimeSpan PreOpenStart = new(9, 0, 0);
    static readonly TimeSpan PreOpenEnd = new(9, 30, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!policy.Enabled) return;
        await SafeWarmAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var et = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), NewYork);
            if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (et.TimeOfDay >= PreOpenStart && et.TimeOfDay < PreOpenEnd)
                await SafeWarmAsync(stoppingToken);
        }
    }

    async Task SafeWarmAsync(CancellationToken ct)
    {
        try { await warmup.WarmAsync(warmup.SessionDate(), ct); }
        catch (OperationCanceledException) { }
        catch { }
    }
}
