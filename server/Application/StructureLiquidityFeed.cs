using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

/// <summary>
/// 이슈 #41 — 폴링 경로에서 v5 구조 엔진으로 호가를 흘려보내는 유일한 변환 지점.
///
/// 새 외부 호출 경로를 만들지 않는다. <see cref="LiquidityQueryService"/>가 이미 가진
/// 캐시 + in-flight 중복 제거 + 정규장/모니터링 상태 게이트를 그대로 통과하므로,
/// 같은 poll 안의 여러 호출과 `/api/liquidity` 요청이 같은 조회 하나를 공유한다(§9.1).
///
/// 검증되지 않은 호가로 spread를 만들어내지 않는다: 장외·중지·조회 실패·교차/지연 호가는
/// 전부 null로 남기고 기존 `MISSING_LIQUIDITY_COST`(assumedSpread=0) 경로를 그대로 태운다(§16B).
/// 호가 조회 실패는 v4 신호·거래에 영향을 주면 안 되므로 여기서 종목 단위로 삼킨다(§16).
/// </summary>
public sealed class StructureLiquidityFeed(LiquidityQueryService liquidity, IMonitorDiagnostics diagnostics)
{
    /// <summary>구조 입력으로 쓸 수 있는 유일한 상태. 그 밖(stopped/marketClosed/invalid/unavailable)은 결측이다.</summary>
    public const string StatusReady = "ready";

    public const string DiagnosticsOperation = "structure-liquidity";

    public async Task<StructureLiquidity?> TryGetAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var (status, response) = await liquidity.GetAsync(symbol, ct).ConfigureAwait(false);
            return status == 200 && response is not null ? Map(response) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // v5 격리(§16): 호가가 없다고 v4 신호·거래·폴링이 흔들리면 안 된다. 결측으로만 남긴다.
            diagnostics.MarketDataFailed(symbol, DiagnosticsOperation, ex);
            return null;
        }
    }

    /// <summary>
    /// 이미 검증된 ready 응답의 값을 옮기기만 한다. 새 가격·잔량을 계산하거나 추정하지 않는다.
    /// 잔량은 표시 호가 잔량 합계이며 §16B "양쪽 양수 잔량" 검사에만 쓰인다 — spread는 최우선 호가에서만 나온다.
    /// </summary>
    public static StructureLiquidity? Map(LiquidityResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!string.Equals(response.Status, StatusReady, StringComparison.Ordinal)) return null;
        if (response.Liquidity is not { } summary || response.ObservedAt is not { } observedAt) return null;
        return new StructureLiquidity(summary.BestBid, summary.BestAsk, observedAt,
            (double)summary.DisplayedBidVolume, (double)summary.DisplayedAskVolume);
    }
}
