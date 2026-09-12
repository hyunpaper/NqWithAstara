namespace Astra.Server.Domain.Indicators;

/// <summary>컨플루언스 지표 코어의 완료 봉 입력 단위 (C2, #166).</summary>
public sealed record IndicatorBar(
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>지표 출력 반올림 규칙 — 가격 4자리·비율 4자리 (C2, #166).</summary>
public static class IndicatorRounding
{
    public const int PriceDigits = 4;
    public const int RatioDigits = 4;

    public static double Price(double value)
        => double.IsFinite(value) ? Math.Round(value, PriceDigits, MidpointRounding.AwayFromZero) : double.NaN;

    public static double Ratio(double value)
        => double.IsFinite(value) ? Math.Round(value, RatioDigits, MidpointRounding.AwayFromZero) : double.NaN;

    public static double? Price(double? value) => value is { } v && double.IsFinite(v) ? Price(v) : null;

    public static double? Ratio(double? value) => value is { } v && double.IsFinite(v) ? Ratio(v) : null;
}

/// <summary>지표 계산 공용 보조 — decimal 입력을 double로 승격하고 기간 인자를 검증한다 (C2, #166).</summary>
public static class IndicatorMath
{
    public static void RequirePeriod(int period, string name)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(name);
    }

    public static double TrueRange(IndicatorBar bar, decimal? previousClose)
    {
        double high = (double)bar.High, low = (double)bar.Low;
        if (previousClose is not { } prev) return high - low;
        var close = (double)prev;
        return Math.Max(high - low, Math.Max(Math.Abs(high - close), Math.Abs(low - close)));
    }

    public static double WilderNext(double previous, double current, int period)
        => ((period - 1) * previous + current) / period;
}
