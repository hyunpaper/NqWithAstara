namespace Astra.Server.Application.Rates;

public sealed record EtfProxyState(EtfProxyDefinition Proxy, EtfProxyQuote? Quote, DateTimeOffset? FetchedAt,
    string? Error, int Failures, DateTimeOffset? RetryAt);

public sealed record RateEtfProxyDto(string Tenor, string Symbol, double Duration, double? Price, double? PreviousClose,
    double? ReturnPct, double? ImpliedChangeBp, double? RateChangeBp, string EtfDirection, string RateDirection,
    string Agreement, int DivergeRuns, DateTimeOffset? AsOf, DateTimeOffset? FetchedAt, string? Source, string? Reason);

/// <summary>국채 ETF 당일 수익률을 듀레이션으로 bp 환산해 실시간 금리 변화와 방향을 비교한다. 참고 지표이며 매매 판정에 쓰지 않는다(#325).</summary>
public static class EtfProxyDivergence
{
    public const string Agree = "agree";
    public const string Diverge = "diverge";
    public const string Unknown = "unknown";
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static RateEtfProxyDto Evaluate(RatesOptions options, EtfProxyState state, IntradayTenorState? rate, int divergeRuns)
    {
        var proxy = state.Proxy;
        var key = proxy.Tenor.Key();
        if (state.Quote is null)
            return Empty(key, proxy, state, divergeRuns,
                state.Error is null ? "아직 수집하지 않았습니다." : $"ETF 가격 수집 실패: {state.Error}");
        var quote = state.Quote;
        if (quote.PreviousClose is not { } previous || previous <= 0)
            return Empty(key, proxy, state, divergeRuns, "ETF 전일 종가가 없어 당일 수익률을 계산할 수 없습니다.");
        var returnPct = Math.Round((quote.Price / previous - 1) * 100, 3);
        var impliedBp = Math.Round(-returnPct / proxy.Duration * 100, 1);
        var etfDirection = Direction(impliedBp, options.DirectionMinBp);
        var rateChange = rate?.Quote is { PreviousClose: { } close } rateQuote ? RatesSnapshotBuilder.Bp(rateQuote.Value - close) : (double?)null;
        if (rate?.Quote is null || rateChange is null)
            return new RateEtfProxyDto(key, proxy.Symbol, proxy.Duration, quote.Price, previous, returnPct, impliedBp, null,
                etfDirection, RateDirections.Unknown, Unknown, divergeRuns, quote.AsOf, state.FetchedAt, quote.Source,
                "실시간 금리가 없어 방향을 비교할 수 없습니다.");
        var etfDate = TradingDate(quote.AsOf);
        var rateDate = TradingDate(rate.Quote.AsOf);
        if (etfDate != rateDate)
            return new RateEtfProxyDto(key, proxy.Symbol, proxy.Duration, quote.Price, previous, returnPct, impliedBp, rateChange,
                etfDirection, Direction(rateChange, options.DirectionMinBp), Unknown, divergeRuns, quote.AsOf, state.FetchedAt,
                quote.Source, $"ETF 데이터 거래일({etfDate:yyyy-MM-dd})과 금리 데이터 거래일({rateDate:yyyy-MM-dd})이 다릅니다.");
        var rateDirection = Direction(rateChange, options.DirectionMinBp);
        var agreement = etfDirection != RateDirections.Flat && rateDirection != RateDirections.Flat && etfDirection != rateDirection
            ? Diverge
            : Agree;
        return new RateEtfProxyDto(key, proxy.Symbol, proxy.Duration, quote.Price, previous, returnPct, impliedBp, rateChange,
            etfDirection, rateDirection, agreement, divergeRuns, quote.AsOf, state.FetchedAt, quote.Source, null);
    }

    public static string DivergeWarning(RateEtfProxyDto dto)
        => $"{dto.Tenor} ETF {dto.Symbol} 당일 수익률({dto.ReturnPct:+0.00;-0.00}% ≈ 금리 {dto.ImpliedChangeBp:+0.0;-0.0}bp)이 실시간 금리 변화({dto.RateChangeBp:+0.0;-0.0}bp)와 방향이 어긋납니다.";

    public static string PersistentWarning(RateEtfProxyDto dto)
        => $"{dto.Tenor} ETF {dto.Symbol} 괴리가 {dto.DivergeRuns}회 연속 이어집니다.";

    static RateEtfProxyDto Empty(string key, EtfProxyDefinition proxy, EtfProxyState state, int runs, string reason)
        => new(key, proxy.Symbol, proxy.Duration, state.Quote?.Price, state.Quote?.PreviousClose, null, null, null,
            RateDirections.Unknown, RateDirections.Unknown, Unknown, runs, state.Quote?.AsOf, state.FetchedAt,
            state.Quote?.Source, reason);

    static DateOnly TradingDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, NewYork).DateTime);

    static string Direction(double? changeBp, double minBp) => changeBp switch
    {
        null => RateDirections.Unknown,
        var x when x >= minBp => RateDirections.Up,
        var x when x <= -minBp => RateDirections.Down,
        _ => RateDirections.Flat,
    };
}
