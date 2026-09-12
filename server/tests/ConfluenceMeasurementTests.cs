using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server.Application;
using Astra.Server.Application.Backtest;
using Astra.Server.Backtest;
using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Indicators;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Astra.Server.Tests;

public sealed class MeasurementStatisticsTests
{
    [Fact]
    public void WilsonIntervalMatchesThePublishedValueFor70Of100()
    {
        var interval = MeasurementStatistics.Wilson(70, 100);

        Assert.Equal(0.6042, interval.Low, 1e-4);
        Assert.Equal(0.7810, interval.High, 1e-4);
    }

    [Fact]
    public void WilsonIntervalMatchesThePublishedValueFor5Of10()
    {
        var interval = MeasurementStatistics.Wilson(5, 10);

        Assert.Equal(0.2366, interval.Low, 1e-4);
        Assert.Equal(0.7634, interval.High, 1e-4);
    }

    [Fact]
    public void WilsonIntervalIsTheWholeRangeWithoutTrials()
    {
        var interval = MeasurementStatistics.Wilson(0, 0);

        Assert.Equal(0, interval.Low);
        Assert.Equal(1, interval.High);
    }

    [Fact]
    public void BrierScoreIsTheMeanSquaredForecastError()
    {
        var observations = new[] { (0.8, true), (0.8, true), (0.8, false), (0.2, false) };

        Assert.Equal((0.04 + 0.04 + 0.64 + 0.04) / 4, MeasurementStatistics.Brier(observations), 10);
    }

    [Fact]
    public void ForecastProbabilityMapsScoreToZeroOneRange()
    {
        Assert.Equal(0.5, MeasurementStatistics.ForecastProbability(0));
        Assert.Equal(1, MeasurementStatistics.ForecastProbability(1));
        Assert.Equal(0, MeasurementStatistics.ForecastProbability(-1));
        Assert.Equal(0.75, MeasurementStatistics.ForecastProbability(0.5));
    }

    [Fact]
    public void BinomialTwoSidedPMatchesTheExactValueFor8Of10()
    {
        Assert.Equal(0.109375, MeasurementStatistics.BinomialTwoSidedP(8, 10), 9);
    }

    [Fact]
    public void BinomialTwoSidedPIsOneAtTheNullCenter()
    {
        Assert.Equal(1, MeasurementStatistics.BinomialTwoSidedP(5, 10), 9);
    }

    [Fact]
    public void BinomialTwoSidedPIsSymmetric()
    {
        Assert.Equal(MeasurementStatistics.BinomialTwoSidedP(70, 100),
            MeasurementStatistics.BinomialTwoSidedP(30, 100), 12);
    }

    [Fact]
    public void BenjaminiHochbergRejectsOnlyTheSignificantTechnique()
    {
        var pValues = new[] { 0.001, 0.5, 0.6, 0.7, 0.8, 0.9, 1, 1, 1, 1 };

        var rejected = MeasurementStatistics.BenjaminiHochberg(pValues, 0.05);

        Assert.True(rejected[0]);
        Assert.Equal(1, rejected.Count(x => x));
    }

    [Fact]
    public void BenjaminiHochbergAcceptsTheExactThresholdAndRefusesJustAboveIt()
    {
        var onBoundary = MeasurementStatistics.BenjaminiHochberg([0.005, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0.05);
        var aboveBoundary = MeasurementStatistics.BenjaminiHochberg([0.0051, 1, 1, 1, 1, 1, 1, 1, 1, 1], 0.05);

        Assert.True(onBoundary[0]);
        Assert.DoesNotContain(true, aboveBoundary);
    }

    [Fact]
    public void BenjaminiHochbergRejectsEveryPValueBelowTheLargestPassingRank()
    {
        var rejected = MeasurementStatistics.BenjaminiHochberg([0.009, 0.001, 1, 1, 1, 1, 1, 1, 1, 1], 0.05);

        Assert.True(rejected[0]);
        Assert.True(rejected[1]);
        Assert.Equal(2, rejected.Count(x => x));
    }
}

public sealed class ConfluenceMeasurementTests
{
    [Fact]
    public void SignalCountsOnlyWhenItIsNotWarmupAndPassesTheThreshold()
    {
        var policy = MeasurementPolicy.Default;

        Assert.True(ConfluenceMeasurement.IsSignal(TechniqueSignal.Create("MACD", 0.3, 1), policy));
        Assert.False(ConfluenceMeasurement.IsSignal(TechniqueSignal.Create("MACD", 0.29, 1), policy));
        Assert.True(ConfluenceMeasurement.IsSignal(TechniqueSignal.Create("MACD", -0.9, 0.5), policy));
        Assert.False(ConfluenceMeasurement.IsSignal(TechniqueSignal.WarmingUp("MACD"), policy));
        Assert.False(ConfluenceMeasurement.IsSignal(TechniqueSignal.Missing("OBI"), policy));
    }

    [Fact]
    public void OutcomeIsTheAtrNormalisedMoveNetOfRoundTripCost()
    {
        var bars = Flat(20, 100);
        bars = bars.SetItem(15, bars[15] with { Close = 102 });

        var outcome = ConfluenceMeasurement.Measure("MACD", "TSLA", bars, 10, 5, 0.5, 1,
            MeasurementPolicy.Default);

        Assert.NotNull(outcome);
        Assert.Equal(2, outcome.ReturnAtr, 4);
        Assert.Equal(2 - 100 * 0.0021, outcome.NetReturnAtr, 4);
        Assert.True(outcome.Hit);
    }

    [Fact]
    public void OutcomeIsAMissWhenTheMoveOpposesTheScore()
    {
        var bars = Flat(20, 100);
        bars = bars.SetItem(15, bars[15] with { Close = 102 });

        var outcome = ConfluenceMeasurement.Measure("MACD", "TSLA", bars, 10, 5, -0.5, 2,
            MeasurementPolicy.Default);

        Assert.NotNull(outcome);
        Assert.Equal(-1, outcome.ReturnAtr, 4);
        Assert.False(outcome.Hit);
    }

    [Fact]
    public void OutcomeIsSkippedWhenTheHorizonReachesPastTheStoredDay()
    {
        var bars = Flat(20, 100);

        Assert.Null(ConfluenceMeasurement.Measure("MACD", "TSLA", bars, 18, 5, 0.5, 1, MeasurementPolicy.Default));
        Assert.Null(ConfluenceMeasurement.Measure("MACD", "TSLA", bars, 10, 5, 0.5, 0, MeasurementPolicy.Default));
        Assert.Null(ConfluenceMeasurement.Measure("MACD", "TSLA", bars, 10, 5, 0, 1, MeasurementPolicy.Default));
    }

    [Fact]
    public void SummaryReportsHitRateWilsonAndBrierForASeventyPercentTechnique()
    {
        var outcomes = Outcomes(TechniqueNames.Macd, 100, 70, 0.6);

        var row = ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default)
            .Single(x => x.Technique == TechniqueNames.Macd);

        Assert.Equal(100, row.N);
        Assert.Equal(70, row.Hits);
        Assert.Equal(0.7, row.HitRate, 4);
        Assert.Equal(0.6042, row.Ci.Low, 1e-4);
        Assert.Equal(0.7810, row.Ci.High, 1e-4);
        Assert.Equal(0.22, row.Brier, 4);
        Assert.Equal("verified", row.StatusText);
        Assert.Equal(1.4, row.Weight, 4);
    }

    [Fact]
    public void SummaryLeavesACoinFlipTechniqueAtTheBaseline()
    {
        var outcomes = Outcomes(TechniqueNames.Rsi, 100, 50, 0.6);

        var row = ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default)
            .Single(x => x.Technique == TechniqueNames.Rsi);

        Assert.Equal(0.5, row.HitRate, 4);
        Assert.Equal("unverified", row.StatusText);
        Assert.Equal(1, row.Weight);
    }

    [Fact]
    public void SummaryPushesAThirtyPercentTechniqueBelowTheBaseline()
    {
        var outcomes = Outcomes(TechniqueNames.AdxDmi, 100, 30, 0.6);

        var row = ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default)
            .Single(x => x.Technique == TechniqueNames.AdxDmi);

        Assert.Equal(0.3, row.HitRate, 4);
        Assert.Equal("rejected", row.StatusText);
        Assert.Equal(0.6, row.Weight, 4);
    }

    [Fact]
    public void SummaryKeepsSmallSamplesUnverifiedAtWeightOne()
    {
        var outcomes = Outcomes(TechniqueNames.VwapDeviation, 49, 49, 0.6);

        var row = ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default)
            .Single(x => x.Technique == TechniqueNames.VwapDeviation);

        Assert.Equal(49, row.N);
        Assert.Equal("unverified", row.StatusText);
        Assert.Equal(1, row.Weight);
    }

    [Fact]
    public void SummaryAppliesTheCorrectionAcrossAllTenTechniquesAtOnce()
    {
        var outcomes = Outcomes(TechniqueNames.Macd, 100, 70, 0.6)
            .Concat(TechniqueNames.All.Where(x => x != TechniqueNames.Macd)
                .SelectMany(x => Outcomes(x, 100, 52, 0.6)))
            .ToArray();

        var rows = ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default);

        Assert.Equal(10, rows.Length);
        Assert.Equal(TechniqueNames.Macd, Assert.Single(rows, x => x.StatusText == "verified").Technique);
        Assert.Equal(9, rows.Count(x => x.StatusText == "unverified"));
        Assert.All(rows.Where(x => x.StatusText == "unverified"), row => Assert.Equal(1, row.Weight));
    }

    [Fact]
    public void SummaryIgnoresOutcomesFromOtherHorizons()
    {
        var outcomes = Outcomes(TechniqueNames.Macd, 100, 70, 0.6, horizon: 5);

        var row = ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default)
            .Single(x => x.Technique == TechniqueNames.Macd);

        Assert.Equal(0, row.N);
        Assert.Equal("unverified", row.StatusText);
        Assert.Equal(1, row.Weight);
    }

    [Fact]
    public void WeightMappingMovesAroundTheBaselineOfOne()
    {
        Assert.Equal(1.2, ConfluenceMeasurement.WeightOf(MeasurementStatus.Verified, 0.6), 4);
        Assert.Equal(1.5, ConfluenceMeasurement.WeightOf(MeasurementStatus.Verified, 0.75), 4);
        Assert.Equal(0.8, ConfluenceMeasurement.WeightOf(MeasurementStatus.Rejected, 0.4), 4);
        Assert.Equal(1, ConfluenceMeasurement.WeightOf(MeasurementStatus.Verified, 0.5), 4);
        Assert.Equal(1, ConfluenceMeasurement.WeightOf(MeasurementStatus.Unverified, 0.9), 4);
        Assert.Equal(1, ConfluenceMeasurement.WeightOf(MeasurementStatus.Unverified, 0.1), 4);
    }

    [Fact]
    public void WeightMappingClampsToZeroTwo()
    {
        Assert.Equal(2, ConfluenceMeasurement.WeightOf(MeasurementStatus.Verified, 1), 4);
        Assert.Equal(2, ConfluenceMeasurement.WeightOf(MeasurementStatus.Verified, 1.5), 4);
        Assert.Equal(0, ConfluenceMeasurement.WeightOf(MeasurementStatus.Rejected, 0), 4);
        Assert.Equal(0, ConfluenceMeasurement.WeightOf(MeasurementStatus.Rejected, -0.5), 4);
    }

    internal static ImmutableArray<IndicatorBar> Flat(int count, decimal close)
    {
        var start = new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
        return [.. Enumerable.Range(0, count).Select(i => new IndicatorBar(start.AddMinutes(i),
            start.AddMinutes(i + 1), close, close, close, close, 1000))];
    }

    static SignalOutcome[] Outcomes(string technique, int n, int hits, double score, int horizon = 10)
    {
        var start = new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
        return [.. Enumerable.Range(0, n).Select(i => new SignalOutcome(technique, "TSLA", start.AddMinutes(i),
            score, horizon, i < hits ? 1 : -1, i < hits ? 0.8 : -1.2, i < hits))];
    }
}

public sealed class ConfluenceReplayTests
{
    [Fact]
    public void ReplayWindowNeverContainsABarAfterTheCurrentIndex()
    {
        var bars = Rising(40);

        foreach (var window in SequentialBarReplay.Windows("TSLA", bars[0].Start, bars))
        {
            Assert.Equal(window.Index + 1, window.Completed.Length);
            Assert.Equal(bars[window.Index], window.Current);
            Assert.Equal(bars[window.Index].Close, window.Completed.Max(x => x.Close));
            Assert.All(window.Completed, bar => Assert.True(bar.End <= window.Current.End));
        }
    }

    [Fact]
    public void ReplayWindowEqualsTheTruncatedSeries()
    {
        var bars = Rising(30);

        var windows = SequentialBarReplay.Windows("TSLA", bars[0].Start, bars).ToArray();

        Assert.Equal(30, windows.Length);
        for (var i = 0; i < windows.Length; i++)
            Assert.Equal(bars.Take(i + 1).ToArray(), windows[i].Completed.ToArray());
    }

    [Fact]
    public void ReplayCutsTheBenchmarkAtTheCurrentBarEnd()
    {
        var bars = Rising(10);
        var benchmark = Rising(10);

        foreach (var window in SequentialBarReplay.Windows("TSLA", bars[0].Start, bars, benchmark))
            Assert.All(window.Benchmark, bar => Assert.True(bar.End <= window.Current.End));
    }

    [Fact]
    public void ReplayScoresDoNotChangeWhenFutureBarsAreExtreme()
    {
        var calm = Rising(40);
        var shocked = calm.SetItem(39, calm[39] with { High = 9000, Close = 9000 });

        var fromCalm = SequentialBarReplay.Windows("TSLA", calm[0].Start, calm).ElementAt(30);
        var fromShocked = SequentialBarReplay.Windows("TSLA", shocked[0].Start, shocked).ElementAt(30);

        Assert.Equal(fromCalm.Completed.ToArray(), fromShocked.Completed.ToArray());
    }

    [Fact]
    public void ParseReadsTheStoredBarSchemaAndSkipsBrokenLines()
    {
        var bars = ConfluenceReplay.Parse([Line("2026-09-08T13:31:00Z", 2), "{", "", Line("2026-09-08T13:30:00Z", 1)]);

        Assert.Equal(2, bars.Length);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero), bars[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 13, 31, 0, TimeSpan.Zero), bars[0].End);
        Assert.Equal(101m, bars[0].Close);
    }

    [Fact]
    public async Task ReplayUsesOnlyTheDaysInsideTheMeasurementWindow()
    {
        var store = new MemoryBars();
        store.Seed("2026-09-07", "TSLA", 60);
        store.Seed("2026-09-08", "TSLA", 60);
        store.Seed("2026-09-09", "TSLA", 60);

        var report = await new ConfluenceReplay(store).RunAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
            10, "QQQ", CancellationToken.None);

        Assert.Equal(1, report.Days);
        Assert.Equal(60, report.Bars);
        Assert.Equal(1, report.Symbols);
    }

    [Fact]
    public async Task ReplayLeavesTechniquesWithoutStoredInputsEmpty()
    {
        var store = new MemoryBars();
        store.Seed("2026-09-08", "TSLA", 90);

        var report = await new ConfluenceReplay(store).RunAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
            10, "QQQ", CancellationToken.None);

        Assert.Equal(10, report.Techniques.Length);
        Assert.Equal(0, report.Techniques.Single(x => x.Technique == TechniqueNames.OrderBookImbalance).N);
        Assert.Equal(0, report.Techniques.Single(x => x.Technique == TechniqueNames.RelativeStrength).N);
        Assert.True(report.Signals > 0);
        Assert.All(report.Techniques, row => Assert.Equal(10, row.HorizonBars));
    }

    [Fact]
    public async Task ReplaySkipsTheBenchmarkSymbolAsAMeasuredSymbol()
    {
        var store = new MemoryBars();
        store.Seed("2026-09-08", "TSLA", 60);
        store.Seed("2026-09-08", "QQQ", 60);

        var report = await new ConfluenceReplay(store).RunAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
            10, "QQQ", CancellationToken.None);

        Assert.Equal(1, report.Symbols);
        Assert.Equal(60, report.Bars);
    }

    [Fact]
    public async Task ReplayReportsEveryHorizonOfTheMeasurementPolicy()
    {
        var store = new MemoryBars();
        store.Seed("2026-09-08", "TSLA", 90);

        var report = await new ConfluenceReplay(store).RunAsync(new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 8),
            20, "QQQ", CancellationToken.None);

        Assert.Equal([5, 10, 20], report.ByHorizon.Keys.OrderBy(x => x).ToArray());
        Assert.Equal(20, report.HorizonBars);
        Assert.All(report.Techniques, row => Assert.Equal(20, row.HorizonBars));
    }

    internal static ImmutableArray<IndicatorBar> Rising(int count)
    {
        var start = new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero);
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            decimal open = 100 + i, close = 100 + i + 0.5m;
            return new IndicatorBar(start.AddMinutes(i), start.AddMinutes(i + 1), open, close + 0.2m, open - 0.2m,
                close, 1000 + i * 10);
        })];
    }

    static string Line(string timestamp, int step) => JsonSerializer.Serialize(new
    {
        t = DateTimeOffset.Parse(timestamp).UtcDateTime,
        o = 100.0 + step,
        h = 100.5 + step,
        l = 99.5 + step,
        c = 100.0 + step,
        v = 1000.0
    });

    internal sealed class MemoryBars : IBarStore
    {
        readonly Dictionary<(string Day, string Symbol), List<string>> _files = new();

        public void Seed(string day, string symbol, int count)
        {
            var start = DateTimeOffset.Parse($"{day}T13:30:00Z");
            var lines = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var wave = Math.Sin(i / 6.0) * 1.5;
                double open = 100 + wave, close = 100 + Math.Sin((i + 1) / 6.0) * 1.5;
                lines.Add(JsonSerializer.Serialize(new
                {
                    t = start.AddMinutes(i).UtcDateTime,
                    o = open,
                    h = Math.Max(open, close) + 0.3,
                    l = Math.Min(open, close) - 0.3,
                    c = close,
                    v = 1000.0 + i * 25
                }));
            }
            _files[(day, symbol)] = lines;
        }

        public Task<string?> LastLineAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(_files.TryGetValue((day, symbol), out var lines) && lines.Count > 0 ? lines[^1] : null);

        public Task AppendAsync(string day, string symbol, string line, CancellationToken ct)
        {
            if (!_files.TryGetValue((day, symbol), out var lines)) _files[(day, symbol)] = lines = [];
            lines.Add(line);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListDaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Select(x => x.Day).Distinct().ToArray());

        public Task<IReadOnlyList<string>> ListSymbolsAsync(string day, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.Keys.Where(x => x.Day == day).Select(x => x.Symbol)
                .ToArray());

        public Task<int> CountLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult(_files.TryGetValue((day, symbol), out var lines) ? lines.Count : 0);

        public Task<IReadOnlyList<string>> ReadLinesAsync(string day, string symbol, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_files.TryGetValue((day, symbol), out var lines)
                ? lines.ToArray()
                : []);

        public Task DeleteDayAsync(string day, CancellationToken ct)
        {
            foreach (var key in _files.Keys.Where(x => x.Day == day).ToArray()) _files.Remove(key);
            return Task.CompletedTask;
        }
    }
}

public sealed class ConfluenceWalkForwardTests
{
    [Fact]
    public void FourWeekMeasurementWindowEndsTheDayBeforeTheApplyWeek()
    {
        var window = ConfluenceWalkForward.ForApplyWeek(new DateOnly(2026, 9, 14));

        Assert.Equal(new DateOnly(2026, 8, 17), window.From);
        Assert.Equal(new DateOnly(2026, 9, 13), window.To);
        Assert.Equal(new DateOnly(2026, 9, 14), window.ApplyFrom);
        Assert.Equal(new DateOnly(2026, 9, 20), window.ApplyTo);
    }

    [Fact]
    public void MeasurementWindowExcludesTheApplyWeekAndAnythingOlderThanFourWeeks()
    {
        var window = ConfluenceWalkForward.ForApplyWeek(new DateOnly(2026, 9, 14));

        Assert.False(window.Contains(new DateOnly(2026, 8, 16)));
        Assert.True(window.Contains(new DateOnly(2026, 8, 17)));
        Assert.True(window.Contains(new DateOnly(2026, 9, 13)));
        Assert.False(window.Contains(new DateOnly(2026, 9, 14)));
        Assert.True(window.Applies(new DateOnly(2026, 9, 14)));
        Assert.False(window.Applies(new DateOnly(2026, 9, 21)));
    }

    [Fact]
    public void DaysInWindowKeepsOnlyParsableDatesInsideTheBoundary()
    {
        var days = ConfluenceWalkForward.DaysInWindow(
            ["2026-09-13", "not-a-day", "2026-08-16", "2026-08-17", "2026-09-14"],
            new DateOnly(2026, 8, 17), new DateOnly(2026, 9, 13));

        Assert.Equal([new DateOnly(2026, 8, 17), new DateOnly(2026, 9, 13)], days.ToArray());
    }
}

public sealed class ConfluenceWeightsFileTests
{
    [Fact]
    public void DocumentRoundTripsThroughTheStoredSchema()
    {
        var document = Sample();

        Assert.True(ConfluenceWeightsDocument.TryRead(document.ToJson(), out var parsed));
        Assert.Equal(document.WeightsVersion, parsed.WeightsVersion);
        Assert.Equal(document.HorizonBars, parsed.HorizonBars);
        Assert.Equal(document.WindowFrom, parsed.WindowFrom);
        Assert.Equal(document.WindowTo, parsed.WindowTo);
        Assert.Equal(document.Weights[TechniqueNames.Macd].W, parsed.Weights[TechniqueNames.Macd].W);
        Assert.Equal("verified", parsed.Weights[TechniqueNames.Macd].Status);
    }

    [Fact]
    public void VersionCarriesTheMeasurementDateAndAContentHash()
    {
        var document = Sample();

        Assert.Matches("^w-20260911-[0-9a-f]{8}$", document.WeightsVersion);
    }

    [Fact]
    public void SameMeasurementProducesTheSameVersion()
    {
        Assert.Equal(Sample().WeightsVersion, Sample().WeightsVersion);
    }

    [Fact]
    public void WeightsFeedTheAggregatorByTechniqueName()
    {
        var weights = Sample().ToWeights();

        Assert.Equal(1.4, weights.Values[TechniqueNames.Macd]);
        Assert.Equal(1, weights.Values[TechniqueNames.Rsi]);
    }

    [Fact]
    public void CorruptOrIncompleteDocumentsAreRefused()
    {
        Assert.False(ConfluenceWeightsDocument.TryRead("{ broken", out _));
        Assert.False(ConfluenceWeightsDocument.TryRead("[]", out _));
        Assert.False(ConfluenceWeightsDocument.TryRead("""{"weights":{}}""", out _));
        Assert.False(ConfluenceWeightsDocument.TryRead("""{"weightsVersion":"w-1"}""", out _));
        Assert.False(ConfluenceWeightsDocument.TryRead("""{"weightsVersion":"w-1","weights":{"MACD":{"w":-1}}}""",
            out _));
        Assert.False(ConfluenceWeightsDocument.TryRead("""{"weightsVersion":"w-1","weights":{"MACD":{}}}""", out _));
    }

    [Fact]
    public void MissingFileLoadsTheDefaultWeightsWithoutAnError()
    {
        var root = Directory.CreateTempSubdirectory("astra-weights-").FullName;
        try
        {
            var document = ConfluenceWeightsStore.Load(root, out var error);

            Assert.Null(error);
            Assert.True(document.IsDefault);
            Assert.Empty(document.Weights);
            Assert.Equal(ConfluenceAggregator.DefaultWeightsVersion, document.WeightsVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CorruptFileLogsAndFallsBackToUniformWeights()
    {
        var root = Directory.CreateTempSubdirectory("astra-weights-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "App_Data"));
            File.WriteAllText(ConfluenceWeightsDocument.PathFor(root), "{ not json");

            var document = ConfluenceWeightsStore.Load(root, out var error);

            Assert.NotNull(error);
            Assert.True(document.IsDefault);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SavedFileIsReadBackWithTheSameWeights()
    {
        var root = Directory.CreateTempSubdirectory("astra-weights-").FullName;
        try
        {
            ConfluenceWeightsStore.Save(root, Sample());

            var document = ConfluenceWeightsStore.Load(root, out var error);

            Assert.Null(error);
            Assert.False(document.IsDefault);
            Assert.Equal(1.4, document.Weights[TechniqueNames.Macd].W);
            Assert.Equal(10, document.HorizonBars);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static ConfluenceWeightsDocument Sample()
    {
        var outcomes = Enumerable.Range(0, 100)
            .Select(i => new SignalOutcome(TechniqueNames.Macd, "TSLA",
                new DateTimeOffset(2026, 9, 8, 13, 30, 0, TimeSpan.Zero).AddMinutes(i), 0.6, 10,
                i < 70 ? 1 : -1, i < 70 ? 0.8 : -1.2, i < 70))
            .ToArray();
        var report = new ConfluenceMeasurementReport(new DateOnly(2026, 8, 14), new DateOnly(2026, 9, 10), 20, 14,
            5000, 900, 10, ConfluenceMeasurement.Summarize(outcomes, 10, MeasurementPolicy.Default),
            ImmutableDictionary<int, ImmutableArray<TechniqueMeasurement>>.Empty);
        return ConfluenceWeightsDocument.FromMeasurement(report,
            new DateTimeOffset(2026, 9, 11, 6, 0, 0, TimeSpan.Zero));
    }
}

public sealed class ConfluenceWeightsApiTests
{
    [Fact]
    public async Task WeightsEndpointReportsUniformWeightsWhenNoFileIsStored()
    {
        using var host = new TemporaryHost();

        var payload = await host.WeightsAsync();

        Assert.Equal("default", payload.GetProperty("source").GetString());
        Assert.Equal(ConfluenceAggregator.DefaultWeightsVersion, payload.GetProperty("weightsVersion").GetString());
        Assert.Empty(payload.GetProperty("weights").EnumerateObject());
    }

    [Fact]
    public async Task WeightsEndpointReportsTheStoredMeasurement()
    {
        using var host = new TemporaryHost();
        var document = ConfluenceWeightsFileTests.Sample();
        ConfluenceWeightsStore.Save(host.ContentRoot, document);

        var payload = await host.WeightsAsync();

        Assert.Equal("file", payload.GetProperty("source").GetString());
        Assert.Equal(document.WeightsVersion, payload.GetProperty("weightsVersion").GetString());
        Assert.Equal(10, payload.GetProperty("horizonBars").GetInt32());
        Assert.Equal("2026-08-14", payload.GetProperty("window").GetProperty("from").GetString());
        var macd = payload.GetProperty("weights").GetProperty(TechniqueNames.Macd);
        Assert.Equal(1.4, macd.GetProperty("w").GetDouble(), 4);
        Assert.Equal(100, macd.GetProperty("n").GetInt32());
        Assert.Equal("verified", macd.GetProperty("status").GetString());
        Assert.Equal(2, macd.GetProperty("ci").GetArrayLength());
    }

    [Fact]
    public void StoredWeightsReachTheConfluenceService()
    {
        using var host = new TemporaryHost();
        ConfluenceWeightsStore.Save(host.ContentRoot, ConfluenceWeightsFileTests.Sample());

        var service = host.Factory.Services.GetRequiredService<ConfluenceService>();

        Assert.Equal(ConfluenceWeightsFileTests.Sample().WeightsVersion, service.WeightsVersion);
    }

    sealed class TemporaryHost : IDisposable
    {
        public string ContentRoot { get; } = Directory.CreateTempSubdirectory("astra-weights-host-").FullName;
        public WebApplicationFactory<Program> Factory { get; }

        public TemporaryHost() =>
            Factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseContentRoot(ContentRoot));

        public async Task<JsonElement> WeightsAsync()
        {
            var response = await Factory.CreateClient().GetAsync("/api/confluence/weights");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        public void Dispose()
        {
            Factory.Dispose();
            try { Directory.Delete(ContentRoot, true); } catch { }
        }
    }
}

public sealed class ConfluenceMeasureCommandTests
{
    [Fact]
    public async Task CommandWritesTheWeightsFileFromStoredBars()
    {
        var root = Directory.CreateTempSubdirectory("astra-measure-").FullName;
        try
        {
            SeedDay(root, "2026-09-08", "TSLA", 90);
            var output = new StringWriter();

            var exit = await ConfluenceMeasureCommand.RunAsync(
                ["confluence-measure", "--root", root, "--from", "2026-09-08", "--to", "2026-09-08", "--horizon", "10"],
                output, TimeProvider.System, CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(ConfluenceWeightsDocument.PathFor(root)));
            Assert.Contains("| 기법 | 지평 |", output.ToString());
            var document = ConfluenceWeightsStore.Load(root, out var error);
            Assert.Null(error);
            Assert.Equal(10, document.Weights.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CommandLeavesNoFileWhenNoBarsAreStored()
    {
        var root = Directory.CreateTempSubdirectory("astra-measure-").FullName;
        try
        {
            var output = new StringWriter();

            var exit = await ConfluenceMeasureCommand.RunAsync(["confluence-measure", "--root", root], output,
                TimeProvider.System, CancellationToken.None);

            Assert.Equal(0, exit);
            Assert.False(File.Exists(ConfluenceWeightsDocument.PathFor(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CommandRefusesAnInvertedWindow()
    {
        var root = Directory.CreateTempSubdirectory("astra-measure-").FullName;
        try
        {
            var exit = await ConfluenceMeasureCommand.RunAsync(
                ["confluence-measure", "--root", root, "--from", "2026-09-08", "--to", "2026-09-01"],
                new StringWriter(), TimeProvider.System, CancellationToken.None);

            Assert.Equal(2, exit);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OptionReaderTakesTheValueAfterTheFlag()
    {
        Assert.Equal("10", ConfluenceMeasureCommand.Option(["a", "--horizon", "10"], "--horizon"));
        Assert.Null(ConfluenceMeasureCommand.Option(["a", "--horizon"], "--horizon"));
        Assert.Null(ConfluenceMeasureCommand.Option([], "--horizon"));
    }

    static void SeedDay(string root, string day, string symbol, int count)
    {
        var directory = Path.Combine(root, "App_Data", "bars", day);
        Directory.CreateDirectory(directory);
        var start = DateTimeOffset.Parse($"{day}T13:30:00Z");
        var lines = Enumerable.Range(0, count).Select(i =>
        {
            double open = 100 + Math.Sin(i / 6.0) * 1.5, close = 100 + Math.Sin((i + 1) / 6.0) * 1.5;
            return JsonSerializer.Serialize(new
            {
                t = start.AddMinutes(i).UtcDateTime,
                o = open,
                h = Math.Max(open, close) + 0.3,
                l = Math.Min(open, close) - 0.3,
                c = close,
                v = 1000.0 + i * 25
            });
        });
        File.WriteAllLines(Path.Combine(directory, $"{symbol}.jsonl"), lines);
    }
}
