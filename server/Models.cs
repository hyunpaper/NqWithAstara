namespace Astra.Server;

public sealed class TossAuthException(string message, string guideUrl) : HttpRequestException(message, null, System.Net.HttpStatusCode.Forbidden)
{
    public string GuideUrl { get; } = guideUrl;
}

public sealed record WatchItem(string Symbol, string Name);
public sealed record TossTrade(string Symbol, decimal Price, decimal Volume, DateTimeOffset Timestamp, string Currency);
public sealed record Position(double EntryPrice, double Quantity, double? Target, double? Stop, string? TargetBasis = null, string? StopBasis = null);
public sealed record PositionInput(double EntryPrice, double Quantity);
public sealed record Candle(DateTimeOffset Timestamp, double Open, double High, double Low, double Close, double Volume);
public sealed record MarketSession(bool IsOpen, string Label, DateTimeOffset? NextOpen, DateTimeOffset? Start, DateTimeOffset? End);
/// <summary>신호 발동 시점의 가상 진입 기록. 손절/목표 도달 시 자동 청산되며 수수료 0.2%를 손익에 차감한다. 진입 당시 판단 근거(점수·σ·거래량·체결·RSI·사유)를 함께 남겨 사후 분석에 쓴다.
/// v5 구조 거래는 <see cref="SimTrade.Structure"/>에 진입 시점의 동결 계획(FrozenPlan)을 함께 저장하며, 이후 구조가 변해도 그 거래의 Stop/Target/근거는 바꾸지 않는다(설계 §10/§18). Logic이 소유 버전을 구분한다("v4"/null=레거시, "v5-structure.*"=구조 엔진).</summary>
public sealed record SimTrade(string Id, string Symbol, string Kind, DateTimeOffset EnteredAt, double EntryPrice, double Target, double Stop, string? TargetBasis, string? StopBasis, string Status, double? ExitPrice, DateTimeOffset? ExitAt, double? PnlPercent, double LastPrice, int? Score = null, double? ExtSigma = null, double? RelVolume = null, double? BuyShare = null, double? Rsi = null, string[]? Reasons = null, string? Logic = null, DateTimeOffset? LastEvaluatedBarAt = null, DateTimeOffset? SessionEnd = null, bool? ExitEstimated = null, DateTimeOffset? LastPriceAt = null, DateTimeOffset? TriggerBarAt = null, FrozenStructureContext? Structure = null, ExecutionProvenance? Execution = null);

/// <summary>체결 판정에 사용한 관측의 출처와 시각. 가격 정책이 아니라 사후 검증용 저장 계약이다.</summary>
public sealed record ExecutionProvenance(DateTimeOffset EntryBarStart, DateTimeOffset EntryBarCloseAt,
    string EntryMinuteCoverage, DateTimeOffset? EntryMinuteEvidenceAt, string? ExitSource = null,
    DateTimeOffset? ExitEvidenceAt = null, DateTimeOffset? EvaluatedBarStart = null,
    DateTimeOffset? EvaluatedBarCloseAt = null, string? BarrierDecision = null,
    double? EvaluatedBarOpen = null, double? EvaluatedBarHigh = null,
    double? EvaluatedBarLow = null, double? EvaluatedBarClose = null,
    DateTimeOffset? LastQuoteAt = null);

/// <summary>
/// 설계 §11 FrozenStructureContext. v5 거래가 체결된 시점의 구조 계획·추세·품질 스냅샷으로, 진입 이후
/// 새 저점/새 ATR/새 매물대가 생겨도 재계산하지 않는다(§10). 모드가 off/shadow로 되돌아가도 이 동결 계획대로
/// 기존 청산 경로(봉 replay·gap stop·same-bar stop-first·EOD)가 관리한다(§18 rollback).
/// 저장 계약이므로 Domain 계산 record가 아니라 평탄한 값만 담는다.
/// </summary>
public sealed record FrozenStructureContext(string EntryEventId, FrozenPlanSnapshot PlanSnapshot,
    string TrendAtEntry, double? SignedTrendAtEntry, double? EntryQualityAtEntry,
    DateTimeOffset AnalysisAsOf, DateTimeOffset? QuoteAt, string StructuralExitPolicyVersion);

/// <summary>
/// 설계 §11 StructuralTradePlan의 동결 저장 형태. 무효화/목표 구간은 진입 시점 경계 스냅샷만 남긴다(§16B FrozenPlan).
/// null Plan에 0을 넣지 않는다는 규칙(§11)에 따라 이 record는 성립한 계획(READY→ENTERED)에서만 만들어진다.
/// </summary>
public sealed record FrozenPlanSnapshot(string PlanId, string Kind, decimal EntryReference, decimal InvalidationAnchor,
    decimal Stop, decimal Target, string InvalidationZoneId, decimal InvalidationLower, decimal InvalidationUpper,
    string TargetZoneId, decimal TargetLower, decimal TargetUpper, decimal Buffer, string BufferBasis,
    decimal FrontRunBuffer, decimal NetReward, decimal NetRisk, decimal NetR, double RiskPercent,
    decimal FeePerShare, decimal ExtraCostPerShare, decimal? ValidSpread, bool MissingLiquidity,
    string EligibilityCostModelVersion, string RealizedFillCostModelVersion, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string EngineVersion, string PolicyHash, string[] ReasonCodes, string Explanation,
    // #43 additive: 계획 시점 1분 ATR. 기존 필드 순서·의미를 바꾸지 않으려고 마지막에 선택 필드로 붙인다.
    // 이 값이 있어야 거래 기록만으로 "손절폭이 ATR 대비 얼마였나"를 사후 재구성할 수 있다(#28 분석 입력).
    // 과거에 저장된 행은 null로 복원되며 결측을 0으로 대체하지 않는다(§16A).
    double? Atr1mAtPlan = null);
public sealed record IndicatorSnapshot(double Rsi, double EmaFast, double EmaSlow, double Vwap, double Atr, double RelativeVolume, double BollingerLower, double BollingerUpper, double VwapSd = 0);
public sealed record SignalResult(int Score, string Action, string[] Reasons, IndicatorSnapshot Indicators);
public sealed record SignalView(string Symbol, string Name, double Price, double ChangePercent, int Score, string Action, string[] Reasons, DateTimeOffset UpdatedAt, bool Stale, object Indicators, IEnumerable<object> Bars, object? Position, double Atr, string? Setup = null, DateTimeOffset? SetupAt = null, string? Breakout = null, DateTimeOffset? BreakoutAt = null);
public sealed record DailyMetrics(double? VolumeRatio3, double? VolumeRatio5, double? VolumeRatio20, double TodayVolume, double? GapPercent, double? ChangeFromPrevClose, double? RangePosition20, double? MaGap5, double? MaGap20, double SessionElapsedPercent);
public static class DailyStats
{
    /// <summary>진행 중 세션의 누적 거래량을 경과 시간으로 보정해 최근 N일 평균과 비교한다. 정규장 밖이거나 오늘 일봉이 없으면 null.</summary>
    public static DailyMetrics? Compute(IReadOnlyList<Candle> days, MarketSession market, DateTimeOffset now)
        => Compute(days, market, now, null);
    public static DailyMetrics? Compute(IReadOnlyList<Candle> days, MarketSession market, DateTimeOffset now, double? livePrice)
    {
        if (!market.IsOpen || market.Start is null || market.End is null || days.Count == 0 || now < market.Start || now >= market.End) return null;
        var sessionDate = MarketRules.TradingDate(market.Start.Value);
        var today = days.Where(x => MarketRules.TradingDate(x.Timestamp) == sessionDate).OrderBy(x => x.Timestamp).LastOrDefault();
        if (today is null || !double.IsFinite(today.Open) || !double.IsFinite(today.High) || !double.IsFinite(today.Low) || !double.IsFinite(today.Close) || !double.IsFinite(today.Volume)) return null;
        var history = days.Where(x => MarketRules.TradingDate(x.Timestamp) < sessionDate && double.IsFinite(x.Close) && double.IsFinite(x.High) && double.IsFinite(x.Low)).OrderBy(x => x.Timestamp).ToArray();
        if (history.Length < 3) return null;
        var elapsed = Math.Clamp((now - market.Start.Value) / (market.End.Value - market.Start.Value), 0.02, 1.0);
        double? Ratio(int n) { var volumes = history.Where(x => double.IsFinite(x.Volume) && x.Volume > 0).TakeLast(n).ToArray(); if (volumes.Length < n) return null; var avg = volumes.Average(x => x.Volume); return Math.Round(today.Volume / (avg * elapsed), 2); }
        var price = livePrice is > 0 && double.IsFinite(livePrice.Value) ? livePrice.Value : today.Close;
        var prevClose = history[^1].Close;
        var recent = history.TakeLast(20).ToArray();
        var high = Math.Max(recent.Max(x => x.High), today.High);
        var low = Math.Min(recent.Min(x => x.Low), today.Low);
        double? Ma(int n) => history.Length < n ? null : history.TakeLast(n).Average(x => x.Close);
        var ma5 = Ma(5); var ma20 = Ma(20);
        return new(
            Ratio(3), Ratio(5), Ratio(20), today.Volume,
            prevClose > 0 ? Math.Round((today.Open / prevClose - 1) * 100, 2) : null,
            prevClose > 0 ? Math.Round((price / prevClose - 1) * 100, 2) : null,
            high > low ? Math.Round((price - low) / (high - low) * 100, 1) : null,
            ma5 is > 0 ? Math.Round((price / ma5.Value - 1) * 100, 2) : null,
            ma20 is > 0 ? Math.Round((price / ma20.Value - 1) * 100, 2) : null,
            Math.Round(elapsed * 100, 1));
    }
}
public static class MarketRules
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    /// <summary>토스증권 미국주식 왕복 수수료(매수 0.1% + 매도 0.1%). 평가 손익에서 차감해 실질 손익을 보여준다.</summary>
    public const double RoundTripFeePercent = .2;
    public static DateOnly TradingDate(DateTimeOffset timestamp) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, NewYork).DateTime);
    public static bool IsOpen(DateTimeOffset now, DateTimeOffset start, DateTimeOffset end) => now >= start && now < end;
    public static Candle[] CompletedRegularBars(IEnumerable<Candle> bars, MarketSession session, DateTimeOffset quoteAt)
    {
        var minute = new DateTimeOffset(quoteAt.Year, quoteAt.Month, quoteAt.Day, quoteAt.Hour, quoteAt.Minute, 0, quoteAt.Offset);
        return bars.Where(x => session.Start.HasValue && session.End.HasValue && x.Timestamp >= session.Start && x.Timestamp < session.End && x.Timestamp < minute).OrderBy(x => x.Timestamp).ToArray();
    }
    public static Position Enter(double entry, double quantity, double? atr)
    {
        if (!double.IsFinite(entry) || entry <= 0) throw new ArgumentOutOfRangeException(nameof(entry));
        if (!double.IsFinite(quantity) || quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (atr is null or <= 0 || !double.IsFinite(atr.Value)) return new(entry, quantity, null, null);
        return PriceLevels.Enter(entry, quantity, atr.Value, []);
    }
    public static Position FreezeRisk(Position position, double atr, IReadOnlyList<PriceLevel>? levels = null) => position.Target.HasValue || atr <= 0 || !double.IsFinite(atr) ? position : PriceLevels.Enter(position.EntryPrice, position.Quantity, atr, levels ?? []);
}
