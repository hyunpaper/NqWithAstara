namespace Astra.Server;

/// <summary>거래 비용 기본값의 단일 출처. 계좌별 실제 수수료가 아니라 정책·표시·측정의 코드 기본값이다.</summary>
public static class TradingCostDefaults
{
    /// <summary>토스증권 미국주식 왕복 수수료 기본값(매수 0.1% + 매도 0.1%).</summary>
    public const double RoundTripFeePercent = .2;
}
