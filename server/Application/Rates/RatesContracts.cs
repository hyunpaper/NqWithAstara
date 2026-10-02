namespace Astra.Server.Application.Rates;

public enum TreasuryTenor { Y2, Y10, Y30 }

public static class TreasuryTenors
{
    public static readonly TreasuryTenor[] All = [TreasuryTenor.Y2, TreasuryTenor.Y10, TreasuryTenor.Y30];

    public static string Key(this TreasuryTenor tenor) => tenor switch
    {
        TreasuryTenor.Y2 => "2Y",
        TreasuryTenor.Y10 => "10Y",
        TreasuryTenor.Y30 => "30Y",
        _ => throw new ArgumentOutOfRangeException(nameof(tenor)),
    };

    public static string Label(this TreasuryTenor tenor) => tenor switch
    {
        TreasuryTenor.Y2 => "미국채 2년",
        TreasuryTenor.Y10 => "미국채 10년",
        TreasuryTenor.Y30 => "미국채 30년",
        _ => throw new ArgumentOutOfRangeException(nameof(tenor)),
    };

    public static string FredSeries(this TreasuryTenor tenor) => tenor switch
    {
        TreasuryTenor.Y2 => "DGS2",
        TreasuryTenor.Y10 => "DGS10",
        TreasuryTenor.Y30 => "DGS30",
        _ => throw new ArgumentOutOfRangeException(nameof(tenor)),
    };

    public static TreasuryTenor? Parse(string? key) => key?.Trim().ToUpperInvariant() switch
    {
        "2Y" => TreasuryTenor.Y2,
        "10Y" => TreasuryTenor.Y10,
        "30Y" => TreasuryTenor.Y30,
        _ => null,
    };
}

/// <summary>장중 출처가 돌려준 한 만기의 최신 호가. PreviousClose는 출처가 제공한 전일 종가다.</summary>
public sealed record IntradayRateQuote(TreasuryTenor Tenor, string Symbol, double Value, double? PreviousClose,
    DateTimeOffset AsOf, string Source);

/// <summary>일별 공식 출처(FRED)의 한 관측치. 휴일·결측은 호출자가 걸러 Value가 항상 유효하다.</summary>
public sealed record DailyRatePoint(DateOnly Date, double Value);

public sealed record DailyRateSeries(TreasuryTenor Tenor, string Series, IReadOnlyList<DailyRatePoint> Points,
    DateTimeOffset FetchedAt, string Source)
{
    public DailyRatePoint? Latest => Points.Count > 0 ? Points[^1] : null;
    public DailyRatePoint? Previous => Points.Count > 1 ? Points[^2] : null;
}

/// <summary>`App_Data/rates/<yyyy-MM-dd>.jsonl` 한 줄. At은 수집 시각, AsOf는 출처가 밝힌 데이터 시각이다.</summary>
public sealed record RateObservation(DateTimeOffset At, string Tenor, double Value, string Source,
    DateTimeOffset AsOf, double? PreviousClose, string Kind);

public static class RateObservationKinds
{
    public const string Intraday = "intraday";
    public const string Daily = "daily";
    public const string Etf = "etf";
}

/// <summary>만기별 국채 ETF 대리변수 정의. Duration은 가격 수익률을 bp로 환산하는 근사 듀레이션(년)이다(#325).</summary>
public sealed record EtfProxyDefinition(TreasuryTenor Tenor, string Symbol, double Duration);

/// <summary>ETF 대리변수의 최신 가격. PreviousClose는 출처가 제공한 전일 종가다(#325).</summary>
public sealed record EtfProxyQuote(TreasuryTenor Tenor, string Symbol, double Price, double? PreviousClose,
    DateTimeOffset AsOf, string Source);

public interface IIntradayRateSource
{
    bool Supports(TreasuryTenor tenor);
    Task<IntradayRateQuote> FetchAsync(TreasuryTenor tenor, CancellationToken ct);
    Task<EtfProxyQuote> FetchEtfAsync(EtfProxyDefinition proxy, CancellationToken ct);
}

public interface IDailyRateSource
{
    Task<DailyRateSeries> FetchAsync(TreasuryTenor tenor, DateOnly since, CancellationToken ct);
}

public interface IRateObservationStore
{
    Task AppendAsync(IReadOnlyCollection<RateObservation> observations, CancellationToken ct);
    Task<IReadOnlyList<RateObservation>> ReadDayAsync(DateOnly day, CancellationToken ct);
    Task<int> PruneAsync(DateTimeOffset now, int retentionDays, CancellationToken ct);
}
