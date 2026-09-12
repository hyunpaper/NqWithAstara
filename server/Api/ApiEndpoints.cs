using Astra.Server.Application;

namespace Astra.Server.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapAstraApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (MonitorRuntimeState r, FeeRateCheckService? feeCheck) => { var s = r.Snapshot(); return Results.Ok(new { app = "Astra", status = "ready", connection = s.ConnectionStatus, credentialsRequired = s.ConnectionStatus is "idle" or "error", guideUrl = s.ConnectionMessage.Contains("허용 IP") ? "https://developers.tossinvest.com/docs" : null, warnings = feeCheck?.Warnings ?? Array.Empty<string>() }); });
        app.MapGet("/api/state", async (StateQueryService q) => Results.Ok(await q.GetAsync())); app.MapGet("/api/search", SearchAsync);
        app.MapPost("/api/watchlist", AddWatchAsync);
        app.MapDelete("/api/watchlist/{symbol}", async (string symbol, MonitorControlService c, CancellationToken ct) => { await c.RemoveAsync(symbol, ct); return Results.NoContent(); });
        app.MapPost("/api/start", async (MonitorControlService c, CancellationToken ct) => { await c.StartAsync(ct); return Results.Ok(); });
        app.MapPost("/api/stop", async (MonitorControlService c, CancellationToken ct) => { await c.StopAsync(ct); return Results.Ok(); });
        app.MapGet("/api/sim", async (SimulationReportQueryService q) => Results.Ok(await q.GetAsync()));
        app.MapGet("/api/validation", ValidationAsync);
        app.MapGet("/api/metrics/{symbol}", MetricsAsync);
        app.MapGet("/api/liquidity/{symbol}", LiquidityAsync);
        app.MapGet("/api/structure/{symbol}", StructureAsync);
        app.MapPut("/api/positions/{symbol}", PutPositionAsync);
        app.MapDelete("/api/positions/{symbol}", async (string symbol, PositionService service, CancellationToken ct) => { await service.RemoveAsync(symbol, ct); return Results.NoContent(); });
        app.Map("/api/{**path}", () => Results.NotFound(new { message = "API endpoint not found." })); return app;
    }
    static async Task<IResult> AddWatchAsync(WatchItem? item, MonitorControlService c, CancellationToken ct) { try { var r = await c.AddAsync(item, ct); return r.Status switch { WatchlistChangeStatus.Ok => Results.Ok(), WatchlistChangeStatus.Invalid => Results.BadRequest(), WatchlistChangeStatus.NotFound => Results.NotFound(), _ => Results.BadRequest(new { message = r.Message }) }; } catch (HttpRequestException) { return Results.Problem("Toss 종목 조회에 실패했습니다.", statusCode: 502); } }
    static async Task<IResult> SearchAsync(string q, CatalogQueryService query, CancellationToken ct) { try { return Results.Ok(await query.SearchAsync(q, ct)); } catch (HttpRequestException) { return Results.Problem("Toss 종목 조회에 실패했습니다.", statusCode: 502); } }
    static async Task<IResult> MetricsAsync(string symbol, MetricsQueryService q, CancellationToken ct) { try { var r = await q.GetAsync(symbol, ct); return r.Status switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(r.Data) }; } catch (HttpRequestException) { return Results.Problem("Toss 일봉 조회에 실패했습니다.", statusCode: 502); } }
    static async Task<IResult> LiquidityAsync(string symbol, LiquidityQueryService query, CancellationToken ct) { try { var result = await query.GetAsync(symbol, ct); return result.HttpStatus switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(result.Response) }; } catch (HttpRequestException) { return Results.Problem("Toss 호가 조회에 실패했습니다.", statusCode: 502); } }
    /// <summary>설계 §12: 마지막 공개 분석 snapshot만 반환한다. 요청이 전체 분석이나 새 거래를 실행하지 않는다.</summary>
    static async Task<IResult> StructureAsync(string symbol, StructureAnalysisService query, CancellationToken ct) { var result = await query.GetAsync(symbol, ct); return result.HttpStatus switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(result.Response) }; }
    /// <summary>이슈 #28: additive 검증 보고서 조회. 읽기 전용이며 운영 진입/청산·점수·비용 정책을 바꾸지 않는다.</summary>
    static async Task<IResult> ValidationAsync(int? days, ValidationQueryService query, CancellationToken ct) { var result = await query.GetAsync(days, ct); return result.HttpStatus == 400 ? Results.BadRequest(new { message = $"days는 1~{ValidationQueryService.MaxWindowDays} 범위여야 합니다." }) : Results.Ok(result.Report); }
    static async Task<IResult> PutPositionAsync(string symbol, PositionInput input, PositionService service, CancellationToken ct) { var r = await service.PutAsync(symbol, input, ct); return r.Status switch { PositionChangeStatus.Invalid => Results.BadRequest(), PositionChangeStatus.NotFound => Results.NotFound(), _ => Results.Ok(r.Position) }; }
}
