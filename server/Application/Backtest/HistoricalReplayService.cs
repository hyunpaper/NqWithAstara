using System.Collections.Immutable;
using Astra.Server.Domain;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application.Backtest;

public sealed record HistoricalReplayRequest(DateOnly From, DateOnly To);

public sealed record HistoricalReplayExitCounts(int Stop, int Target, int Eod);

public sealed record HistoricalReplaySymbolResult(string Symbol, int Signals, int? VirtualEntries, int? Wins,
    int? Losses, double? PnlPercent, double? AverageHoldingMinutes, HistoricalReplayExitCounts? Exits,
    string TradeReplayStatus, string? UnavailableReason);

public sealed record HistoricalReplayAggregate(int Signals, int? VirtualEntries, int? Wins, int? Losses,
    double? PnlPercent, double? AverageHoldingMinutes, HistoricalReplayExitCounts? Exits);

public sealed record HistoricalReplayQuality(string Symbol, int ExpectedBars, int ActualBars, int Gaps, int Duplicates,
    double MissingRate, bool BenchmarkMissing);

public sealed record HistoricalReplayRun(string Id, DateOnly From, DateOnly To, ImmutableArray<string> Watchlist,
    string Benchmark, string Source, string PolicyHash, string WeightsVersion, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? FailureReason,
    ImmutableArray<HistoricalReplayQuality> DataQuality, HistoricalReplayAggregate? Aggregate,
    ImmutableArray<HistoricalReplaySymbolResult> Symbols, string ResultKind, string Notice);

public sealed record HistoricalReplayStartResult(int HttpStatus, HistoricalReplayRun? Run, string? Message);

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

        var watch = (await _store.Read("watchlist.json", new List<WatchItem>()))
            .Select(x => x.Symbol.Trim().ToUpperInvariant()).Where(x => x.Length > 0 && x != "QQQ")
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (watch.Length == 0) return new(409, null, "관심종목이 비어 있습니다.");

        var now = _clock.GetUtcNow();
        var run = new HistoricalReplayRun(Guid.NewGuid().ToString("N"), request.From, request.To, watch, "QQQ",
            _bars.Name, _structurePolicy.PolicyHash, _weights.WeightsVersion, "queued", now, null, null, [], null, [],
            "historical-virtual", "과거 replay 가상 결과이며 실제 체결 성과가 아닙니다.");
        await SaveAsync(run);
        _ = Task.Run(() => ExecuteAsync(run), CancellationToken.None);
        return new(202, run, null);
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

    async Task ExecuteAsync(HistoricalReplayRun queued)
    {
        var running = queued with { Status = "running" };
        await SaveAsync(running);
        try
        {
            var replayRoot = Path.Combine(_root, "replays", queued.Id);
            var import = await new ReplayBackfill(_bars, _clock).RunAsync(replayRoot, queued.Watchlist,
                queued.Benchmark, queued.From, queued.To, CancellationToken.None);
            var measurement = await new ConfluenceReplay(_barStores.Create(Path.Combine(replayRoot, "bars")),
                _confluencePolicy).RunAsync(queued.From, queued.To, 10, queued.Benchmark, CancellationToken.None);
            var replayStore = _barStores.Create(Path.Combine(replayRoot, "bars"));
            var replayed = await new HistoricalStructureTradeReplay(replayStore, _structurePolicy).RunAsync(
                queued.From, queued.To, queued.Watchlist, CancellationToken.None);
            var symbols = queued.Watchlist.Select(symbol => Result(symbol,
                measurement.BySymbol.TryGetValue(symbol, out var techniques) ? techniques.Sum(x => x.N) : 0,
                replayed.GetValueOrDefault(symbol)))
                .ToImmutableArray();
            var quality = import.Rows.Select(x => new HistoricalReplayQuality(x.Symbol, x.ExpectedBars, x.ActualBars,
                x.Gaps, x.Duplicates, x.MissingRate, x.BenchmarkMissing)).ToImmutableArray();
            var complete = running with
            {
                Status = "completed", CompletedAt = _clock.GetUtcNow(),
                Source = import.Source,
                DataQuality = quality,
                Symbols = symbols,
                Aggregate = Aggregate(symbols)
            };
            await SaveAsync(complete);
        }
        catch (Exception exception)
        {
            await SaveAsync(running with
            {
                Status = "failed", CompletedAt = _clock.GetUtcNow(), FailureReason = exception.Message
            });
        }
    }

    static HistoricalReplaySymbolResult Result(string symbol, int signals, ImmutableArray<SimTrade> trades)
    {
        var closed = trades.Where(x => x.Status != "OPEN" && x.ExitAt.HasValue).ToArray();
        var exits = new HistoricalReplayExitCounts(closed.Count(x => x.Status == "STOP"),
            closed.Count(x => x.Status == "TARGET"), closed.Count(x => x.Status == "EOD"));
        return new HistoricalReplaySymbolResult(symbol, signals, trades.Length,
            closed.Count(x => x.PnlPercent > 0), closed.Count(x => x.PnlPercent <= 0),
            Math.Round(closed.Sum(x => x.PnlPercent ?? 0), 2),
            closed.Length == 0 ? 0 : Math.Round(closed.Average(x => (x.ExitAt!.Value - x.EnteredAt).TotalMinutes), 1),
            exits, "partial", "봉 기반 v5 구조 진입과 STOP/TARGET/EOD만 재현했습니다. 과거 호가·체결 의존 입력은 unavailable입니다.");
    }

    static HistoricalReplayAggregate Aggregate(ImmutableArray<HistoricalReplaySymbolResult> symbols)
    {
        var exits = new HistoricalReplayExitCounts(symbols.Sum(x => x.Exits?.Stop ?? 0),
            symbols.Sum(x => x.Exits?.Target ?? 0), symbols.Sum(x => x.Exits?.Eod ?? 0));
        var entries = symbols.Sum(x => x.VirtualEntries ?? 0);
        return new HistoricalReplayAggregate(symbols.Sum(x => x.Signals), entries,
            symbols.Sum(x => x.Wins ?? 0), symbols.Sum(x => x.Losses ?? 0),
            Math.Round(symbols.Sum(x => x.PnlPercent ?? 0), 2),
            entries == 0 ? 0 : Math.Round(symbols.Sum(x => (x.AverageHoldingMinutes ?? 0) * (x.VirtualEntries ?? 0)) / entries, 1),
            exits);
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
}
