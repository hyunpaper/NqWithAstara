using System.Globalization;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Opening;

public sealed record OpeningFirst5(int BarsSeen, int UpBars, int NewHighs);

public sealed record OpeningPremarket(double High, double Volume, bool AbovePremarketHigh);

/// <summary>스냅샷 평가 입력(#371 §2). 라이브 훅과 재생 명령이 같은 평가기를 공유하도록 원본 값만 담는다.</summary>
public sealed record OpeningScanInput(
    string Symbol,
    string Name,
    DateOnly SessionDate,
    DateTimeOffset SessionOpen,
    DateTimeOffset ObservedAt,
    IReadOnlyList<Candle> Bars,
    IReadOnlyList<Candle> All,
    IReadOnlyList<Candle>? Daily,
    IReadOnlyList<SessionVolumeProfile> Previous,
    double QuotePrice,
    DateTimeOffset QuoteAt,
    string QuoteStatus,
    OpeningScanPolicy Policy);

/// <summary>개장 스냅샷 1건의 모든 표시·기록 값(#371 §4.4). 전부 유한수 또는 null이다.</summary>
public sealed record OpeningScanSnapshot(
    string Symbol,
    string Name,
    DateOnly SessionDate,
    DateTimeOffset SessionOpen,
    DateTimeOffset ObservedAt,
    int ElapsedMinutes,
    DateTimeOffset LastBarStart,
    double LastBarClose,
    double QuotePrice,
    DateTimeOffset QuoteAt,
    string QuoteStatus,
    double CumulativeVolume,
    double? RvolNow,
    OpeningRvolWindow Rvol3,
    OpeningRvolWindow Rvol5,
    OpeningRvolWindow Rvol20,
    double? BaselineMean,
    double? BaselineMedian,
    int SampleCount,
    string VolumeStatus,
    double? PrevClose,
    string? PrevCloseSource,
    double? Open,
    bool OpenBarMissing,
    double? GapPercent,
    double? ChangeFromPrevClosePercent,
    double? ChangeFromOpenPercent,
    double? Vwap,
    bool? AboveVwap,
    OpeningFirst5? First5,
    double? OpeningRangeHigh5,
    bool? BrokeOpeningRange,
    OpeningPremarket? Premarket,
    string Grade,
    double Score,
    string[] Reasons,
    string[] Warnings);

/// <summary>§2·§3 순수 평가. 지표당 정의 1개 원칙에 따라 VWAP은 기존 <see cref="Indicators.VwapSeries"/>를 재사용한다.</summary>
public static class OpeningSnapshotEvaluator
{
    public const string GradeStrong = "STRONG";
    public const string GradeVolumeOnly = "VOLUME_ONLY";
    public const string GradePriceOnly = "PRICE_ONLY";
    public const string GradeWeak = "WEAK";

    public const string VolumeFull = "full";
    public const string VolumePartial = "partial";
    public const string VolumeInsufficient = "insufficient";

    public const string QuoteFresh = "fresh";
    public const string QuoteStale = "stale";
    public const string QuoteBarClose = "bar-close";

    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static OpeningScanSnapshot Evaluate(OpeningScanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var policy = input.Policy;
        var open = input.SessionOpen;
        var windowEnd = open.AddMinutes(policy.WindowMinutes);
        var bars = input.Bars.Where(x => x.Timestamp >= open && x.Timestamp < windowEnd).OrderBy(x => x.Timestamp).ToArray();
        var warnings = new List<string>();

        var last = bars[^1];
        var k = Math.Clamp((int)Math.Round((last.Timestamp.AddMinutes(1) - open).TotalMinutes, MidpointRounding.AwayFromZero), 1, policy.WindowMinutes);
        var cumulative = bars.Sum(x => (decimal)x.Volume);

        // #375: 같은 경과 분 k를 최근 3·5·20세션 평균과 각각 비교한다. 등급 기준은 5세션(없으면 3세션), 최소 GradeMinimumSessions.
        var window3 = OpeningRvol.Window(cumulative, k, input.Previous, policy.RvolShortSessions, policy.GradeMinimumSessions);
        var window5 = OpeningRvol.Window(cumulative, k, input.Previous, policy.RvolMediumSessions, policy.GradeMinimumSessions);
        var window20 = OpeningRvol.Window(cumulative, k, input.Previous, policy.LookbackSessions, policy.GradeMinimumSessions);
        var grade5 = OpeningRvol.Compute(cumulative, k, input.Previous, policy.RvolMediumSessions, policy.GradeMinimumSessions);
        var grade3 = OpeningRvol.Compute(cumulative, k, input.Previous, policy.RvolShortSessions, policy.GradeMinimumSessions);
        var rvol = grade5 ?? grade3;
        var sampleCount = window20.SampleCount;
        var volumeStatus = rvol is null ? VolumeInsufficient
            : sampleCount >= policy.SampleCountForFull ? VolumeFull
            : VolumePartial;

        var priceValid = !string.Equals(input.QuoteStatus, QuoteStale, StringComparison.Ordinal);
        var price = input.QuotePrice;

        var openBarMissing = bars[0].Timestamp != open;
        double? openPrice = openBarMissing ? null : bars[0].Open;
        if (openBarMissing) warnings.Add("OPEN_BAR_MISSING");

        var (prevClose, prevCloseSource) = ComputePrevClose(input);

        var vwapSeries = Astra.Server.Indicators.VwapSeries(bars);
        double? vwap = priceValid ? Round4(vwapSeries[^1]) : null;
        bool? aboveVwap = priceValid ? price > vwapSeries[^1] : null;

        double? gap = openPrice is { } o && o > 0 && prevClose is { } pc && pc > 0 ? Round2((o / pc - 1) * 100) : null;
        double? changePrev = priceValid && prevClose is { } pc2 && pc2 > 0 ? Round2((price / pc2 - 1) * 100) : null;
        double? changeOpen = priceValid && openPrice is { } o2 && o2 > 0 ? Round2((price / o2 - 1) * 100) : null;

        var first5 = First5(bars);

        double? orh5 = bars.Length >= 5 ? Round4(bars.Take(5).Max(x => x.High)) : null;
        bool? brokeOr = priceValid && orh5 is { } orh ? price > orh : null;

        var premarket = priceValid ? Premarket(input, price) : null;

        if (!priceValid) warnings.Add("QUOTE_STALE");

        var volumeStrong = rvol is { } r && r.Ratio >= policy.VolumeStrongRatio;
        var priceUp = changeOpen is { } co && co >= policy.PriceUpPercent && (!policy.RequireAboveVwap || aboveVwap == true);
        var grade = volumeStrong && priceUp ? GradeStrong
            : volumeStrong ? GradeVolumeOnly
            : priceUp ? GradePriceOnly
            : GradeWeak;

        var score = Score(rvol, changeOpen, aboveVwap, first5);
        var gradeBasisSessions = grade5 is not null ? policy.RvolMediumSessions : policy.RvolShortSessions;
        var reasons = Reasons(policy, rvol, gradeBasisSessions, window3, window5, window20, sampleCount, k,
            changeOpen, aboveVwap, first5, gap, prevCloseSource, orh5, brokeOr, premarket, priceValid);

        static OpeningRvolWindow Rounded(OpeningRvolWindow w) => w with
        {
            Ratio = w.Ratio is { } r ? Round2(r) : null,
            BaselineVolume = w.BaselineVolume is { } b ? Round2(b) : null,
        };

        return new OpeningScanSnapshot(
            input.Symbol, input.Name, input.SessionDate, open, input.ObservedAt, k,
            last.Timestamp, Round4(last.Close), Round4(price), input.QuoteAt, input.QuoteStatus,
            (double)cumulative, rvol is null ? null : Round2(rvol.Ratio),
            Rounded(window3), Rounded(window5), Rounded(window20),
            rvol is null ? null : Round2(rvol.BaselineMean), rvol is null ? null : Round2(rvol.BaselineMedian),
            sampleCount, volumeStatus,
            prevClose is null ? null : Round4(prevClose.Value), prevCloseSource,
            openPrice is null ? null : Round4(openPrice.Value), openBarMissing,
            gap, changePrev, changeOpen, vwap, aboveVwap,
            first5, orh5, brokeOr, premarket,
            grade, Round2(score), [.. reasons], [.. warnings]);
    }

    static (double? Close, string? Source) ComputePrevClose(OpeningScanInput input)
    {
        if (input.Daily is { Count: > 0 })
        {
            var day = input.Daily.Where(x => MarketRules.TradingDate(x.Timestamp) < input.SessionDate && double.IsFinite(x.Close) && x.Close > 0)
                .OrderBy(x => x.Timestamp).LastOrDefault();
            if (day is not null) return (day.Close, "daily");
        }
        var bar = input.All.Where(x => MarketRules.TradingDate(x.Timestamp) < input.SessionDate
                && TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Timestamp, NewYork).DateTime) < new TimeOnly(16, 0)
                && double.IsFinite(x.Close) && x.Close > 0)
            .OrderBy(x => x.Timestamp).LastOrDefault();
        return bar is null ? (null, null) : ((double?)bar.Close, "bars1m");
    }

    static OpeningFirst5 First5(IReadOnlyList<Candle> bars)
    {
        var seen = Math.Min(bars.Count, 5);
        var up = 0;
        var newHighs = 0;
        var runningHigh = double.NegativeInfinity;
        for (var i = 0; i < seen; i++)
        {
            var bar = bars[i];
            if (bar.Close > bar.Open) up++;
            if (i > 0 && bar.High > runningHigh) newHighs++;
            runningHigh = Math.Max(runningHigh, bar.High);
        }
        return new OpeningFirst5(seen, up, newHighs);
    }

    static OpeningPremarket? Premarket(OpeningScanInput input, double price)
    {
        var pre = input.All.Where(x => MarketRules.TradingDate(x.Timestamp) == input.SessionDate && x.Timestamp < input.SessionOpen && double.IsFinite(x.High)).ToArray();
        if (pre.Length == 0) return null;
        var high = pre.Max(x => x.High);
        return new OpeningPremarket(Round4(high), pre.Sum(x => x.Volume), price > high);
    }

    static double Score(OpeningRvolResult? rvol, double? changeOpen, bool? aboveVwap, OpeningFirst5? first5)
    {
        var volume = rvol is { } r ? Math.Min(r.Ratio, 5) / 5 * 50 : 0;
        var move = changeOpen is { } c ? Math.Clamp(c, -2, 3) / 3 * 30 : 0;
        var vwap = aboveVwap == true ? 10 : 0;
        var highs = first5 is { } f ? Math.Min(f.NewHighs, 4) / 4d * 10 : 0;
        return Math.Clamp(volume + move + vwap + highs, 0, 100);
    }

    static List<string> Reasons(OpeningScanPolicy policy, OpeningRvolResult? rvol, int gradeBasisSessions,
        OpeningRvolWindow window3, OpeningRvolWindow window5, OpeningRvolWindow window20, int sampleCount,
        int k, double? changeOpen, bool? aboveVwap, OpeningFirst5? first5, double? gap, string? prevCloseSource,
        double? orh5, bool? brokeOr, OpeningPremarket? premarket, bool priceValid)
    {
        var reasons = new List<string>();
        if (rvol is { } r)
            reasons.Add($"거래량 {r.Ratio.ToString("0.0", CultureInfo.InvariantCulture)}× ({gradeBasisSessions}세션 기준 · {k}분 경과 · {WindowDetail(window3, window5, window20)})");
        else
            reasons.Add($"거래량 기준 없음 ({sampleCount}/{policy.LookbackSessions}세션)");

        if (!priceValid)
        {
            reasons.Add("시세 지연 — 가격 지표 생략");
            return reasons;
        }

        if (changeOpen is { } co)
        {
            if (co >= policy.PriceUpPercent)
                reasons.Add($"시가 대비 {Signed(co)}% · {(aboveVwap == true ? "VWAP 위" : "VWAP 아래")}");
            else if (aboveVwap == false)
                reasons.Add("VWAP 아래");
            else
                reasons.Add($"시가 대비 {Signed(co)}% (기준 {policy.PriceUpPercent.ToString("0.#", CultureInfo.InvariantCulture)}% 미만)");
        }

        if (first5 is { } f)
            reasons.Add(f.BarsSeen >= 5 ? $"첫 5봉 양봉 {f.UpBars} · 신고가 {f.NewHighs}" : $"첫 {f.BarsSeen}봉 양봉 {f.UpBars}");

        reasons.Add(gap is { } g ? $"갭 {Signed(g)}% (전일 종가: {(prevCloseSource == "daily" ? "일봉 기준" : "1분봉 기준")})" : "전일 종가 미제공");

        if (premarket is { AbovePremarketHigh: true }) reasons.Add("프리마켓 고가 돌파");
        else if (brokeOr == true) reasons.Add("개장 5분 고가 돌파");

        return reasons;
    }

    static string WindowDetail(params OpeningRvolWindow[] windows) =>
        string.Join(" · ", windows.Select(w => w.Ratio is { } r
            ? $"{w.LookbackSessions}일 {r.ToString("0.0", CultureInfo.InvariantCulture)}×"
            : $"{w.LookbackSessions}일 {w.SampleCount}세션"));

    static string Signed(double value) => (value >= 0 ? "+" : "") + value.ToString("0.0", CultureInfo.InvariantCulture);

    static double Round2(double value) => double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;

    static double Round4(double value) => double.IsFinite(value) ? Math.Round(value, 4, MidpointRounding.AwayFromZero) : 0;
}
