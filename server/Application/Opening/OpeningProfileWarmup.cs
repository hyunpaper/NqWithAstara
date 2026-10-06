using Astra.Server.Domain;
using Astra.Server.Domain.Opening;

namespace Astra.Server.Application.Opening;

/// <summary>관심종목 개장 프로파일을 개장 전·기동 시 미리 적재한다(#375). 적재는 심볼별 하루 1회 캐시라 반복 호출해도 중복 요청이 없다.</summary>
public sealed class OpeningProfileWarmup(ILocalStore store, OpeningVolumeProfileSource profiles,
    OpeningScanPolicy policy, TimeProvider clock, IMonitorDiagnostics diagnostics)
{
    /// <summary>관심종목별 프로파일을 적재한다. 심볼 실패는 격리하고 적재한(또는 캐시된) 심볼 수를 돌려준다.</summary>
    public async Task<int> WarmAsync(DateOnly sessionDate, CancellationToken ct)
    {
        if (!policy.Enabled) return 0;
        var watch = await store.Read("watchlist.json", new List<WatchItem>());
        var warmed = 0;
        foreach (var item in watch.DistinctBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await profiles.GetAsync(item.Symbol, sessionDate, policy.LookbackSessions, ct);
                warmed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { diagnostics.MarketDataFailed(item.Symbol, "opening-scan-warmup", ex); }
        }
        return warmed;
    }

    public DateOnly SessionDate() => MarketRules.TradingDate(clock.GetUtcNow());
}
