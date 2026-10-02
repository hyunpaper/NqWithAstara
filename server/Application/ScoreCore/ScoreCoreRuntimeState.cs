using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

public sealed record ScoreCoreHealth(bool Enabled, string Status, DateTimeOffset? LastRunAt,
    DateTimeOffset? LastSuccessAt, string? LastError, int TargetCount, int SnapshotCount, int AppendedCount,
    int UnchangedCount, int ShadowCount, int InsufficientCount, int UnavailableCount,
    IReadOnlyList<string> FailedTargets, DateTimeOffset? LastPrunedAt, int PrunedFiles, string PolicyVersion);

/// <summary>shadow 수집 주기 상태와 대상별 마지막 확인 시각. `/api/health`의 scoreCore 블록 원천이다(#309).</summary>
public sealed class ScoreCoreRuntimeState(ScoreCoreOptions options)
{
    readonly object _gate = new();
    readonly Dictionary<string, DateTimeOffset> _confirmed = new(StringComparer.Ordinal);
    string _status = "idle";
    DateTimeOffset? _lastRunAt, _lastSuccessAt, _lastPrunedAt;
    string? _lastError;
    int _targets, _snapshots, _appended, _unchanged, _shadow, _insufficient, _unavailable, _pruned;
    IReadOnlyList<string> _failedTargets = [];

    public void RunStarted(DateTimeOffset at) { lock (_gate) { _lastRunAt = at; _status = "running"; } }

    public void RunCompleted(DateTimeOffset at, DateTimeOffset asOf, IReadOnlyCollection<ScoreCoreCaptureOutcome> outcomes)
    {
        lock (_gate)
        {
            var captured = outcomes.Where(x => x.Snapshot is not null).ToArray();
            var failed = outcomes.Where(x => x.Error is not null).ToArray();
            foreach (var outcome in captured.Where(x => x.Append is ScoreSnapshotAppendResult.Appended
                         or ScoreSnapshotAppendResult.Unchanged or ScoreSnapshotAppendResult.AlreadyExists))
                _confirmed[Key(outcome.TargetKind, outcome.TargetId)] = asOf;
            _status = failed.Length == 0 ? "ok" : captured.Length == 0 ? "failed" : "partial";
            if (captured.Length > 0) _lastSuccessAt = at;
            _lastError = failed.FirstOrDefault()?.Error?.GetType().Name;
            _targets = outcomes.Count;
            _snapshots = captured.Length;
            _appended = captured.Count(x => x.Append == ScoreSnapshotAppendResult.Appended);
            _unchanged = captured.Count(x => x.Append == ScoreSnapshotAppendResult.Unchanged);
            _shadow = captured.Count(x => x.Snapshot!.Status == "shadow");
            _insufficient = captured.Count(x => x.Snapshot!.Status == "insufficient_data");
            _unavailable = captured.Count(x => x.Snapshot!.Status == "unavailable");
            _failedTargets = failed.Select(x => Key(x.TargetKind, x.TargetId)).ToArray();
        }
    }

    public void RunFailed(DateTimeOffset at, string error)
    {
        lock (_gate) { _status = "failed"; _lastRunAt = at; _lastError = error; }
    }

    public void Pruned(DateTimeOffset at, int files) { lock (_gate) { _lastPrunedAt = at; _pruned = files; } }

    public DateTimeOffset? LastConfirmedAt(ImpactTargetKind kind, string targetId)
    {
        lock (_gate) return _confirmed.TryGetValue(Key(kind, targetId), out var at) ? at : null;
    }

    public ScoreCoreHealth Health()
    {
        lock (_gate)
            return new(options.Enabled, options.Enabled ? _status : "disabled", _lastRunAt, _lastSuccessAt,
                _lastError, _targets, _snapshots, _appended, _unchanged, _shadow, _insufficient, _unavailable,
                _failedTargets, _lastPrunedAt, _pruned, ScoreCorePolicy.ShadowV1.Version);
    }

    static string Key(ImpactTargetKind kind, string targetId)
        => kind.ToString().ToLowerInvariant() + ":" + targetId.ToUpperInvariant();
}
