using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Application.Rates;
using Astra.Server.Application.ScoreCore;
using System.Net;

namespace Astra.Server.Api;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapAstraApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (MonitorRuntimeState r, FeeRateCheckService? feeCheck, NewsQueryService? news,
            BarStoreService? bars, ConfluenceOptions? confluence, ScoreCoreRuntimeState? scoreCore, RatesRuntimeState? rates, TimeProvider clock,
            MonitorOptions? monitor, MonitorAutoStartService? autoStart, StructuralEntryHealthService? structureEntries) =>
        {
            var s = r.Snapshot();
            var warnings = (feeCheck?.Warnings ?? Array.Empty<string>()).Concat(autoStart?.Warnings ?? Array.Empty<string>()).ToArray();
            var tradingDate = MarketRules.TradingDate(clock.GetUtcNow());
            var barsHealth = bars is null ? null : await bars.HealthAsync(tradingDate,
                confluence?.BenchmarkSymbol ?? "QQQ", CancellationToken.None);
            var entriesHealth = structureEntries is null ? null : await structureEntries.HealthAsync(tradingDate);
            return Results.Ok(new { app = "Astra", status = "ready", connection = s.ConnectionStatus, credentialsRequired = s.ConnectionStatus is "idle" or "error", guideUrl = s.ConnectionMessage.Contains("허용 IP") ? "https://developers.tossinvest.com/docs" : null, autoStart = monitor?.AutoStart ?? false, startedAt = s.StartedAt, startedBy = s.StartedBy, warnings, news = news?.Health(), bars = barsHealth, scoreCore = scoreCore?.Health(), rates = rates?.Health(), structureEntries = entriesHealth });
        });
        app.MapScoreCoreApi();
        app.MapRatesApi();
        app.MapGet("/api/news", (string? symbol, int? limit, NewsQueryService q) => Results.Ok(q.Articles(symbol, limit)));
        app.MapGet("/api/news/{id}", (string id, NewsQueryService q) => q.Detail(id) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapGet("/api/news/detail", (string? id, string? symbol, NewsQueryService q) => q.DetailByQuery(id, symbol) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapPost("/api/news/translation", (string? id, NewsQueryService q) => q.RequestTranslation(id) ? Results.Accepted() : Results.NotFound());
        app.MapGet("/api/news/{id}/evidence", (string id, NewsQueryService q) => q.Evidence(id) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapGet("/api/news/sentiment", (NewsQueryService q) => Results.Ok(q.Sentiment()));
        app.MapPost("/api/news/migration/preview", NewsMigrationPreviewAsync);
        app.MapPost("/api/news/migration/execute", NewsMigrationExecuteAsync);
        app.MapGet("/api/market-mood", async (MarketMoodQueryService q, CancellationToken ct) => Results.Ok(await q.GetAsync(ct)));
        app.MapGet("/api/state", async (StateQueryService q) => Results.Ok(await q.GetAsync())); app.MapGet("/api/search", SearchAsync);
        app.MapPost("/api/watchlist", AddWatchAsync);
        app.MapDelete("/api/watchlist/{symbol}", async (string symbol, MonitorControlService c, CancellationToken ct) => { await c.RemoveAsync(symbol, ct); return Results.NoContent(); });
        app.MapPut("/api/watchlist/order", ReorderWatchAsync);
        app.MapPost("/api/start", async (MonitorControlService c, CancellationToken ct) => { await c.StartAsync(ct); return Results.Ok(); });
        app.MapPost("/api/stop", async (MonitorControlService c, CancellationToken ct) => { await c.StopAsync(ct); return Results.Ok(); });
        app.MapGet("/api/sim", async (SimulationReportQueryService q, CancellationToken ct) => Results.Ok(await q.GetAsync(ct)));
        app.MapPost("/api/sim/reset", SimResetAsync);
        app.MapGet("/api/validation", ValidationAsync);
        app.MapGet("/api/validation/real-vs-v5", RealVsV5Async);
        app.MapPost("/api/validation/real-fills/refresh", RealFillsRefreshAsync);
        app.MapGet("/api/metrics/{symbol}", MetricsAsync);
        app.MapGet("/api/liquidity/{symbol}", LiquidityAsync);
        app.MapGet("/api/structure/{symbol}", StructureAsync);
        app.MapGet("/api/structure/research/rejected", async (int? limit, DateTimeOffset? asOf, StructureAnalysisService service, CancellationToken ct) => Results.Ok(await service.ResearchRejectedAsync(limit ?? 100, asOf, ct)));
        app.MapGet("/api/confluence/weights", ConfluenceWeights);
        app.MapGet("/api/confluence/{symbol}", ConfluenceAsync);
        app.MapPost("/api/replays", StartReplayAsync);
        app.MapPost("/api/replays/{id}/cancel", CancelReplayAsync);
        app.MapGet("/api/replays/latest", async (HistoricalReplayService service) =>
        {
            var run = await service.LatestAsync();
            return run is null ? Results.NotFound(new { message = "저장된 과거 replay가 없습니다." }) : Results.Ok(run);
        });
        app.MapGet("/api/replays/{id}", async (string id, HistoricalReplayService service) =>
        {
            var run = await service.GetAsync(id);
            return run is null ? Results.NotFound(new { message = "과거 replay 작업을 찾을 수 없습니다." }) : Results.Ok(run);
        });
        app.MapGet("/api/replays/{id}/trades", async (string id, HistoricalReplayService service,
            CancellationToken ct) =>
        {
            var trades = await service.TradesAsync(id, ct);
            return trades is null ? Results.NotFound(new { message = "과거 replay 작업을 찾을 수 없습니다." }) : Results.Ok(trades);
        });
        app.MapGet("/api/replays/{id}/diagnostics", async (string id, HistoricalReplayService service,
            CancellationToken ct) =>
        {
            var trades = await service.DiagnosticTradesAsync(id, ct);
            return trades is null ? Results.NotFound(new { message = "과거 replay 작업을 찾을 수 없습니다." })
                : Results.Ok(HistoricalReplayDiagnosticsBuilder.Build(trades));
        });
        app.MapPut("/api/positions/{symbol}", PutPositionAsync);
        app.MapDelete("/api/positions/{symbol}", async (string symbol, PositionService service, CancellationToken ct) => { await service.RemoveAsync(symbol, ct); return Results.NoContent(); });
        app.Map("/api/{**path}", () => Results.NotFound(new { message = "API endpoint not found." })); return app;
    }
    static async Task<IResult> NewsMigrationPreviewAsync(HttpContext context, NewsStorageMigrationService service, CancellationToken ct)
        => IsLoopback(context) ? Results.Ok(await service.PlanAsync(ct)) : Results.StatusCode(StatusCodes.Status403Forbidden);

    static async Task<IResult> NewsMigrationExecuteAsync(HttpContext context, NewsStorageMigrationService service, CancellationToken ct)
        => IsLoopback(context) ? Results.Ok(await service.ExecuteAsync(await service.PlanAsync(ct), ct)) : Results.StatusCode(StatusCodes.Status403Forbidden);

    internal static bool IsLoopback(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) return false;
        var host = context.Request.Host.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))) return false;
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out var originAddress) && IPAddress.IsLoopback(originAddress);
    }
    /// <summary>관심종목 표시 순서 저장. 집합 불일치는 400으로 알려 클라가 최신 state로 재동기화한다 (#224).</summary>
    static async Task<IResult> ReorderWatchAsync(WatchlistOrderRequest? body, MonitorControlService c, CancellationToken ct)
    {
        var result = await c.ReorderAsync(body?.Symbols, ct);
        return result.Status == WatchlistChangeStatus.Ok ? Results.NoContent() : Results.BadRequest(new { message = result.Message });
    }
    /// <summary>시뮬 거래 이력 초기화(#228). 백업이 실패하면 삭제하지 않고 500을 돌려준다.</summary>
    static async Task<IResult> SimResetAsync(SimResetRequest? body, SimulationResetService service, HttpContext http)
    {
        var requester = new SimulationResetRequester(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
        try { return Results.Ok(await service.ResetAsync(body?.IncludeOpen ?? false, requester)); }
        catch (IOException) { return Results.Problem("백업 생성에 실패해 시뮬 이력을 삭제하지 않았습니다.", statusCode: 500); }
        catch (UnauthorizedAccessException) { return Results.Problem("백업 생성에 실패해 시뮬 이력을 삭제하지 않았습니다.", statusCode: 500); }
    }
    static async Task<IResult> AddWatchAsync(WatchItem? item, MonitorControlService c, CancellationToken ct) { try { var r = await c.AddAsync(item, ct); return r.Status switch { WatchlistChangeStatus.Ok => Results.Ok(), WatchlistChangeStatus.Invalid => Results.BadRequest(), WatchlistChangeStatus.NotFound => Results.NotFound(), _ => Results.BadRequest(new { message = r.Message }) }; } catch (HttpRequestException) { return Results.Problem("Toss 종목 조회에 실패했습니다.", statusCode: 502); } }
    static async Task<IResult> SearchAsync(string q, CatalogQueryService query, CancellationToken ct) { try { return Results.Ok(await query.SearchAsync(q, ct)); } catch (HttpRequestException) { return Results.Problem("Toss 종목 조회에 실패했습니다.", statusCode: 502); } }
    static async Task<IResult> MetricsAsync(string symbol, MetricsQueryService q, CancellationToken ct) { try { var r = await q.GetAsync(symbol, ct); return r.Status switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(r.Data) }; } catch (HttpRequestException) { return Results.Problem("Toss 일봉 조회에 실패했습니다.", statusCode: 502); } }
    static async Task<IResult> LiquidityAsync(string symbol, LiquidityQueryService query, CancellationToken ct) { try { var result = await query.GetAsync(symbol, ct); return result.HttpStatus switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(result.Response) }; } catch (HttpRequestException) { return Results.Problem("Toss 호가 조회에 실패했습니다.", statusCode: 502); } }
    /// <summary>설계 §12: 마지막 공개 분석 snapshot만 반환한다. 요청이 전체 분석이나 새 거래를 실행하지 않는다.</summary>
    static async Task<IResult> StructureAsync(string symbol, StructureAnalysisService query, CancellationToken ct) { var result = await query.GetAsync(symbol, ct); return result.HttpStatus switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(result.Response) }; }
    /// <summary>이슈 #167: 최신 컨플루언스 점수와 기법별 값. 조회가 계산을 유발하지 않는다(C1 1단계).</summary>
    static async Task<IResult> ConfluenceAsync(string symbol, ConfluenceService query, CancellationToken ct) { var result = await query.GetAsync(symbol, ct); return result.HttpStatus switch { 400 => Results.BadRequest(), 404 => Results.NotFound(), _ => Results.Ok(result.Response) }; }
    /// <summary>이슈 #169: 기동 시 읽은 가중치 파일과 그 근거. 조회가 측정을 유발하지 않는다(C5).</summary>
    static IResult ConfluenceWeights(ConfluenceWeightsDocument document) => Results.Ok(new
    {
        weightsVersion = document.WeightsVersion,
        source = document.IsDefault ? "default" : "file",
        measuredAt = document.MeasuredAt,
        window = new { from = document.WindowFrom, to = document.WindowTo },
        horizonBars = document.HorizonBars,
        weights = document.Weights.ToDictionary(x => x.Key, x => new
        {
            w = x.Value.W,
            n = x.Value.N,
            hitRate = x.Value.HitRate,
            ci = new[] { ConfluenceWeightsDocument.Finite(x.Value.CiLow), ConfluenceWeightsDocument.Finite(x.Value.CiHigh) },
            brier = ConfluenceWeightsDocument.Finite(x.Value.Brier),
            pValue = ConfluenceWeightsDocument.Finite(x.Value.PValue),
            status = x.Value.Status
        }, StringComparer.Ordinal)
    });
    static async Task<IResult> StartReplayAsync(HistoricalReplayRequest? request, HistoricalReplayService service,
        CancellationToken ct)
    {
        var result = await service.StartAsync(request, ct);
        return result.HttpStatus switch
        {
            202 => Results.Accepted($"/api/replays/{result.Run!.Id}", result.Run),
            409 => Results.Conflict(new { message = result.Message }),
            _ => Results.BadRequest(new { message = result.Message })
        };
    }
    static async Task<IResult> CancelReplayAsync(string id, HistoricalReplayService service)
    {
        var result = await service.CancelAsync(id);
        return result.HttpStatus == 404
            ? Results.NotFound(new { message = result.Message })
            : Results.Ok(result.Run);
    }
    /// <summary>이슈 #28: additive 검증 보고서 조회. 읽기 전용이며 운영 진입/청산·점수·비용 정책을 바꾸지 않는다.</summary>
    static async Task<IResult> ValidationAsync(int? days, ValidationQueryService query, CancellationToken ct) { var result = await query.GetAsync(days, ct); return result.HttpStatus == 400 ? Results.BadRequest(new { message = $"days는 1~{ValidationQueryService.MaxWindowDays} 범위여야 합니다." }) : Results.Ok(result.Report); }
    /// <summary>이슈 #131: 저장된 실체결과 v5 관측의 대조 보고서. 조회가 Toss를 호출하지 않는다.</summary>
    static async Task<IResult> RealVsV5Async(string? date, RealVsV5QueryService query, CancellationToken ct) { var result = await query.GetAsync(date, ct); return result.HttpStatus == 400 ? Results.BadRequest(new { message = "date는 yyyy-MM-dd 형식이어야 합니다." }) : Results.Ok(result.Response); }
    /// <summary>이슈 #131: 실체결 수동 수집(읽기 전용 `/orders?status=CLOSED`). 주문 생성·정정·취소는 하지 않는다.</summary>
    static async Task<IResult> RealFillsRefreshAsync(string? date, RealFillsService service, CancellationToken ct) { DateOnly? day = null; if (!string.IsNullOrWhiteSpace(date)) { if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return Results.BadRequest(new { message = "date는 yyyy-MM-dd 형식이어야 합니다." }); day = parsed; } var result = await service.RefreshAsync(day, ct); return Results.Ok(result); }
    static async Task<IResult> PutPositionAsync(string symbol, PositionInput input, PositionService service, CancellationToken ct) { var r = await service.PutAsync(symbol, input, ct); return r.Status switch { PositionChangeStatus.Invalid => Results.BadRequest(), PositionChangeStatus.NotFound => Results.NotFound(), _ => Results.Ok(r.Position) }; }
}

public sealed record WatchlistOrderRequest(string[]? Symbols);

public sealed record SimResetRequest(bool IncludeOpen);
