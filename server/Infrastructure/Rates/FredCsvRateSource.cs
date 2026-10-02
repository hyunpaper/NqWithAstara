using System.Globalization;
using Astra.Server.Application.Rates;

namespace Astra.Server.Infrastructure.Rates;

/// <summary>FRED `fredgraph.csv`에서 DGS2·DGS10·DGS30 일별 공식값을 받는다. 하루 1회만 호출한다(#316).</summary>
public sealed class FredCsvRateSource(HttpClient http, RatesOptions options, TimeProvider clock) : IDailyRateSource
{
    public const string SourceName = "fred";

    public async Task<DailyRateSeries> FetchAsync(TreasuryTenor tenor, DateOnly since, CancellationToken ct)
    {
        var series = tenor.FredSeries();
        var url = $"{options.FredCsvUrl}?id={series}&cosd={since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/csv");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var csv = await response.Content.ReadAsStringAsync(ct);
        var points = FredCsvParser.Parse(csv, series);
        if (points.Count == 0) throw new InvalidDataException($"{series} 관측치가 없습니다.");
        return new DailyRateSeries(tenor, series, points, clock.GetUtcNow(), $"{SourceName}:{series}");
    }
}
