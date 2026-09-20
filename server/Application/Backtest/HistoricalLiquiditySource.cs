using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

/// <summary>
/// 과거 replay 시점의 호가 비용을 제공하는 입력 계약이다.
/// OHLCV만 가진 원천은 null을 반환하며, 이를 관측 호가로 승격하지 않는다.
/// </summary>
public interface IHistoricalLiquiditySource
{
    StructureLiquidity? Get(string symbol, DateTimeOffset observedAt);
}
