using Astra.Server.Application;
using Astra.Server.Domain.News;
using Astra.Server.Infrastructure;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>v2c 프롬프트 리소스 로드·치환 (#171)</summary>
public sealed class OllamaNewsClassifierTests
{
    [Fact]
    public void PromptVersionIsV2c()
    {
        Assert.Equal("v2c", OllamaNewsClassifier.PromptVersion);
        Assert.Equal(NewsPromptVersions.V2c, OllamaNewsClassifier.PromptVersion);
    }

    [Fact]
    public void BuildPromptLoadsTheV2cResourceAndFillsPlaceholders()
    {
        var request = new NewsClassificationRequest("리야드 조기 경보 발령", "본문 내용", ["NVDA", "F"]);

        var prompt = OllamaNewsClassifier.BuildPrompt(request);

        Assert.Contains("NOW CLASSIFY", prompt);
        Assert.Contains("Feed tickers: NVDA, F", prompt);
        Assert.Contains("Title: 리야드 조기 경보 발령", prompt);
        Assert.Contains("Body: 본문 내용", prompt);
        Assert.DoesNotContain("{tickers}", prompt);
        Assert.DoesNotContain("{title}", prompt);
        Assert.DoesNotContain("{body}", prompt);
    }

    [Fact]
    public void BuildPromptUsesNoneWhenFeedHasNoTickers()
    {
        var request = new NewsClassificationRequest("제목", "본문", []);

        var prompt = OllamaNewsClassifier.BuildPrompt(request);

        Assert.Contains("Feed tickers: (none)", prompt);
    }

    [Fact]
    public void BuildPromptIncludesTheAlertCancelledFewShotPair()
    {
        var prompt = OllamaNewsClassifier.BuildPrompt(new NewsClassificationRequest("", "", []));

        Assert.Contains("리야드 인근 지역에 조기 경보 발령", prompt);
        Assert.Contains("리야드 인근 조기 경보 해제", prompt);
    }

    [Fact]
    public void CompactPromptRetainsTickerMacroAndImpactSemantics()
    {
        var prompt = OllamaNewsClassifier.BuildPrompt(new NewsClassificationRequest("보도에 따르면 금리 인상", "", ["NVDA"]));
        Assert.Contains("copy every ticker exactly", prompt);
        Assert.Contains("macro, geopolitics, rates, oil, indices and market news use [\"MARKET\"]", prompt);
        Assert.Contains("impact must contain every symbol", prompt);
        Assert.Contains("strength is 1-5", prompt);
    }

}
