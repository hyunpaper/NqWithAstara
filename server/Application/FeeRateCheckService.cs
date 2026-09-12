using System.Globalization;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

/// <summary>
/// 이슈 #130: 실계좌 US 왕복 수수료와 <see cref="StructurePolicy.RoundTripFeePercent"/> 정합성을 확인한다.
/// 정책 값은 절대 바꾸지 않는다 — PolicyHash가 바뀌면 구조 엔진 래치가 세션 중 리셋된다(§16A).
/// 계좌 조회 실패는 경고가 아니라 진단 로그 note로만 남기고 진입을 막지 않는다.
/// </summary>
public sealed class FeeRateCheckService(IMarketDataGateway gateway, StructurePolicy policy, TimeProvider clock, IMonitorDiagnostics diagnostics)
{
    const string BrokerageAccountType = "BROKERAGE";
    const string UsMarketCountry = "US";
    const double MismatchTolerance = .0001;
    static readonly TimeSpan ExpiryWindow = TimeSpan.FromDays(3);

    readonly object _gate = new();
    IReadOnlyList<string> _warnings = [];
    bool _startupChecked;
    DateTimeOffset? _lastSessionStart;

    public IReadOnlyList<string> Warnings { get { lock (_gate) return _warnings; } }

    public Task CheckOnMonitorStartAsync(CancellationToken ct)
    {
        lock (_gate) { if (_startupChecked) return Task.CompletedTask; _startupChecked = true; }
        return RunAsync(ct);
    }

    public Task CheckOnSessionEntryAsync(DateTimeOffset sessionStart, CancellationToken ct)
    {
        lock (_gate) { if (_lastSessionStart == sessionStart) return Task.CompletedTask; _lastSessionStart = sessionStart; }
        return RunAsync(ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var accounts = await gateway.Accounts(ct);
            var account = accounts.FirstOrDefault(x => x.AccountType == BrokerageAccountType);
            if (account is null)
            {
                diagnostics.MarketDataFailed("ACCOUNT", "fee-rate-check", new InvalidOperationException("FEE_RATE_CHECK_UNAVAILABLE: no brokerage account"));
                SetWarnings([]);
                return;
            }

            var commissions = await gateway.Commissions(account.AccountSeq, ct);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var row = commissions
                .Where(x => x.MarketCountry == UsMarketCountry && (x.StartDate is null || x.StartDate <= today) && (x.EndDate is null || x.EndDate >= today))
                .OrderBy(x => x.EndDate ?? DateOnly.MaxValue)
                .FirstOrDefault();

            var warnings = new List<string>();
            if (row is null)
            {
                warnings.Add("V5_FEE_RATE_EXPIRING:none");
            }
            else
            {
                var roundTrip = row.Rate * 2 * 100;
                if (Math.Abs((double)roundTrip - policy.RoundTripFeePercent) > MismatchTolerance)
                    warnings.Add($"V5_FEE_RATE_MISMATCH:policy={policy.RoundTripFeePercent.ToString(CultureInfo.InvariantCulture)};account={roundTrip.ToString(CultureInfo.InvariantCulture)}");
                if (row.EndDate is { } end && end.ToDateTime(TimeOnly.MinValue) - today.ToDateTime(TimeOnly.MinValue) <= ExpiryWindow)
                    warnings.Add($"V5_FEE_RATE_EXPIRING:{end:yyyy-MM-dd}");
            }
            SetWarnings(warnings);
        }
        catch (Exception ex)
        {
            diagnostics.MarketDataFailed("ACCOUNT", "fee-rate-check", ex);
            SetWarnings([]);
        }
    }

    void SetWarnings(IReadOnlyList<string> warnings) { lock (_gate) _warnings = warnings; }
}
