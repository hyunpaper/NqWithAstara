namespace Astra.Server.Application.Rates;

/// <summary>금리 수집 1회 실행 정책. 출처 실패는 만기별로 격리하고 백오프하며, 결과는 jsonl에 append한다(#316).</summary>
public sealed class RatesCollector(
    RatesOptions options,
    IIntradayRateSource intraday,
    IDailyRateSource daily,
    IRateObservationStore store,
    RatesRuntimeState state,
    IMonitorDiagnostics diagnostics,
    TimeProvider clock)
{
    static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    readonly Dictionary<TreasuryTenor, DateTimeOffset> _dailyAttemptAt = new();
    readonly Dictionary<TreasuryTenor, DateOnly> _dailyAppended = new();
    readonly Dictionary<TreasuryTenor, (double Value, DateTimeOffset AsOf)> _intradayAppended = new();
    DateOnly? _dailyDay, _pruneDay;
    bool _restored;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!options.Enabled) return;
        var now = clock.GetUtcNow();
        state.RunStarted(now);
        foreach (var tenor in TreasuryTenors.All) state.MarkSupport(tenor, intraday.Supports(tenor));
        await RestoreAsync(now, ct);
        await PruneIfDueAsync(now, ct);
        var pending = new List<RateObservation>();
        var requests = 0;
        await RefreshDailyAsync(now, pending, ct);
        foreach (var tenor in TreasuryTenors.All.Where(intraday.Supports))
        {
            if (state.RetryAt(tenor) is { } retryAt && retryAt > clock.GetUtcNow()) continue;
            await SpaceAsync(requests++, ct);
            try
            {
                var quote = await intraday.FetchAsync(tenor, ct);
                var at = clock.GetUtcNow();
                state.IntradaySucceeded(tenor, quote, at);
                if (!_intradayAppended.TryGetValue(tenor, out var last) || last.Value != quote.Value || last.AsOf != quote.AsOf)
                {
                    pending.Add(new RateObservation(at, tenor.Key(), quote.Value, quote.Source, quote.AsOf,
                        quote.PreviousClose, RateObservationKinds.Intraday));
                    _intradayAppended[tenor] = (quote.Value, quote.AsOf);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                diagnostics.PollFailed($"rates-intraday:{tenor.Key()}", exception);
                state.IntradayFailed(tenor, Describe(exception), clock.GetUtcNow());
            }
        }
        await AppendAsync(pending, ct);
    }

    public TimeSpan NextDelay(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, NewYork);
        var inWindow = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
                       local.TimeOfDay >= new TimeSpan(7, 0, 0) && local.TimeOfDay < new TimeSpan(17, 30, 0);
        return inWindow ? options.Interval : options.OffHoursInterval;
    }

    async Task RefreshDailyAsync(DateTimeOffset now, List<RateObservation> pending, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, NewYork).DateTime);
        var newDay = _dailyDay != today;
        var requests = 0;
        foreach (var tenor in TreasuryTenors.All)
        {
            var retryDue = !state.HasDaily(tenor) &&
                           (!_dailyAttemptAt.TryGetValue(tenor, out var attempt) || now - attempt >= TimeSpan.FromHours(1));
            if (!newDay && !retryDue) continue;
            _dailyAttemptAt[tenor] = now;
            await SpaceAsync(requests++, ct);
            try
            {
                var series = await daily.FetchAsync(tenor, today.AddDays(-options.DailyLookback), ct);
                var at = clock.GetUtcNow();
                state.DailySucceeded(series, at);
                if (series.Latest is { } latest &&
                    (!_dailyAppended.TryGetValue(tenor, out var appended) || appended != latest.Date))
                {
                    pending.Add(new RateObservation(at, tenor.Key(), latest.Value, series.Source, DailyAsOf(latest.Date),
                        series.Previous?.Value, RateObservationKinds.Daily));
                    _dailyAppended[tenor] = latest.Date;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                diagnostics.PollFailed($"rates-daily:{tenor.Key()}", exception);
                state.DailyFailed(tenor, Describe(exception));
            }
        }
        if (newDay) _dailyDay = today;
    }

    async Task RestoreAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_restored) return;
        _restored = true;
        try
        {
            var rows = await store.ReadDayAsync(DateOnly.FromDateTime(now.UtcDateTime), ct);
            foreach (var group in rows.Where(x => x.Kind == RateObservationKinds.Intraday).GroupBy(x => x.Tenor))
            {
                if (TreasuryTenors.Parse(group.Key) is not { } tenor || !intraday.Supports(tenor)) continue;
                var last = group.OrderBy(x => x.At).Last();
                state.IntradaySucceeded(tenor, new IntradayRateQuote(tenor, "", last.Value, last.PreviousClose, last.AsOf,
                    last.Source), last.At);
                _intradayAppended[tenor] = (last.Value, last.AsOf);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { diagnostics.PollFailed("rates-restore", exception); }
    }

    async Task PruneIfDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (_pruneDay == day) return;
        try
        {
            state.Pruned(now, await store.PruneAsync(now, options.Retention, ct));
            _pruneDay = day;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { diagnostics.PollFailed("rates-retention", exception); }
    }

    async Task AppendAsync(List<RateObservation> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;
        try
        {
            await store.AppendAsync(pending, ct);
            state.Appended(pending.Count, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { diagnostics.PollFailed("rates-store", exception); }
    }

    Task SpaceAsync(int index, CancellationToken ct)
        => index == 0 || options.RequestSpacing <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(options.RequestSpacing, ct);

    public static DateTimeOffset DailyAsOf(DateOnly date)
    {
        var close = date.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(close, NewYork.GetUtcOffset(close));
    }

    static string Describe(Exception exception)
        => exception is HttpRequestException { StatusCode: { } code } ? $"HTTP {(int)code}" : exception.GetType().Name;
}
