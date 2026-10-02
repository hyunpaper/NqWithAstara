using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

public sealed record ScoreCoreHealth(bool Enabled, string Status, DateTimeOffset? LastRunAt,
    DateTimeOffset? LastSuccessAt, string? LastError, int TargetCount, int SnapshotCount, int ShadowCount,
    int InsufficientCount, int UnavailableCount, DateTimeOffset? LastPrunedAt, int PrunedFiles,
    string PolicyVersion);

/// <summary>shadow 수집 주기 상태. `/api/health`의 scoreCore 블록 원천이다(#309).</summary>
public sealed class ScoreCoreRuntimeState(ScoreCoreOptions options)
{
    readonly object _gate = new();
    string _status = "idle";
    DateTimeOffset? _lastRunAt, _lastSuccessAt, _lastPrunedAt;
    string? _lastError;
    int _targets, _snapshots, _shadow, _insufficient, _unavailable, _pruned;

    public void RunStarted(DateTimeOffset at) { lock (_gate) { _lastRunAt = at; _status = "running"; } }

    public void RunCompleted(DateTimeOffset at, int targets, IReadOnlyCollection<ScoreCoreShadowSnapshot> snapshots)
    {
        lock (_gate)
        {
            _status = "ok";
            _lastSuccessAt = at;
            _lastError = null;
            _targets = targets;
            _snapshots = snapshots.Count;
            _shadow = snapshots.Count(x => x.Status == "shadow");
            _insufficient = snapshots.Count(x => x.Status == "insufficient_data");
            _unavailable = snapshots.Count(x => x.Status == "unavailable");
        }
    }

    public void RunFailed(DateTimeOffset at, string error)
    {
        lock (_gate) { _status = "failed"; _lastRunAt = at; _lastError = error; }
    }

    public void Pruned(DateTimeOffset at, int files) { lock (_gate) { _lastPrunedAt = at; _pruned = files; } }

    public ScoreCoreHealth Health()
    {
        lock (_gate)
            return new(options.Enabled, options.Enabled ? _status : "disabled", _lastRunAt, _lastSuccessAt,
                _lastError, _targets, _snapshots, _shadow, _insufficient, _unavailable, _lastPrunedAt, _pruned,
                ScoreCorePolicy.ShadowV1.Version);
    }
}
