using Astra.Server.Application;

namespace Astra.Server.Infrastructure;

public sealed class NewsRelevanceAdjudicationService(NewsFeedService feed) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => feed.RunRelevanceAdjudicationWorkerAsync(stoppingToken);
}
