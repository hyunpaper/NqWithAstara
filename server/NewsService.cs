using Astra.Server.Application;

namespace Astra.Server;

/// <summary>뉴스 폴링 호스트 어댑터(#151 §1). 수집 정책은 Application에 있다. Enabled=false면 아무 것도 하지 않는다.</summary>
public sealed class NewsService(NewsOptions options, NewsFeedService feed) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.PollSeconds)));
        do
        {
            await feed.PollAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
