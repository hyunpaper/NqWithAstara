using Astra.Server;
using Xunit;

public sealed class SymbolAliasesTests
{
    [Theory]
    [InlineData("애플", "AAPL")]
    [InlineData("엔비디아", "NVDA")]
    [InlineData("존슨 앤 존슨", "JNJ")]
    [InlineData("버크셔", "BRK.B")]
    public void ResolvesKoreanNameToSymbol(string query, string expected)
    {
        Assert.Equal(expected, SymbolAliases.Resolve(query)[0]);
    }

    [Fact]
    public void PrefixMatchReturnsMultipleCandidates()
    {
        var symbols = SymbolAliases.Resolve("마이크로");
        Assert.Contains("MSFT", symbols);
        Assert.Contains("MSTR", symbols);
    }

    [Fact]
    public void ExactMatchRanksBeforePrefixMatch()
    {
        Assert.Equal("PEP", SymbolAliases.Resolve("펩시")[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("없는회사이름")]
    public void UnknownOrEmptyQueryReturnsNothing(string query)
    {
        Assert.Empty(SymbolAliases.Resolve(query));
    }
}
