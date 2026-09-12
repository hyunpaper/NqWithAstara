using System.Collections.Immutable;
using Astra.Server.Domain.Indicators;

namespace Astra.Server.Domain.Confluence;

/// <summary>
/// 기법 하나의 출력 (C3, #167). Score는 [−1,+1], Confidence는 [0,1]이며 롱 전용이라 음수는 "롱에 불리"다.
/// Warmup이거나 Confidence가 0이면 합산에서 제외된다.
/// </summary>
public sealed record TechniqueSignal(string Name, double Score, double Confidence, bool Warmup,
    ImmutableSortedDictionary<string, double?> Evidence)
{
    public static TechniqueSignal WarmingUp(string name, params (string Key, double? Value)[] evidence) =>
        new(name, 0, 0, true, Facts(evidence));

    /// <summary>자료 결측으로 신호를 낼 수 없는 상태. warmup이 아니라 c=0으로 남는다(RS 동기 실패·OBI 결측).</summary>
    public static TechniqueSignal Missing(string name, params (string Key, double? Value)[] evidence) =>
        new(name, 0, 0, false, Facts(evidence));

    public static TechniqueSignal Create(string name, double score, double confidence,
        params (string Key, double? Value)[] evidence) =>
        new(name, ConfluenceMath.Clamp(score), ConfluenceMath.Clamp(confidence, 0, 1), false, Facts(evidence));

    static ImmutableSortedDictionary<string, double?> Facts((string Key, double? Value)[] evidence) =>
        evidence.Length == 0
            ? ImmutableSortedDictionary<string, double?>.Empty
            : evidence.ToImmutableSortedDictionary(x => x.Key, x => IndicatorRounding.Ratio(x.Value),
                StringComparer.Ordinal);
}

/// <summary>기법 이름 상수 (C3 표, #167). 가중치 파일(K4)의 키이기도 하므로 값이 고정이다.</summary>
public static class TechniqueNames
{
    public const string Macd = "MACD";
    public const string Rsi = "RSI";
    public const string BollingerPercentB = "BB_PERCENT_B";
    public const string AdxDmi = "ADX_DMI";
    public const string VwapDeviation = "VWAP_DEVIATION";
    public const string RelativeVolume = "RVOL";
    public const string AtrChannel = "ATR_CHANNEL";
    public const string OpeningRange = "ORB15";
    public const string RelativeStrength = "RS_QQQ";
    public const string OrderBookImbalance = "OBI";

    public static readonly ImmutableArray<string> All =
    [
        Macd, Rsi, BollingerPercentB, AdxDmi, VwapDeviation, RelativeVolume, AtrChannel, OpeningRange,
        RelativeStrength, OrderBookImbalance
    ];
}

/// <summary>C3 기법 계산에 쓰는 공용 수학. 순수 함수이며 시계를 읽지 않는다.</summary>
public static class ConfluenceMath
{
    public static double Clamp(double value, double low = -1, double high = 1) =>
        !double.IsFinite(value) ? 0 : value < low ? low : value > high ? high : value;

    public static double Tanh(double value) => !double.IsFinite(value) ? 0 : Math.Tanh(value);

    public static int Sign(double value) => !double.IsFinite(value) || value == 0 ? 0 : value > 0 ? 1 : -1;
}

/// <summary>
/// 기법 어댑터의 입력 (C3, #167). 전부 완료 봉과 이미 관측된 값이며 어떤 필드도 미래를 보지 않는다(C6).
/// </summary>
/// <param name="Bars">세션 앵커 기준 완료 1분봉. 마지막 원소가 평가 대상 봉이다.</param>
/// <param name="BenchmarkBars">당일 벤치마크(QQQ) 완료 1분봉. 없으면 비어 있다.</param>
/// <param name="OrderBook">최근 poll 순서의 호가 스냅샷(오래된 것 → 최신). 없으면 비어 있다.</param>
/// <param name="RelativeVolume">기존 상대거래량 계산 경로가 낸 값. 표본이 없으면 null이다.</param>
/// <param name="PreviousSessionClose">C2 ATR 세션 첫봉 TR의 기준이 되는 전일 정규장 종가. 없으면 null이다.</param>
public sealed record ConfluenceInput(
    string Symbol,
    DateTimeOffset SessionStart,
    ImmutableArray<IndicatorBar> Bars,
    ImmutableArray<IndicatorBar> BenchmarkBars,
    ImmutableArray<OrderBookSnapshot> OrderBook,
    double? RelativeVolume,
    decimal? PreviousSessionClose = null);

/// <summary>OBI 입력으로 쓰는 호가 잔량 스냅샷 (C3, #167). 가격은 쓰지 않는다.</summary>
public sealed record OrderBookSnapshot(DateTimeOffset ObservedAt, double BidVolume, double AskVolume);
