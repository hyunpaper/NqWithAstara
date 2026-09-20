namespace Astra.Server.Domain;

/// <summary>구조 엔진이 판단한 거래 방향. 기존 저장 행은 기본값 Long으로 읽어 하위 호환한다.</summary>
public enum TradeSide
{
    Long = 1,
    Short = -1
}

/// <summary>
/// 신호 봉에서 확정된 계획을 다음 완료 봉의 관측 가능한 가격으로 확인하기 위한 대기 상태.
/// 확인 전에는 SimTrade를 만들지 않으며, 계획과 확인 관측을 분리해 look-ahead를 막는다.
/// </summary>
public sealed record PendingEntry(
    string EntryEventId,
    string Symbol,
    TradeSide Side,
    DateTimeOffset SignalBarStart,
    DateTimeOffset ConfirmationBarStart,
    DateTimeOffset ExpiresAt,
    double PlannedStop,
    double PlannedTarget,
    double? PlannedEntry,
    string PlanId,
    string PlanPolicyHash);

public enum PendingEntryDecision
{
    Confirmed,
    Expired,
    RejectedGap,
    RejectedUnobservedFill,
    RejectedInvalidPlan
}

/// <summary>확인봉 OHLC에서 실제로 관측한 진입 결과. 결정 시각 이후 데이터만 담는다.</summary>
public sealed record EntryConfirmation(
    PendingEntry Pending,
    PendingEntryDecision Decision,
    DateTimeOffset ObservedAt,
    double? FillPrice,
    double? SpreadCost,
    string PriceSource,
    string EvidenceStatus);

public static class PendingEntryPolicy
{
    /// <summary>확인봉의 OHLC만 사용한다. 신호봉의 종가나 이후 봉을 체결가로 재사용하지 않는다.</summary>
    public static EntryConfirmation Confirm(PendingEntry pending, Candle confirmationBar, DateTimeOffset observedAt,
        double? observedFill, string priceSource = "CONFIRMATION_BAR")
    {
        if (confirmationBar.Timestamp < pending.ConfirmationBarStart)
            return new(pending, PendingEntryDecision.RejectedUnobservedFill, observedAt, null, null, priceSource, "UNOBSERVED");
        if (observedAt > pending.ExpiresAt)
            return new(pending, PendingEntryDecision.Expired, observedAt, null, null, priceSource, "OBSERVED");
        if (pending.Side == TradeSide.Long && confirmationBar.Open <= pending.PlannedStop ||
            pending.Side == TradeSide.Short && confirmationBar.Open >= pending.PlannedStop)
            return new(pending, PendingEntryDecision.RejectedGap, observedAt, null, null, priceSource, "OBSERVED");
        if (observedFill is not { } fill || !double.IsFinite(fill) || fill <= 0)
            return new(pending, PendingEntryDecision.RejectedUnobservedFill, observedAt, null, null, priceSource, "UNOBSERVED");
        var valid = pending.Side == TradeSide.Long
            ? pending.PlannedStop < fill && pending.PlannedTarget > fill
            : pending.PlannedTarget < fill && pending.PlannedStop > fill;
        return valid
            ? new(pending, PendingEntryDecision.Confirmed, observedAt, fill, null, priceSource, "OBSERVED")
            : new(pending, PendingEntryDecision.RejectedInvalidPlan, observedAt, null, null, priceSource, "OBSERVED");
    }
}

/// <summary>walk-forward 한 구간의 결과. 승률은 목표가 아니라 검증 지표다.</summary>
public sealed record WalkForwardReplayWindow(
    DateOnly TrainFrom,
    DateOnly TrainTo,
    DateOnly ValidationFrom,
    DateOnly ValidationTo,
    int Signals,
    int Entries,
    int ClosedTrades,
    int Wins,
    double? GrossPnlPercent,
    double? NetPnlPercent,
    string DataQuality,
    string? PolicyHash);

/// <summary>훈련 구간에서 고른 정책을 이후 validation 구간에만 적용했음을 남기는 진단 계약.</summary>
public sealed record WalkForwardReplayDiagnostics(
    string Version,
    IReadOnlyList<WalkForwardReplayWindow> Windows,
    string SelectionBasis,
    string Limitation);
