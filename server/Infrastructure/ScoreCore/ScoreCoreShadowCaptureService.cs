using Astra.Server.Application;
using Astra.Server.Application.ScoreCore;

namespace Astra.Server.Infrastructure.ScoreCore;

/// <summary>Score Core shadow snapshot을 주기적으로 수집하고 보관 기간을 지난 날짜 파일을 정리한다(§8 1단계, #309).</summary>
public sealed class ScoreCoreShadowCaptureService(
    ScoreCoreOptions options,
    ScoreCoreTargetSelector selector,
    ScoreCoreShadowCaptureOrchestrator orchestrator,
    IScoreCoreSnapshotStore store,
    ScoreCoreRuntimeState state,
    IMonitorDiagnostics diagnostics,
    TimeProvider clock) : BackgroundService
{
    DateOnly? _lastPruneDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try { await Task.Delay(options.Interval, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        var now = clock.GetUtcNow();
        var asOf = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        await PruneIfDueAsync(asOf, ct);
        state.RunStarted(asOf);
        try
        {
            var targets = await selector.SelectAsync(asOf, ct);
            var outcomes = await orchestrator.CaptureTargetsAsync(targets, asOf, ct);
            foreach (var failed in outcomes.Where(x => x.Error is not null))
                diagnostics.PollFailed($"score-core-capture:{failed.TargetKind}:{failed.TargetId}", failed.Error!);
            state.RunCompleted(clock.GetUtcNow(), asOf, outcomes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            diagnostics.PollFailed("score-core-capture", exception);
            state.RunFailed(asOf, exception.GetType().Name);
        }
    }

    async Task PruneIfDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (_lastPruneDay == day) return;
        try
        {
            state.Pruned(now, await store.PruneAsync(now, options.Retention, ct));
            _lastPruneDay = day;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { diagnostics.PollFailed("score-core-retention", exception); }
    }
}
