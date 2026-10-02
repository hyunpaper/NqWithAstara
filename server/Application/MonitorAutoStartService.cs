namespace Astra.Server.Application;

public enum MonitorAutoStartOutcome { Disabled, Started, CredentialsMissing, Failed }
public sealed record MonitorAutoStartResult(MonitorAutoStartOutcome Outcome, string? Warning = null);

/// <summary>기동 시 모니터링을 1회 자동 시작한다. 자격 증명 없음·실패는 경고로 남기고 재시도하지 않는다 (#324).</summary>
public sealed class MonitorAutoStartService(MonitorOptions options, MonitorControlService control, IMarketCredentialProbe credentials)
{
    readonly object _gate = new();
    IReadOnlyList<string> _warnings = [];

    public IReadOnlyList<string> Warnings { get { lock (_gate) return _warnings; } }

    public async Task<MonitorAutoStartResult> RunOnceAsync(CancellationToken ct)
    {
        if (!options.AutoStart) return new(MonitorAutoStartOutcome.Disabled);
        if (!await credentials.HasCredentialsAsync(ct))
            return Warn(MonitorAutoStartOutcome.CredentialsMissing, "Monitor:AutoStart가 켜져 있지만 Toss 자격 증명(tossapi.txt)이 없어 모니터링을 자동 시작하지 않았습니다. Start를 직접 누르세요.");
        try
        {
            await control.StartAsync(ct, "auto");
            return new(MonitorAutoStartOutcome.Started);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            try { await control.StopAsync(CancellationToken.None); } catch { /* 롤백 실패는 경고로 충분하다 */ }
            return Warn(MonitorAutoStartOutcome.Failed, $"모니터링 자동 시작에 실패했습니다: {e.Message}. Start를 직접 누르세요.");
        }
    }

    MonitorAutoStartResult Warn(MonitorAutoStartOutcome outcome, string message)
    {
        lock (_gate) _warnings = [message];
        return new(outcome, message);
    }
}
