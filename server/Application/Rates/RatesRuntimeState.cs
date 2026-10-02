namespace Astra.Server.Application.Rates;

/// <summary>금리 수집 런타임 상태. `/api/rates`와 `/api/health`의 rates 블록 원천이다(#316, #325).</summary>
public sealed class RatesRuntimeState(RatesOptions options)
{
    readonly object _gate = new();
    readonly Dictionary<TreasuryTenor, IntradayTenorState> _intraday = new();
    readonly Dictionary<TreasuryTenor, DailyRateSeries> _daily = new();
    readonly Dictionary<string, string> _failedSources = new(StringComparer.Ordinal);
    readonly Dictionary<string, EtfProxyState> _etf = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _etfDivergeRuns = new(StringComparer.Ordinal);
    string _status = "idle";
    DateTimeOffset? _lastRunAt, _lastSuccessAt, _lastDailyRefreshAt, _lastPrunedAt;
    DateOnly? _appendedDay;
    string? _lastError;
    int _appendedToday, _pruned;

    public void MarkSupport(TreasuryTenor tenor, bool supported)
    {
        lock (_gate)
        {
            if (!_intraday.TryGetValue(tenor, out var state)) state = new IntradayTenorState(supported, null, null, null, 0, null);
            _intraday[tenor] = state with { Supported = supported };
        }
    }

    public void RunStarted(DateTimeOffset at) { lock (_gate) { _lastRunAt = at; } }

    public void IntradaySucceeded(TreasuryTenor tenor, IntradayRateQuote quote, DateTimeOffset at)
    {
        lock (_gate)
        {
            _intraday[tenor] = new IntradayTenorState(true, quote, at, null, 0, null);
            _failedSources.Remove(Key("intraday", tenor));
            _lastSuccessAt = at;
            _lastError = null;
            Recompute();
        }
    }

    public void IntradayFailed(TreasuryTenor tenor, string error, DateTimeOffset at)
    {
        lock (_gate)
        {
            var previous = _intraday.TryGetValue(tenor, out var state) ? state : new IntradayTenorState(true, null, null, null, 0, null);
            var failures = previous.Failures + 1;
            var backoff = TimeSpan.FromTicks(Math.Min(options.MaxBackoff.Ticks,
                options.Interval.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 8))));
            _intraday[tenor] = previous with { Error = error, Failures = failures, RetryAt = at + backoff };
            _failedSources[Key("intraday", tenor)] = error;
            _lastError = error;
            Recompute();
        }
    }

    public DateTimeOffset? RetryAt(TreasuryTenor tenor)
    {
        lock (_gate) return _intraday.TryGetValue(tenor, out var state) ? state.RetryAt : null;
    }

    public void DailySucceeded(DailyRateSeries series, DateTimeOffset at)
    {
        lock (_gate)
        {
            _daily[series.Tenor] = series;
            _failedSources.Remove(Key("daily", series.Tenor));
            _lastDailyRefreshAt = at;
            _lastSuccessAt = at;
            Recompute();
        }
    }

    public void DailyFailed(TreasuryTenor tenor, string error)
    {
        lock (_gate)
        {
            _failedSources[Key("daily", tenor)] = error;
            _lastError = error;
            Recompute();
        }
    }

    public void Appended(int count, DateTimeOffset at)
    {
        lock (_gate)
        {
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            if (_appendedDay != day) { _appendedDay = day; _appendedToday = 0; }
            _appendedToday += count;
        }
    }

    public void Pruned(DateTimeOffset at, int files) { lock (_gate) { _lastPrunedAt = at; _pruned = files; } }

    public bool HasDaily(TreasuryTenor tenor) { lock (_gate) return _daily.ContainsKey(tenor); }

    public void RegisterEtf(IEnumerable<EtfProxyDefinition> proxies)
    {
        lock (_gate)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proxy in proxies)
            {
                keep.Add(proxy.Symbol);
                if (!_etf.TryGetValue(proxy.Symbol, out var state)) _etf[proxy.Symbol] = new EtfProxyState(proxy, null, null, null, 0, null);
                else if (state.Proxy != proxy) _etf[proxy.Symbol] = state with { Proxy = proxy };
            }
            foreach (var symbol in _etf.Keys.Where(x => !keep.Contains(x)).ToArray())
            {
                _etf.Remove(symbol);
                _etfDivergeRuns.Remove(symbol);
                _failedSources.Remove(Key("etf", symbol));
            }
        }
    }

    public void EtfSucceeded(EtfProxyDefinition proxy, EtfProxyQuote quote, DateTimeOffset at)
    {
        lock (_gate)
        {
            var state = new EtfProxyState(proxy, quote, at, null, 0, null);
            _etf[proxy.Symbol] = state;
            _failedSources.Remove(Key("etf", proxy.Symbol));
            var evaluated = EtfProxyDivergence.Evaluate(options, state,
                _intraday.TryGetValue(proxy.Tenor, out var rate) ? rate : null, 0);
            _etfDivergeRuns[proxy.Symbol] = evaluated.Agreement == EtfProxyDivergence.Diverge
                ? (_etfDivergeRuns.TryGetValue(proxy.Symbol, out var runs) ? runs : 0) + 1
                : 0;
        }
    }

    public void EtfFailed(EtfProxyDefinition proxy, string error, DateTimeOffset at)
    {
        lock (_gate)
        {
            var previous = _etf.TryGetValue(proxy.Symbol, out var state) ? state : new EtfProxyState(proxy, null, null, null, 0, null);
            var failures = previous.Failures + 1;
            var backoff = TimeSpan.FromTicks(Math.Min(options.MaxBackoff.Ticks,
                options.EtfInterval.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 8))));
            _etf[proxy.Symbol] = previous with { Error = error, Failures = failures, RetryAt = at + backoff };
            _failedSources[Key("etf", proxy.Symbol)] = error;
        }
    }

    public DateTimeOffset? EtfRetryAt(string symbol)
    {
        lock (_gate) return _etf.TryGetValue(symbol, out var state) ? state.RetryAt : null;
    }

    public int EtfDivergeRuns(string symbol)
    {
        lock (_gate) return _etfDivergeRuns.TryGetValue(symbol, out var runs) ? runs : 0;
    }

    public DateTimeOffset? LastDailyRefreshAt { get { lock (_gate) return _lastDailyRefreshAt; } }

    public RatesSnapshotDto Snapshot(DateTimeOffset now)
    {
        lock (_gate)
            return RatesSnapshotBuilder.Build(options, now, options.Enabled ? _status : "disabled",
                new Dictionary<TreasuryTenor, IntradayTenorState>(_intraday),
                new Dictionary<TreasuryTenor, DailyRateSeries>(_daily),
                _etf.Values.ToArray(), new Dictionary<string, int>(_etfDivergeRuns, StringComparer.Ordinal));
    }

    public RatesHealth Health()
    {
        lock (_gate)
            return new RatesHealth(options.Enabled, options.Enabled ? _status : "disabled", _lastRunAt, _lastSuccessAt,
                _lastError, _lastDailyRefreshAt, _appendedToday,
                _failedSources.Select(x => $"{x.Key}: {x.Value}").Order(StringComparer.Ordinal).ToArray(),
                _lastPrunedAt, _pruned, EtfDivergences());
    }

    string[] EtfDivergences()
        => _etf.Values
            .Where(x => _etfDivergeRuns.TryGetValue(x.Proxy.Symbol, out var runs) && runs >= options.EtfDivergenceRuns)
            .OrderBy(x => Array.IndexOf(TreasuryTenors.All, x.Proxy.Tenor)).ThenBy(x => x.Proxy.Symbol, StringComparer.Ordinal)
            .Select(x => EtfProxyDivergence.PersistentWarning(EtfProxyDivergence.Evaluate(options, x,
                _intraday.TryGetValue(x.Proxy.Tenor, out var rate) ? rate : null, _etfDivergeRuns[x.Proxy.Symbol])))
            .ToArray();

    void Recompute()
    {
        var supported = _intraday.Values.Where(x => x.Supported).ToArray();
        var live = supported.Count(x => x.Quote is not null && x.Error is null);
        var anyValue = supported.Any(x => x.Quote is not null) || _daily.Count > 0;
        var coreFailures = _failedSources.Keys.Count(x => !x.StartsWith("etf:", StringComparison.Ordinal));
        _status = !anyValue ? "unavailable"
            : live == supported.Length && _daily.Count == TreasuryTenors.All.Length && coreFailures == 0 ? "ok"
            : "partial";
    }

    static string Key(string kind, TreasuryTenor tenor) => $"{kind}:{tenor.Key()}";

    static string Key(string kind, string symbol) => $"{kind}:{symbol}";
}
