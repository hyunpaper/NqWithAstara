using Astra.Server.Domain.News;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>뉴스 매칭·감쇠·분류 파싱 Domain 로직 (#151)</summary>
public sealed class NewsDomainTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    static NewsArticle Article(string title, string summary = "", params string[] tickers)
        => new("1", title, summary, "reuters", Now, tickers);

    static readonly NewsWatchSymbol[] Watchlist =
    [
        new("INTC", "Intel"), new("F", "Ford Motor"), new("NVDA", "NVIDIA"),
    ];

    [Fact]
    public void TickerTagWinsOverText()
    {
        var match = NewsMatcher.Match(Article("아무 제목", "", "intc"), Watchlist);

        Assert.Equal(["INTC"], match.Symbols);
        Assert.True(match.Matched);
    }

    [Fact]
    public void CashTagInTitleMatches()
    {
        var match = NewsMatcher.Match(Article("$NVDA 실적 발표"), Watchlist);

        Assert.Equal(["NVDA"], match.Symbols);
    }

    [Fact]
    public void CashTagDoesNotMatchLongerSymbol()
    {
        var match = NewsMatcher.Match(Article("$FDX 배송 지연"), Watchlist);

        Assert.Empty(match.Symbols);
    }

    [Fact]
    public void KoreanAliasInTitleMatches()
    {
        var match = NewsMatcher.Match(Article("포드, 전기차 생산 목표 하향"), Watchlist);

        Assert.Equal(["F"], match.Symbols);
    }

    [Fact]
    public void EnglishNameInSummaryMatches()
    {
        var match = NewsMatcher.Match(Article("속보", "Intel raises guidance"), Watchlist);

        Assert.Equal(["INTC"], match.Symbols);
    }

    [Fact]
    public void MacroArticleMatchesNothing()
    {
        var match = NewsMatcher.Match(Article("우크라이나, 러시아 정유시설 타격"), Watchlist);

        Assert.False(match.Matched);
    }

    [Fact]
    public void EmptyWatchlistMatchesNothing()
    {
        Assert.Empty(NewsMatcher.Match(Article("$NVDA 급등"), []).Symbols);
    }

    [Fact]
    public void FreshPositiveArticleKeepsFullStrength()
    {
        var scores = NewsSentimentDecay.Score(
            [new NewsSentimentInput("NVDA", NewsSentiments.Positive, 4, Now)], Now, 30);

        Assert.Equal(4, Assert.Single(scores).Score, 3);
    }

    [Fact]
    public void OneHalfLifeHalvesTheScore()
    {
        var scores = NewsSentimentDecay.Score(
            [new NewsSentimentInput("NVDA", NewsSentiments.Negative, 4, Now.AddMinutes(-30))], Now, 30);

        Assert.Equal(-2, Assert.Single(scores).Score, 3);
    }

    [Fact]
    public void ScoreIsClampedToFive()
    {
        var inputs = Enumerable.Range(0, 10)
            .Select(_ => new NewsSentimentInput("NVDA", NewsSentiments.Positive, 5, Now))
            .ToArray();

        Assert.Equal(5, Assert.Single(NewsSentimentDecay.Score(inputs, Now, 30)).Score, 3);
    }

    [Fact]
    public void NeutralContributesNothing()
    {
        var scores = NewsSentimentDecay.Score(
            [new NewsSentimentInput("NVDA", NewsSentiments.Neutral, 5, Now)], Now, 30);

        Assert.Equal(0, Assert.Single(scores).Score, 3);
    }

    [Fact]
    public void MarketPseudoSymbolIsScoredLikeAnySymbol()
    {
        var scores = NewsSentimentDecay.Score(
            [new NewsSentimentInput(NewsSymbols.Market, NewsSentiments.Negative, 3, Now)], Now, 30);

        Assert.Equal(NewsSymbols.Market, Assert.Single(scores).Symbol);
    }

    [Fact]
    public void FutureTimestampIsNotAmplified()
    {
        var scores = NewsSentimentDecay.Score(
            [new NewsSentimentInput("NVDA", NewsSentiments.Positive, 2, Now.AddMinutes(30))], Now, 30);

        Assert.Equal(2, Assert.Single(scores).Score, 3);
    }

    [Fact]
    public void ParsesPlainJson()
    {
        var parsed = NewsClassificationParser.TryParse(
            """{"symbols":["NVDA"],"sentiment":"positive","strength":4,"reason":"실적 상향"}""");

        Assert.NotNull(parsed);
        Assert.Equal(["NVDA"], parsed!.Symbols);
        Assert.Equal(NewsSentiments.Positive, parsed.Sentiment);
        Assert.Equal(4, parsed.Strength);
        Assert.Equal("실적 상향", parsed.Reason);
    }

    [Fact]
    public void StripsCodeFence()
    {
        var parsed = NewsClassificationParser.TryParse(
            "```json\n{\"symbols\":[\"F\"],\"sentiment\":\"negative\",\"strength\":2,\"reason\":\"감산\"}\n```");

        Assert.Equal(["F"], parsed!.Symbols);
    }

    [Fact]
    public void StripsDollarPrefixAndUppercases()
    {
        var parsed = NewsClassificationParser.TryParse(
            """{"symbols":["$nvda"],"sentiment":"neutral","strength":1,"reason":""}""");

        Assert.Equal(["NVDA"], parsed!.Symbols);
    }

    [Fact]
    public void MissingSymbolsBecomesMarket()
    {
        var parsed = NewsClassificationParser.TryParse(
            """{"symbols":[],"sentiment":"negative","strength":3,"reason":"유가 급등"}""");

        Assert.Equal([NewsSymbols.Market], parsed!.Symbols);
    }

    [Fact]
    public void StrengthIsClampedToRange()
    {
        var high = NewsClassificationParser.TryParse("""{"symbols":["F"],"sentiment":"positive","strength":9}""");
        var low = NewsClassificationParser.TryParse("""{"symbols":["F"],"sentiment":"positive","strength":0}""");

        Assert.Equal(5, high!.Strength);
        Assert.Equal(1, low!.Strength);
    }

    [Fact]
    public void UnknownSentimentIsRejected()
    {
        Assert.Null(NewsClassificationParser.TryParse("""{"symbols":["F"],"sentiment":"bullish","strength":3}"""));
    }

    [Fact]
    public void MalformedPayloadIsRejected()
    {
        Assert.Null(NewsClassificationParser.TryParse("죄송합니다, 판단할 수 없습니다."));
        Assert.Null(NewsClassificationParser.TryParse(""));
        Assert.Null(NewsClassificationParser.TryParse(null));
    }

    [Fact]
    public void ReasonIsTruncated()
    {
        var parsed = NewsClassificationParser.TryParse(
            $$"""{"symbols":["F"],"sentiment":"positive","strength":3,"reason":"{{new string('가', 400)}}"}""");

        Assert.Equal(NewsClassificationParser.MaxReasonLength, parsed!.Reason.Length);
    }
}
