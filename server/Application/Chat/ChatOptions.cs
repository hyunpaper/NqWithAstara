namespace Astra.Server.Application.Chat;

/// <summary>`Chat` 설정 섹션(#338). OllamaUrl이 비면 News.OllamaUrl을 따른다.</summary>
public sealed class ChatOptions
{
    public bool Enabled { get; set; } = true;
    public string? OllamaUrl { get; set; }
    public string? DefaultModel { get; set; }
    public int MaxTurns { get; set; } = 8;
    public int HistoryCharBudget { get; set; } = 5000;
    public int MaxMessageChars { get; set; } = 2000;
    public int MaxPredictTokens { get; set; } = 1024;
    public int TimeoutSeconds { get; set; } = 240;
    public int CheckTimeoutSeconds { get; set; } = 90;
    public int BusyWaitSeconds { get; set; } = 5;

    public string ResolveOllamaUrl(NewsOptions news)
        => string.IsNullOrWhiteSpace(OllamaUrl) ? news.OllamaUrl : OllamaUrl.Trim();
}
