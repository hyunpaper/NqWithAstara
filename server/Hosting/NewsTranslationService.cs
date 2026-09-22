using Astra.Server.Application;

namespace Astra.Server;

/// <summary>뉴스 수집 주기와 분리해 Papago 번역 큐를 처리한다(#265).</summary>
public sealed class NewsTranslationService(NewsOptions options, NewsTranslationQueue queue) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => options.Enabled ? queue.RunAsync(stoppingToken) : Task.CompletedTask;
}
