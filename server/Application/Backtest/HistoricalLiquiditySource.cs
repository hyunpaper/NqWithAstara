using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

/// <summary>
/// 과거 replay 시점의 호가 비용을 제공하는 입력 계약이다.
/// OHLCV만 가진 원천은 null을 반환하며, 이를 관측 호가로 승격하지 않는다.
/// </summary>
public interface IHistoricalLiquiditySource
{
    StructureLiquidity? Get(string symbol, DateTimeOffset observedAt, decimal referencePrice);

    /// <summary>실시간 호가가 아닌 검증용 모델 비용인지 구분한다.</summary>
    bool IsModeled => false;
    string SourceName => "OBSERVED_ORDERBOOK";
}

/// <summary>
/// 과거 bid/ask 원천이 없을 때 사용하는 명시적 검증용 비용 모델이다.
/// 운영 서비스에는 주입하지 않으며, 결과에는 모델 비용임을 남긴다.
/// </summary>
public sealed record HistoricalReplayCostModel(string Version, double SpreadPercent, double? ShortBorrowPercent)
{
    /// <summary>사전에 고정한 OHLCV 검증 프로필이다. 운영 비용으로 사용하지 않는다.</summary>
    public static HistoricalReplayCostModel ConservativeDefault { get; } =
        new("historical.ohlcv-spread-borrow-model.v2", .10, .02);

    public static HistoricalReplayCostModel FeeOnlyLongOnly { get; } =
        new("historical.fee-only-long-only.v1", 0, null);

    public decimal Spread(decimal referencePrice) =>
        referencePrice > 0 && double.IsFinite(SpreadPercent)
            ? referencePrice * (decimal)SpreadPercent / 100m
            : 0m;
}

public sealed class ModeledHistoricalLiquiditySource(HistoricalReplayCostModel model) : IHistoricalLiquiditySource
{
    public bool IsModeled => true;
    public string SourceName => model.Version;

    public StructureLiquidity? Get(string symbol, DateTimeOffset observedAt, decimal referencePrice)
    {
        if (referencePrice <= 0) return null;
        var spread = model.Spread(referencePrice);
        if (spread <= 0)
            return new StructureLiquidity(referencePrice, referencePrice, observedAt, 1, 1);
        var half = spread / 2m;
        return new StructureLiquidity(referencePrice - half, referencePrice + half, observedAt, 1, 1);
    }
}
