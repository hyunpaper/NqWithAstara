using Astra.Server.Application;

namespace Astra.Server.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapAstraApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (MonitorRuntimeState r, FeeRateCheckService? feeCheck, NewsQueryService? news,
            BarStoreService? bars, ConfluenceOptions? confluence, TimeProvider clock) =>
        {
            var s = r.Snapshot();
            var barsHealth = bars is null ? null : await bars.HealthAsync(MarketRules.TradingDate(clock.GetUtcNow()),
                confluence?.BenchmarkSymbol ?? "QQQ", CancellationToken.None);
            return Results.Ok(new { app = "Astra", status = "ready", connection = s.ConnectionStatus, credentialsRequired = s.ConnectionStatus is "idle" or "error", guideUrl = s.ConnectionMessage.Contains("허용 IP") ? "https://developers.tossinvest.com/docs" : null, warnings = feeCheck?.Warnings ?? Array.Empty<string>(), news = news?.Health(), bars = barsHealth });
        });
        app.MapGet("/api/news", (string? symbol, int? limit, NewsQueryService q) => Results.Ok(q.Articles(symbol, limit)));
        app.MapGet("/api/news/sentiment", (NewsQueryService q) => Results.Ok(q.Sentiment()));
        app.MapGet("/api/state", async (StateQueryService q) => Results.Ok(await q.GetAsync())); app.MapGet("/api/search", SearchAsync);
        app.MapPost("/api/watchlist", AddWatchAsync);
        app.MapDelete("/api/watchlist/{symbol}", async (string symbol, MonitorControlService c, CancellationToken ct) => { await c.RemoveAsync(symbol, ct); return Results.NoContent(); });
        app.MapPost("/api/start", async (MonitorControlService c, CancellationToken ct) => { await c.StartAsync(ct); return Results.Ok(); });
        app.MapPost("/api/stop", async (MonitorControlService c, CancellationToken ct) => { await c.StopAsync(ct); return Results.Ok(); });
        app.MapGet("/api/sim", async (SimulationReportQueryService q) => Results.Ok(await q.GetAsync()));
        app.MapGet("/api/validation", ValidationAsync);
        app.MapGet("/api/validation/real-vs-v5", RealVsV5Async);
        app.MapPost("/api/validation/real-fills/refresh", RealFillsRefreshAsync);
        app.MapGet("/api/metrics/{symbol}", MetricsAsync);
        app.MapGet("/api/liquidity/{symbol}", LiquidityAsync);
        app.MapGet("/api/structure/{symbol}", StructureAsync);
        app.MapGet("/api/confluence/{symbol}", ConfluenceAsync);
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
    /// <summary>이슈 #167: 최신 컨플루언스 점수와 기법별 값. 조회가 계산을 유발하지 않는다(C1 1단계).</summary>
    static async Task<IResult> ConfluenceAsync(string symbol, ConfluenceService query, CancellationToken ct) { var result = await query.GetAsync(symbol, ct); return result.HttpStatus switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(result.Response) }; }
    /// <summary>이슈 #28: additive 검증 보고서 조회. 읽기 전용이며 운영 진입/청산·점수·비용 정책을 바꾸지 않는다.</summary>
    static async Task<IResult> ValidationAsync(int? days, ValidationQueryService query, CancellationToken ct) { var result = await query.GetAsync(days, ct); return result.HttpStatus == 400 ? Results.BadRequest(new { message = $"days는 1~{ValidationQueryService.MaxWindowDays} 범위여야 합니다." }) : Results.Ok(result.Report); }
    /// <summary>이슈 #131: 저장된 실체결과 v5 관측의 대조 보고서. 조회가 Toss를 호출하지 않는다.</summary>
    static async Task<IResult> RealVsV5Async(string? date, RealVsV5QueryService query, CancellationToken ct) { var result = await query.GetAsync(date, ct); return result.HttpStatus == 400 ? Results.BadRequest(new { message = "date는 yyyy-MM-dd 형식이어야 합니다." }) : Results.Ok(result.Response); }
    /// <summary>이슈 #131: 실체결 수동 수집(읽기 전용 `/orders?status=CLOSED`). 주문 생성·정정·취소는 하지 않는다.</summary>
    static async Task<IResult> RealFillsRefreshAsync(string? date, RealFillsService service, CancellationToken ct) { DateOnly? day = null; if (!string.IsNullOrWhiteSpace(date)) { if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return Results.BadRequest(new { message = "date는 yyyy-MM-dd 형식이어야 합니다." }); day = parsed; } var result = await service.RefreshAsync(day, ct); return Results.Ok(result); }
    static async Task<IResult> PutPositionAsync(string symbol, PositionInput input, PositionService service, CancellationToken ct) { var r = await service.PutAsync(symbol, input, ct); return r.Status switch { PositionChangeStatus.Invalid => Results.BadRequest(), PositionChangeStatus.NotFound => Results.NotFound(), _ => Results.Ok(r.Position) }; }
}
