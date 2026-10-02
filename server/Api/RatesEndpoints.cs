using Astra.Server.Application.Rates;

namespace Astra.Server.Api;

/// <summary>실시간 미국채 금리 조회 API(#316). 표시·기록용이며 매매 판정 입력이 아니다.</summary>
public static class RatesEndpoints
{
    public static IEndpointRouteBuilder MapRatesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/rates", (RatesRuntimeState state, TimeProvider clock) => Results.Ok(state.Snapshot(clock.GetUtcNow())));
        return app;
    }
}
