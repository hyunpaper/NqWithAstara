using Astra.Server.Application.Rates;

namespace Astra.Server.Infrastructure.Rates;

/// <summary>Yahoo Finance 차트 API에서 10Y(^TNX)·30Y(^TYX) 장중 금리와 국채 ETF 대리변수 가격을 받는다. 비공식 API라 언제든 바뀔 수 있다(#316, #325).</summary>
public sealed class YahooChartRateSource(HttpClient http, RatesOptions options) : IIntradayRateSource
{
    public const string SourceName = "yahoo";
    static readonly Dictionary<TreasuryTenor, string> Symbols = new()
    {
        [TreasuryTenor.Y10] = "^TNX",
        [TreasuryTenor.Y30] = "^TYX",
    };

    public bool Supports(TreasuryTenor tenor) => Symbols.ContainsKey(tenor);

    public async Task<IntradayRateQuote> FetchAsync(TreasuryTenor tenor, CancellationToken ct)
    {
        if (!Symbols.TryGetValue(tenor, out var symbol))
            throw new NotSupportedException($"{tenor.Key()}는 Yahoo 실시간 지수가 없습니다.");
        return YahooChartParser.Parse(await GetChartAsync(symbol, ct), tenor, symbol, $"{SourceName}:{symbol}");
    }

    public async Task<EtfProxyQuote> FetchEtfAsync(EtfProxyDefinition proxy, CancellationToken ct)
        => YahooChartParser.ParseEtf(await GetChartAsync(proxy.Symbol, ct), proxy, $"{SourceName}:{proxy.Symbol}");

    async Task<string> GetChartAsync(string symbol, CancellationToken ct)
    {
        var url = $"{options.YahooChartUrl.TrimEnd('/')}/{Uri.EscapeDataString(symbol)}?interval=1m&range=1d";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
