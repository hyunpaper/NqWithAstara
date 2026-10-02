namespace Astra.Server.Application.Rates;

public sealed record RateDailyDto(double Value, DateOnly Date, double? Previous, DateOnly? PreviousDate,
    double? ChangeBp, string Source, DateTimeOffset FetchedAt);

public sealed record RateTenorDto(string Tenor, string Label, double? Value, double? ChangeBp, double? PreviousClose,
    DateTimeOffset? AsOf, DateTimeOffset? FetchedAt, string? Source, string Mode, string DelayStatus,
    string SessionStatus, int? DelaySeconds, RateDailyDto? Daily, string? Reason);

public sealed record RateSpreadDto(string Key, string Label, double? ValueBp, double? ChangeBp, string Mode,
    string? Reason);

public sealed record RateDirectionCheckDto(string Tenor, string IntradayDirection, string DailyBaselineDirection,
    double? ChangeBp, double? ChangeVsDailyBp, double? BaselineGapBp, string Agreement, string? Reason);

public sealed record RatesSnapshotDto(bool Enabled, string Status, DateTimeOffset AsOf, string IntradaySource,
    string DailySource, int RefreshSeconds, IReadOnlyList<RateTenorDto> Tenors, IReadOnlyList<RateSpreadDto> Spreads,
    IReadOnlyList<RateDirectionCheckDto> DirectionChecks, IReadOnlyList<RateEtfProxyDto> EtfProxies,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Limitations);

public sealed record RatesHealth(bool Enabled, string Status, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt,
    string? LastError, DateTimeOffset? LastDailyRefreshAt, int AppendedToday, IReadOnlyList<string> FailedSources,
    DateTimeOffset? LastPrunedAt, int PrunedFiles, IReadOnlyList<string> EtfDivergences);

public sealed record IntradayTenorState(bool Supported, IntradayRateQuote? Quote, DateTimeOffset? FetchedAt,
    string? Error, int Failures, DateTimeOffset? RetryAt);

public static class RateModes
{
    public const string Intraday = "intraday";
    public const string DailyOnly = "daily_only";
    public const string Mixed = "mixed";
    public const string Daily = "daily";
    public const string Unavailable = "unavailable";
}

public static class RateDirections
{
    public const string Up = "up";
    public const string Down = "down";
    public const string Flat = "flat";
    public const string Unknown = "unknown";
}

/// <summary>수집 상태를 `/api/rates` DTO로 조립한다. 매매 판정 입력이 아니라 표시·기록용 참고값이다(#316, #325).</summary>
public static class RatesSnapshotBuilder
{
    public const string IntradaySourceLabel = "Yahoo Finance 차트 API (비공식 · ^TNX·^TYX)";
    public const string DailySourceLabel = "FRED DGS2·DGS10·DGS30 (일별 공식값)";
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static RatesSnapshotDto Build(RatesOptions options, DateTimeOffset now, string status,
        IReadOnlyDictionary<TreasuryTenor, IntradayTenorState> intraday,
        IReadOnlyDictionary<TreasuryTenor, DailyRateSeries> daily)
        => Build(options, now, status, intraday, daily, [], new Dictionary<string, int>(StringComparer.Ordinal));

    public static RatesSnapshotDto Build(RatesOptions options, DateTimeOffset now, string status,
        IReadOnlyDictionary<TreasuryTenor, IntradayTenorState> intraday,
        IReadOnlyDictionary<TreasuryTenor, DailyRateSeries> daily,
        IReadOnlyList<EtfProxyState> etf, IReadOnlyDictionary<string, int> etfDivergeRuns)
    {
        var tenors = TreasuryTenors.All.Select(x => Tenor(options, now, x,
            intraday.TryGetValue(x, out var i) ? i : new IntradayTenorState(false, null, null, null, 0, null),
            daily.TryGetValue(x, out var d) ? d : null)).ToArray();
        var byKey = tenors.ToDictionary(x => x.Tenor, StringComparer.Ordinal);
        var spreads = new[]
        {
            Spread("2s10s", "10Y − 2Y", byKey["2Y"], byKey["10Y"]),
            Spread("10s30s", "30Y − 10Y", byKey["10Y"], byKey["30Y"]),
        };
        var checks = TreasuryTenors.All
            .Where(x => intraday.TryGetValue(x, out var state) && state.Supported)
            .Select(x => DirectionCheck(options, x, intraday[x], daily.TryGetValue(x, out var d) ? d : null))
            .ToArray();
        var warnings = new List<string>();
        foreach (var check in checks)
        {
            if (check.Agreement == "diverge")
                warnings.Add($"{check.Tenor} 실시간 변화 방향({Korean(check.IntradayDirection)})이 FRED 전일값 기준 방향({Korean(check.DailyBaselineDirection)})과 어긋납니다.");
            if (check.BaselineGapBp is { } gap && Math.Abs(gap) > options.BaselineGapWarnBp)
                warnings.Add($"{check.Tenor} 출처 전일 종가와 FRED 전일값이 {gap:+0.0;-0.0}bp 차이 납니다.");
        }
        foreach (var tenor in tenors.Where(x => x.DelayStatus == "stale" && x.FetchedAt is not null))
            warnings.Add($"{tenor.Tenor} 실시간 값이 {(int)(now - tenor.FetchedAt!.Value).TotalMinutes}분째 갱신되지 않았습니다.");
        var proxies = etf
            .OrderBy(x => Array.IndexOf(TreasuryTenors.All, x.Proxy.Tenor)).ThenBy(x => x.Proxy.Symbol, StringComparer.Ordinal)
            .Select(x => EtfProxyDivergence.Evaluate(options, x,
                intraday.TryGetValue(x.Proxy.Tenor, out var rate) ? rate : null,
                etfDivergeRuns.TryGetValue(x.Proxy.Symbol, out var runs) ? runs : 0))
            .ToArray();
        foreach (var proxy in proxies.Where(x => x.Agreement == EtfProxyDivergence.Diverge))
        {
            warnings.Add(EtfProxyDivergence.DivergeWarning(proxy));
            if (proxy.DivergeRuns >= options.EtfDivergenceRuns) warnings.Add(EtfProxyDivergence.PersistentWarning(proxy));
        }
        var limitations = new List<string>
        {
            "실시간 값은 비공식 Yahoo Finance 차트 API에서 가져오며 지연·결측이 있을 수 있습니다.",
            "2Y는 실시간 지수가 없어 FRED 전일 공식값만 표시합니다.",
            "상승=빨강·하락=초록은 주식 관점 표기이며, 금리는 매매 판정·진입 게이트에 쓰지 않습니다.",
            $"ETF 대리변수({string.Join("·", etf.Select(x => x.Proxy.Symbol).DefaultIfEmpty("없음"))}) 비교는 근사 듀레이션으로 bp를 환산한 참고 지표이며 매매 판정에 쓰지 않습니다.",
        };
        return new RatesSnapshotDto(options.Enabled, status, now, IntradaySourceLabel, DailySourceLabel,
            (int)options.Interval.TotalSeconds, tenors, spreads, checks, proxies, warnings, limitations);
    }

    static RateTenorDto Tenor(RatesOptions options, DateTimeOffset now, TreasuryTenor tenor, IntradayTenorState state,
        DailyRateSeries? series)
    {
        var dailyDto = series?.Latest is { } latest
            ? new RateDailyDto(latest.Value, latest.Date, series.Previous?.Value, series.Previous?.Date,
                series.Previous is { } previous ? Bp(latest.Value - previous.Value) : null, series.Source, series.FetchedAt)
            : null;
        var key = tenor.Key();
        var label = tenor.Label();
        if (!state.Supported)
        {
            return dailyDto is null
                ? new RateTenorDto(key, label, null, null, null, null, null, null, RateModes.Unavailable, "unavailable",
                    "unknown", null, null, "실시간 출처가 없고 FRED 일별값도 아직 받지 못했습니다.")
                : new RateTenorDto(key, label, dailyDto.Value, dailyDto.ChangeBp, dailyDto.Previous,
                    RatesCollector.DailyAsOf(dailyDto.Date),
                    dailyDto.FetchedAt, dailyDto.Source, RateModes.DailyOnly, "fresh", "closed", null, dailyDto,
                    "실시간 지수가 없어 FRED 전일 공식값(전일 대비 변화)만 표시합니다.");
        }
        if (state.Quote is null)
        {
            return new RateTenorDto(key, label, dailyDto?.Value, null, null, null, state.FetchedAt, dailyDto?.Source,
                dailyDto is null ? RateModes.Unavailable : RateModes.DailyOnly, "unavailable", "unknown", null, dailyDto,
                state.Error is null ? "아직 수집하지 않았습니다." : $"실시간 수집 실패: {state.Error}");
        }
        var delay = (int)Math.Max(0, (now - state.Quote.AsOf).TotalSeconds);
        var stale = state.FetchedAt is null || now - state.FetchedAt.Value > options.StaleAfter;
        var change = state.Quote.PreviousClose is { } close ? Bp(state.Quote.Value - close) : (double?)null;
        var session = TradingDate(state.Quote.AsOf) < TradingDate(now) ? "closed" : "open";
        return new RateTenorDto(key, label, state.Quote.Value, change, state.Quote.PreviousClose, state.Quote.AsOf,
            state.FetchedAt, state.Quote.Source, RateModes.Intraday, stale ? "stale" : "fresh", session, delay, dailyDto,
            stale ? (state.Error is null ? "마지막 수집 후 갱신이 없습니다." : $"갱신 실패가 이어집니다: {state.Error}") : null);
    }

    static RateSpreadDto Spread(string key, string label, RateTenorDto shorter, RateTenorDto longer)
    {
        if (shorter.Value is null || longer.Value is null)
            return new RateSpreadDto(key, label, null, null, RateModes.Unavailable, "두 만기 값이 모두 있어야 계산합니다.");
        var value = Bp(longer.Value.Value - shorter.Value.Value);
        var change = shorter.ChangeBp is { } a && longer.ChangeBp is { } b ? Math.Round(b - a, 1) : (double?)null;
        var mode = shorter.Mode == longer.Mode
            ? shorter.Mode == RateModes.DailyOnly ? RateModes.Daily : shorter.Mode
            : RateModes.Mixed;
        var reason = mode == RateModes.Mixed
            ? $"{longer.Tenor} {ModeKorean(longer.Mode)} − {shorter.Tenor} {ModeKorean(shorter.Mode)} 조합입니다."
            : null;
        return new RateSpreadDto(key, label, value, mode == RateModes.Mixed ? null : change, mode, reason);
    }

    static RateDirectionCheckDto DirectionCheck(RatesOptions options, TreasuryTenor tenor, IntradayTenorState state,
        DailyRateSeries? series)
    {
        var key = tenor.Key();
        if (state.Quote is null)
            return new RateDirectionCheckDto(key, RateDirections.Unknown, RateDirections.Unknown, null, null, null,
                "unknown", "실시간 값이 없습니다.");
        var change = state.Quote.PreviousClose is { } close ? Bp(state.Quote.Value - close) : (double?)null;
        var intradayDirection = Direction(change, options.DirectionMinBp);
        if (series?.Latest is not { } latest)
            return new RateDirectionCheckDto(key, intradayDirection, RateDirections.Unknown, change, null, null,
                "unknown", "FRED 일별값이 없습니다.");
        var age = TradingDate(state.Quote.AsOf).DayNumber - latest.Date.DayNumber;
        if (age < 1 || age > 4)
            return new RateDirectionCheckDto(key, intradayDirection, RateDirections.Unknown, change, null, null,
                "unknown", $"FRED 최신값({latest.Date:yyyy-MM-dd})이 직전 거래일이 아닙니다.");
        var changeVsDaily = Bp(state.Quote.Value - latest.Value);
        var dailyDirection = Direction(changeVsDaily, options.DirectionMinBp);
        var gap = state.Quote.PreviousClose is { } previous ? Bp(previous - latest.Value) : (double?)null;
        var agreement = intradayDirection == RateDirections.Unknown ? "unknown"
            : intradayDirection != RateDirections.Flat && dailyDirection != RateDirections.Flat &&
              intradayDirection != dailyDirection ? "diverge"
            : "agree";
        return new RateDirectionCheckDto(key, intradayDirection, dailyDirection, change, changeVsDaily, gap, agreement,
            null);
    }

    static DateOnly TradingDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, NewYork).DateTime);

    static string Direction(double? changeBp, double minBp) => changeBp switch
    {
        null => RateDirections.Unknown,
        var x when x >= minBp => RateDirections.Up,
        var x when x <= -minBp => RateDirections.Down,
        _ => RateDirections.Flat,
    };

    static string Korean(string direction) => direction switch
    {
        RateDirections.Up => "상승",
        RateDirections.Down => "하락",
        RateDirections.Flat => "보합",
        _ => "미상",
    };

    static string ModeKorean(string mode) => mode switch
    {
        RateModes.Intraday => "실시간",
        RateModes.DailyOnly or RateModes.Daily => "전일",
        _ => "미상",
    };

    public static double Bp(double percentagePoints) => Math.Round(percentagePoints * 100, 1);
}
