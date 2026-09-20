using System.Collections.Immutable;
using System.Collections.Concurrent;
using Astra.Server.Domain;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

/// <summary>Symbols가 지정되면 운영 관심종목을 읽지 않고 해당 심볼만 replay한다(OOS 비교용).</summary>
public sealed record HistoricalReplayRequest(DateOnly From, DateOnly To, IReadOnlyList<string>? Symbols = null);

public sealed record HistoricalReplayExitCounts(int Stop, int Target, int Eod);

public sealed record HistoricalReplaySymbolResult(string Symbol, int? Signals, int? VirtualEntries, int? Wins,
    int? Losses, double? PnlPercent, double? AverageHoldingMinutes, HistoricalReplayExitCounts? Exits,
    string TradeReplayStatus, string? UnavailableReason, double? GrossPnlPercent = null,
    double? FeePercent = null, double? SlippagePercent = null);

public sealed record HistoricalReplayAggregate(int? Signals, int? VirtualEntries, int? Wins, int? Losses,
    double? PnlPercent, double? AverageHoldingMinutes, HistoricalReplayExitCounts? Exits,
    double? GrossPnlPercent = null, double? FeePercent = null, double? SlippagePercent = null);

public sealed record HistoricalReplayQuality(string Symbol, int ExpectedBars, int ActualBars, int Gaps, int Duplicates,
    double MissingRate, bool BenchmarkMissing);

public sealed record HistoricalReplaySourceQuality(string Symbol, int RawBars, int ActualTradingDays,
    bool ReachedRequestedStart, DateTimeOffset? OldestBar, string DataStatus, string? Reason,
    DateTimeOffset? NewestBar = null, int RequiredDailySeed = 0, int AvailableDailySeed = 0,
    string CarryPolicy = "session-reset");

public sealed record HistoricalReplayTradeResult(SimTrade Trade, double? GrossPnlPercent,
    double FeePercent, double? SlippagePercent, double? NetPnlPercent);

public sealed record HistoricalReplayRun(string Id, DateOnly From, DateOnly To, ImmutableArray<string> Watchlist,
    string Benchmark, string Source, string PolicyHash, string WeightsVersion, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? FailureReason,
    ImmutableArray<HistoricalReplayQuality> DataQuality, HistoricalReplayAggregate? Aggregate,
    ImmutableArray<HistoricalReplaySymbolResult> Symbols, string ResultKind, string Notice,
    string DataStatus = "unknown", string? DataReason = null,
    ImmutableArray<HistoricalReplaySourceQuality>? SourceQuality = null);

public sealed record HistoricalReplayStartResult(int HttpStatus, HistoricalReplayRun? Run, string? Message);

public sealed record HistoricalReplayCancelResult(int HttpStatus, HistoricalReplayRun? Run, string? Message);

public sealed class HistoricalReplayService
{
    const string RunsFile = "replay-runs.json";
    const int MaxDays = 90;
    readonly ILocalStore _store;
    readonly IHistoricalBarSource _bars;
    readonly TimeProvider _clock;
    readonly string _root;
    readonly StructurePolicy _structurePolicy;
    readonly ConfluencePolicy _confluencePolicy;
    readonly ConfluenceWeightsDocument _weights;
    readonly IReplayBarStoreFactory _barStores;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ConcurrentDictionary<string, ReplayWork> _work = new(StringComparer.OrdinalIgnoreCase);

    public HistoricalReplayService(ILocalStore store, IHistoricalBarSource bars, TimeProvider clock, IReplayWorkspace workspace,
        StructurePolicy structurePolicy, ConfluencePolicy confluencePolicy, ConfluenceWeightsDocument weights,
        IReplayBarStoreFactory barStores)
    {
        _store = store;
        _bars = bars;
        _clock = clock;
        _root = workspace.Root;
        _structurePolicy = structurePolicy;
        _confluencePolicy = confluencePolicy;
        _weights = weights;
        _barStores = barStores;
    }

    public async Task<HistoricalReplayStartResult> StartAsync(HistoricalReplayRequest? request,
        CancellationToken ct)
    {
        if (request is null || request.To < request.From || request.From > DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime)
            || request.To.DayNumber - request.From.DayNumber + 1 > MaxDays)
            return new(400, null, $"기간은 오늘 이전의 1~{MaxDays}일 범위여야 합니다.");

        var requested = request.Symbols ?? Array.Empty<string>();
        var watch = (requested.Count > 0 ? requested.Select(x => new WatchItem(x, x))
            : await _store.Read("watchlist.json", new List<WatchItem>()))
            .Select(x => x.Symbol.Trim().ToUpperInvariant()).Where(x => x.Length > 0 && x != "QQQ")
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (watch.Length == 0) return new(409, null, "관심종목이 비어 있습니다.");

        var now = _clock.GetUtcNow();
        var run = new HistoricalReplayRun(Guid.NewGuid().ToString("N"), request.From, request.To, watch, "QQQ",
            _bars.Name, _structurePolicy.PolicyHash, _weights.WeightsVersion, "queued", now, null, null, [], null, [],
            "historical-virtual", "과거 replay 가상 결과이며 실제 체결 성과가 아닙니다.");
        await SaveAsync(run);
        var cancellation = new CancellationTokenSource();
        var work = new ReplayWork(cancellation);
        if (!_work.TryAdd(run.Id, work))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("과거 replay 작업 ID가 중복되었습니다.");
        }
        _ = Task.Run(() => ExecuteAsync(run, work), CancellationToken.None);
        return new(202, run, null);
    }

    public async Task<HistoricalReplayCancelResult> CancelAsync(string id)
    {
        var run = await UpdateAsync(id, current => current.Status is "queued" or "running"
            ? current with { Status = "canceling" }
            : current);
        if (run is null) return new(404, null, "과거 replay 작업을 찾을 수 없습니다.");
        if (run.Status is "completed" or "no-data" or "failed" or "canceled") return new(200, run, null);

        if (_work.TryGetValue(id, out var work))
        {
            work.Cancellation.Cancel();
            return new(200, run, null);
        }

        await DeleteReplayDirectoryAsync(id);
        var canceled = await UpdateAsync(id, current => current.Status == "canceling"
            ? current with { Status = "canceled", CompletedAt = _clock.GetUtcNow(), FailureReason = null }
            : current);
        return new(200, canceled, null);
    }

    public async Task<HistoricalReplayRun?> GetAsync(string id)
    {
        var runs = await _store.Read(RunsFile, new List<HistoricalReplayRun>());
        return runs.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<HistoricalReplayRun?> LatestAsync()
    {
        var runs = await _store.Read(RunsFile, new List<HistoricalReplayRun>());
        return runs.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
    }

    public async Task<IReadOnlyList<HistoricalReplayTradeResult>?> TradesAsync(string id, CancellationToken ct)
    {
        if (await GetAsync(id) is null) return null;
        var path = Path.Combine(ReplayDirectory(id), "trades.jsonl");
        if (!File.Exists(path)) return Array.Empty<HistoricalReplayTradeResult>();
        var rows = new List<HistoricalReplayTradeResult>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
            if (System.Text.Json.JsonSerializer.Deserialize<HistoricalReplayTradeResult>(line) is { } row) rows.Add(row);
        return rows;
    }

    public async Task<IReadOnlyList<HistoricalReplayDiagnosticTrade>?> DiagnosticTradesAsync(string id, CancellationToken ct)
    {
        if (await GetAsync(id) is null) return null;
        var path = Path.Combine(ReplayDirectory(id), "trades.jsonl");
        if (!File.Exists(path)) return Array.Empty<HistoricalReplayDiagnosticTrade>();
        var rows = new List<HistoricalReplayDiagnosticTrade>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
            if (HistoricalReplayDiagnosticTrade.TryRead(line, out var row) && row is not null) rows.Add(row);
        return rows;
    }

    async Task ExecuteAsync(HistoricalReplayRun queued, ReplayWork work)
    {
        try
        {
            var running = await UpdateAsync(queued.Id, current => current.Status == "queued"
                ? current with { Status = "running" }
                : current);
            work.Cancellation.Token.ThrowIfCancellationRequested();
            if (running?.Status != "running") return;
            var replayRoot = ReplayDirectory(queued.Id);
            var import = await new ReplayBackfill(_bars, _clock).RunAsync(replayRoot, queued.Watchlist,
                queued.Benchmark, queued.From, queued.To, work.Cancellation.Token);
            var sourceQuality = import.Sources.Select(x => new HistoricalReplaySourceQuality(x.Symbol, x.RawBars,
                x.ActualTradingDays, x.ReachedRequestedStart, x.OldestBar, x.DataStatus, x.Reason, x.NewestBar,
                _structurePolicy.DailyLookbackSessions, Math.Min(x.ActualTradingDays, _structurePolicy.DailyLookbackSessions)))
                .ToImmutableArray();
            if (import.DataStatus == "no-data")
            {
                var unavailable = queued.Watchlist.Select(symbol => Unavailable(symbol,
                    import.Sources.FirstOrDefault(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase))?.Reason
                    ?? import.DataReason)).ToImmutableArray();
                await UpdateAsync(queued.Id, current => current.Status == "running" ? current with
                {
                    Status = "no-data", CompletedAt = _clock.GetUtcNow(), Source = import.Source,
                    FailureReason = import.DataReason, DataStatus = import.DataStatus, DataReason = import.DataReason,
                    SourceQuality = sourceQuality, Aggregate = null, Symbols = unavailable
                } : current);
                return;
            }
            var measurement = await new ConfluenceReplay(_barStores.Create(Path.Combine(replayRoot, "bars")),
                _confluencePolicy).RunAsync(queued.From, queued.To, 10, queued.Benchmark, work.Cancellation.Token);
            var replayStore = _barStores.Create(Path.Combine(replayRoot, "bars"));
            var replayed = await new HistoricalStructureTradeReplay(replayStore, _structurePolicy).RunAsync(
                queued.From, queued.To, queued.Watchlist, work.Cancellation.Token);
            await WriteTradesAsync(replayRoot, replayed.Values.SelectMany(x => x), work.Cancellation.Token);
            var symbols = queued.Watchlist.Select(symbol =>
                import.Sources.First(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).DataStatus == "no-data"
                ? Unavailable(symbol, import.Sources.First(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).Reason)
                : Result(symbol, measurement.BySymbol.TryGetValue(symbol, out var techniques) ? techniques.Sum(x => x.N) : 0,
                    replayed.GetValueOrDefault(symbol)))
                .ToImmutableArray();
            var quality = import.Rows.Select(x => new HistoricalReplayQuality(x.Symbol, x.ExpectedBars, x.ActualBars,
                x.Gaps, x.Duplicates, x.MissingRate, x.BenchmarkMissing)).ToImmutableArray();
            work.Cancellation.Token.ThrowIfCancellationRequested();
            await UpdateAsync(queued.Id, current => current.Status == "running" ? current with
            {
                Status = "completed", CompletedAt = _clock.GetUtcNow(),
                Source = import.Source,
                DataStatus = "partial",
                DataReason = "과거 호가·체결과 slippage 입력이 없어 운영 성능 결론에 사용할 수 없습니다.",
                Notice = "과거 replay 가상 결과이며 입력이 부분적이므로 운영 성능 결론에 사용할 수 없습니다.",
                SourceQuality = sourceQuality,
                DataQuality = quality,
                Symbols = symbols,
                Aggregate = Aggregate(symbols)
            } : current);
        }
        catch (Exception) when (work.Cancellation.IsCancellationRequested)
        {
            await DeleteReplayDirectoryAsync(queued.Id);
            await UpdateAsync(queued.Id, current => current.Status is "queued" or "running" or "canceling"
                ? current with { Status = "canceled", CompletedAt = _clock.GetUtcNow(), FailureReason = null,
                    DataQuality = [], Aggregate = null, Symbols = [] }
                : current);
        }
        catch (Exception exception)
        {
            await UpdateAsync(queued.Id, current => current.Status is "queued" or "running"
                ? current with { Status = "failed", CompletedAt = _clock.GetUtcNow(), FailureReason = exception.Message }
                : current);
        }
        finally
        {
            _work.TryRemove(new KeyValuePair<string, ReplayWork>(queued.Id, work));
            work.Cancellation.Dispose();
            work.Completion.TrySetResult();
        }
    }

    static HistoricalReplaySymbolResult Unavailable(string symbol, string? reason) =>
        new(symbol, null, null, null, null, null, null, null, "unavailable",
            reason ?? "요청 기간의 종목 데이터가 없습니다.");

    static HistoricalReplaySymbolResult Result(string symbol, int signals, ImmutableArray<SimTrade> trades)
    {
        var closed = trades.Where(x => x.Status != "OPEN" && x.ExitAt.HasValue).ToArray();
        var exits = new HistoricalReplayExitCounts(closed.Count(x => x.Status == "STOP"),
            closed.Count(x => x.Status == "TARGET"), closed.Count(x => x.Status == "EOD"));
        var gross = closed.Sum(x => x.ExitPrice.HasValue ? (x.ExitPrice.Value / x.EntryPrice - 1) * 100 : 0);
        var fees = closed.Sum(x => x.PnlPercent.HasValue && x.ExitPrice.HasValue
            ? (x.ExitPrice.Value / x.EntryPrice - 1) * 100 - x.PnlPercent.Value
            : 0);
        return new HistoricalReplaySymbolResult(symbol, signals, trades.Length,
            closed.Count(x => x.PnlPercent > 0), closed.Count(x => x.PnlPercent <= 0),
            Math.Round(closed.Sum(x => x.PnlPercent ?? 0), 2),
            closed.Length == 0 ? 0 : Math.Round(closed.Average(x => (x.ExitAt!.Value - x.EnteredAt).TotalMinutes), 1),
            exits, "partial", "봉 기반 v5 구조 진입과 STOP/TARGET/EOD만 재현했습니다. 과거 호가·체결과 slippage는 unavailable입니다.",
            Math.Round(gross, 2), Math.Round(fees, 2), null);
    }

    static HistoricalReplayAggregate Aggregate(ImmutableArray<HistoricalReplaySymbolResult> symbols)
    {
        var exits = new HistoricalReplayExitCounts(symbols.Sum(x => x.Exits?.Stop ?? 0),
            symbols.Sum(x => x.Exits?.Target ?? 0), symbols.Sum(x => x.Exits?.Eod ?? 0));
        var entries = symbols.Sum(x => x.VirtualEntries ?? 0);
        return new HistoricalReplayAggregate(symbols.Any(x => x.Signals.HasValue) ? symbols.Sum(x => x.Signals ?? 0) : null, entries,
            symbols.Sum(x => x.Wins ?? 0), symbols.Sum(x => x.Losses ?? 0),
            Math.Round(symbols.Sum(x => x.PnlPercent ?? 0), 2),
            entries == 0 ? 0 : Math.Round(symbols.Sum(x => (x.AverageHoldingMinutes ?? 0) * (x.VirtualEntries ?? 0)) / entries, 1),
            exits, Math.Round(symbols.Sum(x => x.GrossPnlPercent ?? 0), 2),
            Math.Round(symbols.Sum(x => x.FeePercent ?? 0), 2), null);
    }

    static async Task WriteTradesAsync(string replayRoot, IEnumerable<SimTrade> trades, CancellationToken ct)
    {
        Directory.CreateDirectory(replayRoot);
        var rows = trades.OrderBy(x => x.EnteredAt).ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.Id, StringComparer.Ordinal).Select(x =>
            {
                var gross = x.ExitPrice.HasValue ? Math.Round((x.ExitPrice.Value / x.EntryPrice - 1) * 100, 6) : (double?)null;
                var fee = gross.HasValue && x.PnlPercent.HasValue ? Math.Round(gross.Value - x.PnlPercent.Value, 6) : 0;
                return System.Text.Json.JsonSerializer.Serialize(new HistoricalReplayTradeResult(
                    x, gross, fee, null, x.PnlPercent));
            });
        await File.WriteAllLinesAsync(Path.Combine(replayRoot, "trades.jsonl"), rows, ct);
    }

    async Task SaveAsync(HistoricalReplayRun run)
    {
        await _gate.WaitAsync();
        try
        {
            var runs = await _store.Read(RunsFile, new List<HistoricalReplayRun>());
            runs.RemoveAll(x => x.Id == run.Id);
            runs.Add(run);
            await _store.Write(RunsFile, runs.OrderByDescending(x => x.CreatedAt).Take(20).ToList());
        }
        finally { _gate.Release(); }
    }

    async Task<HistoricalReplayRun?> UpdateAsync(string id,
        Func<HistoricalReplayRun, HistoricalReplayRun> update)
    {
        await _gate.WaitAsync();
        try
        {
            var runs = await _store.Read(RunsFile, new List<HistoricalReplayRun>());
            var index = runs.FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            runs[index] = update(runs[index]);
            await _store.Write(RunsFile, runs.OrderByDescending(x => x.CreatedAt).Take(20).ToList());
            return runs[index];
        }
        finally { _gate.Release(); }
    }

    string ReplayDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains(Path.DirectorySeparatorChar) || id.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidOperationException("유효하지 않은 replay 작업 ID입니다.");
        var replayRoot = Path.GetFullPath(Path.Combine(_root, "replays"));
        var path = Path.GetFullPath(Path.Combine(replayRoot, id));
        var prefix = replayRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("replay 작업 경로가 작업 루트를 벗어났습니다.");
        return path;
    }

    Task DeleteReplayDirectoryAsync(string id)
    {
        var path = ReplayDirectory(id);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var work = _work.Values.ToArray();
        foreach (var item in work) item.Cancellation.Cancel();
        try { await Task.WhenAll(work.Select(x => x.Completion.Task)).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    sealed class ReplayWork(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
