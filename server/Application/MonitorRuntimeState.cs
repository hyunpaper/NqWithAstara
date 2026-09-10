namespace Astra.Server.Application;

public sealed record MonitorRuntimeSnapshot(
    bool Running,
    long Generation,
    string ConnectionStatus,
    string ConnectionMessage,
    MarketSession Market,
    DateTimeOffset UpdatedAt);

/// <summary>Single synchronization boundary for lifecycle commands and poll-result commits.</summary>
public sealed class MonitorRuntimeState
{
    readonly SemaphoreSlim _control = new(1, 1);
    readonly object _stateGate = new();
    MonitorRuntimeSnapshot _state = new(false, 0, "idle", "Start를 눌러 모니터링하세요.", new(false, "확인 전", null, null, null), DateTimeOffset.UtcNow);

    public MonitorRuntimeSnapshot Snapshot() { lock (_stateGate) return _state; }
    public bool IsCurrent(long generation) { lock (_stateGate) return _state.Running && _state.Generation == generation; }

    public async ValueTask<IDisposable> EnterControlAsync(CancellationToken ct = default)
    {
        await _control.WaitAsync(ct);
        return new Lease(_control);
    }

    public long CommitStart()
    {
        lock (_stateGate)
        {
            _state = _state with { Running = true, Generation = _state.Generation + 1, ConnectionStatus = "connecting", ConnectionMessage = "Toss Open API 연결 중", UpdatedAt = DateTimeOffset.UtcNow };
            return _state.Generation;
        }
    }

    public long CommitStop()
    {
        lock (_stateGate)
        {
            _state = _state with { Running = false, Generation = _state.Generation + 1, ConnectionStatus = "idle", ConnectionMessage = "모니터링 중지됨", UpdatedAt = DateTimeOffset.UtcNow };
            return _state.Generation;
        }
    }

    public long CommitWatchlistChange()
    {
        lock (_stateGate)
        {
            if (_state.Running) _state = _state with { Generation = _state.Generation + 1, UpdatedAt = DateTimeOffset.UtcNow };
            return _state.Generation;
        }
    }

    public bool TryCommit(long generation, Func<MonitorRuntimeSnapshot, MonitorRuntimeSnapshot> update)
    {
        lock (_stateGate)
        {
            if (!_state.Running || _state.Generation != generation) return false;
            _state = update(_state);
            return true;
        }
    }

    public bool TryCommit(long generation, Action mutation)
    {
        lock (_stateGate)
        {
            if (!_state.Running || _state.Generation != generation) return false;
            mutation();
            return true;
        }
    }

    sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release(); }
    }
}
