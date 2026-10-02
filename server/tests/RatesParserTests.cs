using Astra.Server.Application.Rates;
using Xunit;

namespace Astra.Server.Tests;

public sealed class RatesParserTests
{
    const string YahooTnx = """
        {"chart":{"result":[{"meta":{"currency":"USD","symbol":"^TNX","regularMarketPrice":5.237,"chartPreviousClose":5.293,"previousClose":5.293,"regularMarketTime":1790881194},
        "timestamp":[1790881080,1790881140],"indicators":{"quote":[{"close":[5.2389998,5.2369999]}]}}],"error":null}}
        """;

    [Fact]
    public void Yahoo_정상_응답은_현재가와_전일종가_시각을_읽는다()
    {
        var quote = YahooChartParser.Parse(YahooTnx, TreasuryTenor.Y10, "^TNX", "yahoo:^TNX");

        Assert.Equal(TreasuryTenor.Y10, quote.Tenor);
        Assert.Equal(5.237, quote.Value);
        Assert.Equal(5.293, quote.PreviousClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790881194), quote.AsOf);
        Assert.Equal("yahoo:^TNX", quote.Source);
    }

    [Fact]
    public void Yahoo_ETF_응답은_가격과_전일종가를_같은_구조로_읽는다()
    {
        const string json = """
            {"chart":{"result":[{"meta":{"currency":"USD","symbol":"IEF","instrumentType":"ETF","regularMarketPrice":89.3,"chartPreviousClose":89.0031,"previousClose":89.0031,"regularMarketTime":1790884801},
            "timestamp":[1790884740,1790884800],"indicators":{"quote":[{"close":[89.28,89.3]}]}}],"error":null}}
            """;
        var proxy = new EtfProxyDefinition(TreasuryTenor.Y10, "IEF", 7.5);

        var quote = YahooChartParser.ParseEtf(json, proxy, "yahoo:IEF");

        Assert.Equal(TreasuryTenor.Y10, quote.Tenor);
        Assert.Equal("IEF", quote.Symbol);
        Assert.Equal(89.3, quote.Price);
        Assert.Equal(89.0031, quote.PreviousClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790884801), quote.AsOf);
        Assert.Equal("yahoo:IEF", quote.Source);
    }

    [Fact]
    public void Yahoo_현재가가_없으면_마지막_유효_close와_그_시각을_쓴다()
    {
        const string json = """
            {"chart":{"result":[{"meta":{"symbol":"^TYX"},
            "timestamp":[100,200,300],"indicators":{"quote":[{"close":[5.60,5.61,null]}]}}],"error":null}}
            """;

        var quote = YahooChartParser.Parse(json, TreasuryTenor.Y30, "^TYX", "yahoo:^TYX");

        Assert.Equal(5.61, quote.Value);
        Assert.Null(quote.PreviousClose);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), quote.AsOf);
    }

    [Fact]
    public void Yahoo_오류_페이로드는_설명을_담은_예외다()
    {
        const string json = """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";

        var error = Assert.Throws<InvalidDataException>(() => YahooChartParser.Parse(json, TreasuryTenor.Y10, "^TNX", "yahoo:^TNX"));

        Assert.Contains("delisted", error.Message);
    }

    [Theory]
    [InlineData("""{"chart":{"result":[{"meta":{"symbol":"^TNX"},"timestamp":[],"indicators":{"quote":[{"close":[]}]}}]}}""")]
    [InlineData("""{"chart":{"result":[]}}""")]
    [InlineData("""{"quote":{}}""")]
    [InlineData("""{"chart":{"result":[{"meta":{"symbol":"^TNX","regularMarketPrice":-1,"regularMarketTime":5}}]}}""")]
    public void Yahoo_결측_응답은_FormatException이다(string json)
    {
        Assert.Throws<FormatException>(() => YahooChartParser.Parse(json, TreasuryTenor.Y10, "^TNX", "yahoo:^TNX"));
    }

    [Fact]
    public void Yahoo_깨진_JSON은_JsonException이다()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => YahooChartParser.Parse("{\"chart\":", TreasuryTenor.Y10, "^TNX", "yahoo:^TNX"));
    }

    [Fact]
    public void FRED_CSV는_결측과_빈_값을_건너뛰고_날짜순으로_정렬한다()
    {
        const string csv = "observation_date,DGS2\n2026-09-30,4.88\n2026-09-29,4.89\n2026-09-28,.\n2026-09-27,\n2026-09-26,abc\nbad-date,4.1\n";

        var points = FredCsvParser.Parse(csv, "DGS2");

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateOnly(2026, 9, 29), points[0].Date);
        Assert.Equal(4.89, points[0].Value);
        Assert.Equal(new DateOnly(2026, 9, 30), points[1].Date);
        Assert.Equal(4.88, points[1].Value);
    }

    [Fact]
    public void FRED_CSV는_여러_열_중_요청한_시리즈만_읽고_BOM과_CRLF를_허용한다()
    {
        const string csv = "\uFEFFobservation_date,DGS2,DGS10,DGS30\r\n2026-09-29,4.89,5.26,5.59\r\n2026-09-30,4.88,5.29,5.64\r\n";

        var points = FredCsvParser.Parse(csv, "DGS30");

        Assert.Equal([5.59, 5.64], points.Select(x => x.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>blocked</html>")]
    [InlineData("observation_date,DGS10\n2026-09-30,5.29\n")]
    public void FRED_헤더가_맞지_않으면_FormatException이다(string csv)
    {
        Assert.Throws<FormatException>(() => FredCsvParser.Parse(csv, "DGS2"));
    }
}
