using Astra.Server.Application.ScoreCore;

namespace Astra.Server.Api;

/// <summary>Score Core shadow snapshot 조회 API(§8 1단계, #309).</summary>
public static class ScoreCoreEndpoints
{
    public static IEndpointRouteBuilder MapScoreCoreApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/score-core/snapshots/{captureId}", async (string captureId, ScoreCoreQueryService query,
            CancellationToken ct) => ToResult(await query.SnapshotAsync(captureId, ct)));
        app.MapGet("/api/score-core/{kind}/{targetId}", async (string kind, string targetId,
            ScoreCoreQueryService query, CancellationToken ct) => ToResult(await query.LatestAsync(kind, targetId, ct)));
        return app;
    }

    static IResult ToResult(ScoreCoreQueryResult result) => result.Status switch
    {
        ScoreCoreQueryStatus.Disabled => Results.Ok(new ScoreCoreDisabledDto(false)),
        ScoreCoreQueryStatus.BadRequest => Results.BadRequest(),
        ScoreCoreQueryStatus.NotFound => Results.NotFound(),
        _ => Results.Ok(result.Snapshot),
    };
}
