using System.Collections.Immutable;
using System.Collections.Concurrent;
using Astra.Server.Domain;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;
using Astra.Server.Domain.Validation;

namespace Astra.Server.Application.Backtest;

/// <summary>Symbols가 지정되면 운영 관심종목을 읽지 않고 해당 심볼만 replay한다(OOS 비교용).</summary>
public static class HistoricalReplayCostPolicies
{
    public const string RejectMissing = "reject-missing";
    public const string ModeledV2 = "modeled-v2";
    public static bool IsSupported(string value) => value is RejectMissing or ModeledV2;
}

public sealed record HistoricalReplayRequest(DateOnly From, DateOnly To, IReadOnlyList<string>? Symbols = null,
    string MissingCostPolicy = HistoricalReplayCostPolicies.ModeledV2);

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

public sealed record HistoricalReplayCostResult(string Basis, string Source, bool Available,
    string MissingCostPolicy, string? UnavailableReason, HistoricalReplayAggregate? Aggregate,
    ImmutableArray<HistoricalReplaySymbolResult> Symbols,
    HistoricalStructureTradeReplay.ReplayGateSummary GateSummary);

public sealed record HistoricalReplaySelectionDiagnostics(string Version, DateOnly TrainFrom, DateOnly TrainTo,
    DateOnly EvaluationFrom, DateOnly EvaluationTo, int TrainingRows, int RequiredTrainingRows,
    double? SelectedThreshold, string Status, string Basis);

public sealed record HistoricalReplayRun(string Id, DateOnly From, DateOnly To, ImmutableArray<string> Watchlist,
    string Benchmark, string Source, string PolicyHash, string WeightsVersion, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? FailureReason,
    ImmutableArray<HistoricalReplayQuality> DataQuality, HistoricalReplayAggregate? Aggregate,
    ImmutableArray<HistoricalReplaySymbolResult> Symbols, string ResultKind, string Notice,
    string DataStatus = "unknown", string? DataReason = null,
    ImmutableArray<HistoricalReplaySourceQuality>? SourceQuality = null,
    string? CostProfile = null, double? ExpectedValueThreshold = null,
    int? ExpectedValueTrainingRows = null,
    ImmutableArray<HistoricalStructureTradeReplay.ReplaySourceCoverage>? ReplayCoverage = null,
    HistoricalStructureTradeReplay.ReplayGateSummary? StrategyGateSummary = null,
    string? TimeframeNotice = null, string SelectedCostPolicy = HistoricalReplayCostPolicies.ModeledV2,
    ImmutableArray<HistoricalReplayCostResult>? CostResults = null,
    HistoricalReplaySelectionDiagnostics? SelectionDiagnostics = null,
    RegimeValidationReport? RegimeValidation = null,
    ProbabilityCalibrationReport? ProbabilityCalibration = null,
    ConditionalReturnModelEvaluation? ConditionalReturnModelEvaluation = null);

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
        if (!HistoricalReplayCostPolicies.IsSupported(request.MissingCostPolicy))
            return new(400, null, "비용 결측 정책은 reject-missing 또는 modeled-v2여야 합니다.");

        var requested = request.Symbols ?? Array.Empty<string>();
        var watch = (requested.Count > 0 ? requested.Select(x => new WatchItem(x, x))
            : await _store.Read("watchlist.json", new List<WatchItem>()))
            .Select(x => x.Symbol.Trim().ToUpperInvariant()).Where(x => x.Length > 0 && x != "QQQ")
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (watch.Length == 0) return new(409, null, "관심종목이 비어 있습니다.");

        var now = _clock.GetUtcNow();
        var run = new HistoricalReplayRun(Guid.NewGuid().ToString("N"), request.From, request.To, watch, "QQQ",
            _bars.Name, _structurePolicy.PolicyHash, _weights.WeightsVersion, "queued", now, null, null, [], null, [],
            "historical-virtual", "과거 replay 가상 결과이며 실제 체결 성과가 아닙니다.",
            SelectedCostPolicy: request.MissingCostPolicy);
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
            var costModel = HistoricalReplayCostModel.ConservativeDefault;
            var costSource = new ModeledHistoricalLiquiditySource(costModel);
            var modelPolicy = _structurePolicy with
            {
                RequireCompleteLiquidityCost = true,
                ShortBorrowCostPercent = costModel.ShortBorrowPercent
            };
            var trainDays = Math.Max(1, (queued.To.DayNumber - queued.From.DayNumber + 1) / 2);
            var trainTo = queued.From.AddDays(trainDays - 1);
            var evaluationFrom = trainTo < queued.To ? trainTo.AddDays(1) : queued.To;
            var trainingRun = await new HistoricalStructureTradeReplay(replayStore, modelPolicy, costSource,
                    queued.Benchmark)
                .RunDetailedAsync(queued.From, trainTo, queued.Watchlist, work.Cancellation.Token);
            var observations = trainingRun.Candidates
                .Where(x => x.CostComplete)
                .Select(x => x.ExpectedNetR is { } feature && x.RealizedNetR is { } realized
                    ? new ExpectedValueObservation(x.SignalAt, x.Regime, feature, realized,
                        x.CostComplete, x.Side, x.Regime)
                    : null)
                .Where(x => x is not null).Select(x => x!).ToArray();
            ExpectedValueThreshold? selectedThreshold = null;
            var requiredTrainingRows = ReplaySelectionPolicy.RequiredTrainingRows(trainingRun.Coverage);
            var replayPolicy = modelPolicy;
            if (observations.Length >= requiredTrainingRows)
            {
                selectedThreshold = WalkForwardExpectedValue.Select(observations,
                    [0d, .25d, .5d, .75d, 1d], minimumRows: requiredTrainingRows);
                replayPolicy = WalkForwardExpectedValue.ApplyToPolicy(modelPolicy, selectedThreshold);
            }
            var modeledRun = await new HistoricalStructureTradeReplay(replayStore, replayPolicy, costSource,
                    queued.Benchmark)
                .RunDetailedAsync(evaluationFrom, queued.To, queued.Watchlist, work.Cancellation.Token);
            var observedPolicy = replayPolicy with { RequireCompleteLiquidityCost = true, ShortBorrowCostPercent = null };
            var observedRun = await new HistoricalStructureTradeReplay(replayStore, observedPolicy,
                    benchmarkSymbol: queued.Benchmark)
                .RunDetailedAsync(evaluationFrom, queued.To, queued.Watchlist, work.Cancellation.Token);
            var selectedRun = queued.SelectedCostPolicy == HistoricalReplayCostPolicies.ModeledV2
                ? modeledRun : observedRun;
            var modelEvaluation = ConditionalReturnWalkForwardTrainer.Evaluate(
                BuildConditionalModelSamples(trainingRun), BuildConditionalModelSamples(selectedRun),
                _clock.GetUtcNow());
            var replayed = selectedRun.Trades;
            await WriteTradesAsync(replayRoot, replayed.Values.SelectMany(x => x), work.Cancellation.Token);
            await WriteModelEvaluationAsync(replayRoot, modelEvaluation, work.Cancellation.Token);
            ImmutableArray<HistoricalReplaySymbolResult> Results(
                HistoricalStructureTradeReplay.ReplayRun run) => queued.Watchlist.Select(symbol =>
                import.Sources.First(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).DataStatus == "no-data"
                ? Unavailable(symbol, import.Sources.First(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).Reason)
                : Result(symbol, measurement.BySymbol.TryGetValue(symbol, out var techniques) ? techniques.Sum(x => x.N) : 0,
                    run.Trades.GetValueOrDefault(symbol)))
                .ToImmutableArray();
            var modeledSymbols = Results(modeledRun);
            var observedUnavailable = queued.Watchlist.Select(symbol => Unavailable(symbol,
                "과거 bid/ask·잔량·slippage 관측이 없어 observed-cost net 성과를 계산하지 않습니다."))
                .ToImmutableArray();
            var symbols = queued.SelectedCostPolicy == HistoricalReplayCostPolicies.ModeledV2
                ? modeledSymbols : observedUnavailable;
            var costResults = ImmutableArray.Create(
                new HistoricalReplayCostResult("modeled", costModel.Version, true,
                    HistoricalReplayCostPolicies.ModeledV2, null, Aggregate(modeledSymbols), modeledSymbols,
                    modeledRun.GateSummary),
                new HistoricalReplayCostResult("observed", "OBSERVED_ORDERBOOK", false,
                    HistoricalReplayCostPolicies.RejectMissing,
                    "과거 bid/ask·잔량·slippage 관측이 없어 net PnL을 산출하지 않습니다.", null,
                    observedUnavailable, observedRun.GateSummary));
            var quality = import.Rows.Select(x => new HistoricalReplayQuality(x.Symbol, x.ExpectedBars, x.ActualBars,
                x.Gaps, x.Duplicates, x.MissingRate, x.BenchmarkMissing)).ToImmutableArray();
            work.Cancellation.Token.ThrowIfCancellationRequested();
            await UpdateAsync(queued.Id, current => current.Status == "running" ? current with
            {
                Status = "completed", CompletedAt = _clock.GetUtcNow(),
                Source = import.Source,
                DataStatus = "partial",
                DataReason = "관측 비용은 결측이며 modeled-v2와 reject-missing 결과를 분리했습니다.",
                Notice = "선택한 비용 결측 정책의 과거 가상 결과이며 실제 체결 성과가 아닙니다.",
                SourceQuality = sourceQuality,
                DataQuality = quality,
                Symbols = symbols,
                Aggregate = queued.SelectedCostPolicy == HistoricalReplayCostPolicies.ModeledV2
                    ? Aggregate(symbols) : null,
                PolicyHash = replayPolicy.PolicyHash,
                CostProfile = queued.SelectedCostPolicy == HistoricalReplayCostPolicies.ModeledV2
                    ? costModel.Version : "OBSERVED_ORDERBOOK_UNAVAILABLE",
                ExpectedValueThreshold = selectedThreshold?.Value,
                ExpectedValueTrainingRows = observations.Length,
                ReplayCoverage = selectedRun.Coverage,
                StrategyGateSummary = selectedRun.GateSummary,
                TimeframeNotice = selectedRun.Coverage.Any(x => x.GranularityStatus == "unsupported" ||
                    x.GranularityStatus == "mixed-unsupported")
                    ? "지원하지 않는 원천 주기는 성과 집계에서 제외했습니다."
                    : selectedRun.Coverage.Any(x => x.GranularityStatus == "native-supported")
                        ? "5분 원천은 native-five-minute-v1 경로로 계산했으며 운영 1분 패리티가 아닙니다."
                        : null,
                CostResults = costResults,
                SelectionDiagnostics = new HistoricalReplaySelectionDiagnostics(ReplaySelectionPolicy.Version,
                    queued.From, trainTo, evaluationFrom, queued.To, observations.Length, requiredTrainingRows,
                    selectedThreshold?.Value,
                    selectedThreshold is null ? "insufficient-training-sample" : "selected-from-training-only",
                    $"훈련 구간만 사용, 최소 {ReplaySelectionPolicy.MinimumTrainingRows}행 및 " +
                    $"{ReplaySelectionPolicy.SymbolSessionsPerRequiredEntry} symbol-session당 1행"),
                RegimeValidation = BuildRegimeValidation(selectedRun, queued.From, trainTo,
                    evaluationFrom, queued.To),
                ProbabilityCalibration = modelEvaluation.Validation?.Calibration ??
                    BuildProbabilityCalibration(selectedRun, _clock.GetUtcNow()),
                ConditionalReturnModelEvaluation = modelEvaluation
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

    static RegimeValidationReport BuildRegimeValidation(HistoricalStructureTradeReplay.ReplayRun run,
        DateOnly trainFrom, DateOnly trainTo, DateOnly evaluationFrom, DateOnly evaluationTo)
    {
        if (trainTo >= evaluationFrom)
            return new(RegimeClassification.Unavailable, trainFrom, trainTo, evaluationFrom, evaluationTo, 0, [],
                [RegimeValidationEvaluator.OutOfSampleWindowUnavailable]);
        var trades = run.Trades.Values.SelectMany(x => x)
            .Where(x => x.Structure?.EntryEventId is not null)
            .GroupBy(x => x.Structure!.EntryEventId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var samples = run.Candidates.Select(candidate =>
        {
            trades.TryGetValue(candidate.EventId, out var trade);
            double? stopDistance = trade is null || trade.EntryPrice <= 0
                ? null : Math.Abs(trade.EntryPrice - trade.Stop) / trade.EntryPrice * 100;
            double? targetDistance = trade is null || trade.EntryPrice <= 0
                ? null : Math.Abs(trade.Target - trade.EntryPrice) / trade.EntryPrice * 100;
            return new RegimeValidationSample(candidate.EventId, candidate.Symbol, candidate.SessionDate,
                candidate.SignalAt, candidate.Regime == "UNCOLLECTED" ? null : candidate.Regime,
                trade?.Kind ?? "UNCOLLECTED", candidate.FinalApproved, trade is not null,
                trade is { Status: not "OPEN" } ? trade.Status : null, stopDistance, targetDistance,
                trade?.PnlPercent);
        }).ToArray();
        return RegimeValidationEvaluator.Evaluate(samples, trainFrom, trainTo, evaluationFrom, evaluationTo);
    }

    static ProbabilityCalibrationReport BuildProbabilityCalibration(
        HistoricalStructureTradeReplay.ReplayRun run, DateTimeOffset asOf)
    {
        var trades = run.Trades.Values.SelectMany(x => x)
            .Where(x => x.Structure?.EntryEventId is not null && x.ExitAt is not null && x.Status != "OPEN")
            .GroupBy(x => x.Structure!.EntryEventId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(t => t.EnteredAt).First(), StringComparer.Ordinal);
        var rows = run.Candidates
            .Where(x => x.Forecast?.SuccessProbability is not null && trades.ContainsKey(x.EventId))
            .Select(x =>
            {
                var trade = trades[x.EventId];
                return new ProbabilityObservation(x.EventId, x.Forecast!.AsOf, trade.ExitAt!.Value,
                    x.Forecast.SuccessProbability!.Value,
                    string.Equals(trade.Status, "TARGET", StringComparison.Ordinal));
            }).ToArray();
        return ProbabilityCalibrationEvaluator.Evaluate(rows, asOf);
    }

    static ConditionalReturnModelSample[] BuildConditionalModelSamples(
        HistoricalStructureTradeReplay.ReplayRun run)
    {
        var trades = run.Trades.Values.SelectMany(x => x)
            .Where(x => x.Structure?.EntryEventId is not null && x.ExitAt is not null && x.Status != "OPEN" &&
                        x.PnlPercent is not null && double.IsFinite(x.PnlPercent.Value))
            .GroupBy(x => x.Structure!.EntryEventId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(t => t.EnteredAt).First(), StringComparer.Ordinal);
        return run.Candidates
            .Where(x => x.ForecastInput is not null && trades.ContainsKey(x.EventId))
            .Select(x =>
            {
                var trade = trades[x.EventId];
                return new ConditionalReturnModelSample(x.EventId, x.Symbol, x.ForecastInput!.AsOf,
                    trade.EnteredAt, trade.ExitAt!.Value, x.ForecastInput,
                    string.Equals(trade.Status, "TARGET", StringComparison.Ordinal), trade.PnlPercent!.Value);
            })
            .OrderBy(x => x.FeatureAt).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
    }

    static HistoricalReplaySymbolResult Result(string symbol, int signals, ImmutableArray<SimTrade> trades)
    {
        var closed = trades.Where(x => x.Status != "OPEN" && x.ExitAt.HasValue).ToArray();
        var exits = new HistoricalReplayExitCounts(closed.Count(x => x.Status == "STOP"),
            closed.Count(x => x.Status == "TARGET"), closed.Count(x => x.Status == "EOD"));
        var gross = closed.Sum(x => x.ExitPrice.HasValue ? Gross(x) : 0);
        var fees = closed.Sum(x => x.PnlPercent.HasValue && x.ExitPrice.HasValue
            ? Gross(x) - x.PnlPercent.Value
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
                var gross = x.ExitPrice.HasValue ? Math.Round(Gross(x), 6) : (double?)null;
                var fee = gross.HasValue && x.PnlPercent.HasValue ? Math.Round(gross.Value - x.PnlPercent.Value, 6) : 0;
                return System.Text.Json.JsonSerializer.Serialize(new HistoricalReplayTradeResult(
                    x, gross, fee, null, x.PnlPercent));
            });
        await File.WriteAllLinesAsync(Path.Combine(replayRoot, "trades.jsonl"), rows, ct);
    }

    static async Task WriteModelEvaluationAsync(string replayRoot, ConditionalReturnModelEvaluation evaluation,
        CancellationToken ct)
    {
        Directory.CreateDirectory(replayRoot);
        var path = Path.Combine(replayRoot, "conditional-model-evaluation.json");
        var temporary = path + ".tmp";
        var json = System.Text.Json.JsonSerializer.Serialize(evaluation,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });
        await File.WriteAllTextAsync(temporary, json, ct);
        File.Move(temporary, path, true);
    }

    static double Gross(SimTrade trade) => trade.Side == TradeSide.Long
        ? (trade.ExitPrice!.Value / trade.EntryPrice - 1) * 100
        : (1 - trade.ExitPrice!.Value / trade.EntryPrice) * 100;

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
