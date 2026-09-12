using Astra.Server.Domain;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

// v5 구조 엔진 D6 — 설계 §18 active 배선의 Application 절반.
// v5 계산 소스(StructureAnalysisService·Domain/Structure)는 실제 거래 저장소를 직접 모른 채 이 포트만 호출한다.
// 거래 저장은 기존 저장소(simtrades.json)를 그대로 쓰며 새 저장 파일을 만들지 않는다(§18).

/// <summary>
/// active 모드에서 성립한 v5 계획(READY 대표 후보)을 실제 시뮬 거래로 커밋하는 포트.
/// off/shadow에서는 호출되지 않는다 — 모드 게이트는 호출자(StructureAnalysisService)가 소유한다(§16B).
/// </summary>
public interface IStructuralTradeEntries
{
    Task<StructuralEntryResult> TryEnterAsync(StructuralEntryRequest request, CancellationToken ct);
}

/// <summary>
/// 기존 거래 저장소에 대한 유일한 v5 쓰기 지점. 읽기-검사-쓰기를 store.Update 한 번으로 묶어
/// 종목당 OPEN 1개 제한(버전 공통)과 EntryEventId 멱등성을 저장 시점에 원자적으로 재확인한다(§12.5).
/// </summary>
public sealed class StructuralTradeEntryService(ILocalStore store, StructurePolicy? policy = null)
    : IStructuralTradeEntries
{
    readonly StructurePolicy _policy = policy ?? StructurePolicy.Default;

    public async Task<StructuralEntryResult> TryEnterAsync(StructuralEntryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 진입이 성립하지 않는 경우(멱등 재시도·OPEN 제한·손절 쿨다운)에는 저장 파일을 다시 쓰지 않는다.
        var preview = StructuralSimulation.Enter(await store.Read("simtrades.json", new List<SimTrade>()), request,
            _policy);
        if (preview.Outcome != StructuralEntryOutcome.Entered) return preview;
        return await store.Update("simtrades.json", new List<SimTrade>(), trades =>
        {
            var result = StructuralSimulation.Enter(trades, request, _policy);
            return (result.Trades, result);
        });
    }
}
