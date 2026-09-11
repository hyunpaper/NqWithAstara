using Astra.Server.Domain;
using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>
/// 이슈 #28 — 사전 정의 평가 체계의 수치 검산과 "보류 / 미수집 / 0"의 구분.
/// 작은 결정적 fixture로 평균·중앙값·표준편차·95% 구간·승률을 손으로 계산한 값과 맞춘다.
/// </summary>
public sealed class ValidationEvaluationTests
{
    static (ObservationRow[] Rows, SimTrade[] Trades) Sample(params (string Id, double Pnl, int Day)[] entries)
    {
        var rows = new List<ObservationRow>();
        var trades = new List<SimTrade>();
        foreach (var (id, pnl, day) in entries)
        {
            var (row, trade) = Vx.Entered(id, pnl, day);
            rows.Add(row);
            trades.Add(trade);
        }
        return (rows.ToArray(), trades.ToArray());
    }

    static ValidationEvaluation Evaluate(ObservationRow[] rows, SimTrade[] trades,
        EvaluationThresholds? thresholds = null, VirtualPathSample[]? virtualPaths = null) =>
        ValidationEvaluator.Evaluate(Vx.Link(rows, trades).Candidates, Vx.AsOf, thresholds ?? Vx.Small,
            virtualPaths);

    static CohortEvaluation Cohort(ValidationEvaluation evaluation, string dimension, string key) =>
        Assert.Single(Assert.Single(evaluation.Groups, g => g.Dimension == dimension).Cohorts, c => c.Key == key);

    /// <summary>빈 입력: 0%·0.0이 아니라 null(미표시) + 검증 불가여야 한다.</summary>
    [Fact]
    public void EmptyInputIsReportedAsUnverifiedWithoutFakeZeros()
    {
        var evaluation = ValidationEvaluator.Evaluate([], Vx.AsOf);

        Assert.Equal(EvaluationVerdict.InsufficientSample, evaluation.Overall.Verdict);
        Assert.Null(evaluation.Overall.Pnl.MeanPercent);
        Assert.Null(evaluation.Overall.Pnl.MedianPercent);
        Assert.Null(evaluation.Overall.Pnl.StdDevPercent);
        Assert.Null(evaluation.Overall.Pnl.Ci95Lower);
        Assert.Null(evaluation.Overall.ObservedWinRatePercent);
        Assert.Null(evaluation.Overall.AvgPlannedNetR);
        Assert.Equal(0, evaluation.Overall.Scope.Candidates);
        Assert.All(evaluation.Groups, g => Assert.Empty(g.Cohorts));
    }

    /// <summary>손으로 계산한 값과 맞춘다: [1,2,3] → 평균 2, 중앙값 2, 표준편차 1, SE 0.5774, 95% [0.8684, 3.1316].</summary>
    [Fact]
    public void RealizedPnlStatisticsMatchHandComputedValues()
    {
        var (rows, trades) = Sample(("E1", 1.0, 0), ("E2", 2.0, 1), ("E3", 3.0, 2));

        var overall = Evaluate(rows, trades).Overall;

        Assert.Equal(3, overall.Scope.Candidates);
        Assert.Equal(3, overall.Scope.Entered);
        Assert.Equal(3, overall.Scope.Closed);
        Assert.Equal(3, overall.Scope.RealizedPnl);
        Assert.Equal(0, overall.Scope.MissingPnl);
        Assert.Equal(3, overall.Scope.Sessions);
        Assert.Equal(2.0, overall.Pnl.MeanPercent);
        Assert.Equal(2.0, overall.Pnl.MedianPercent);
        Assert.Equal(1.0, overall.Pnl.StdDevPercent);
        Assert.Equal(0.5774, overall.Pnl.StandardError);
        Assert.Equal(0.8684, overall.Pnl.Ci95Lower);
        Assert.Equal(3.1316, overall.Pnl.Ci95Upper);
        Assert.Equal(1.0, overall.Pnl.MinPercent);
        Assert.Equal(3.0, overall.Pnl.MaxPercent);
        Assert.Equal(6.0, overall.Pnl.SumPercent);
        Assert.Equal(3, overall.Pnl.Wins);
        Assert.Equal(0, overall.Pnl.Losses);
        Assert.Equal(100.0, overall.ObservedWinRatePercent);
        Assert.Equal(1.6, overall.AvgPlannedNetR);
        Assert.Equal(EvaluationVerdict.Observed, overall.Verdict);
    }

    /// <summary>짝수 표본의 중앙값과 손실 분류(0%는 승리가 아니다) 검산: [-1,0,1,2] → 평균 0.5, 중앙값 0.5, 승률 50%.</summary>
    [Fact]
    public void MedianAndLossClassificationAreVerifiable()
    {
        var (rows, trades) = Sample(("E1", -1.0, 0), ("E2", 0.0, 1), ("E3", 1.0, 2), ("E4", 2.0, 3));

        var overall = Evaluate(rows, trades).Overall;

        Assert.Equal(0.5, overall.Pnl.MeanPercent);
        Assert.Equal(0.5, overall.Pnl.MedianPercent);
        Assert.Equal(2, overall.Pnl.Wins);
        Assert.Equal(2, overall.Pnl.Losses);
        Assert.Equal(50.0, overall.ObservedWinRatePercent);
    }

    /// <summary>표본이 기준에 못 미치면 검증 불가로 보고한다 — 0이나 통과로 위장하지 않는다.</summary>
    [Fact]
    public void SampleBelowThresholdIsInsufficientNotObserved()
    {
        var (rows, trades) = Sample(("E1", 1.0, 0), ("E2", 2.0, 1), ("E3", 3.0, 2));

        var overall = Evaluate(rows, trades, EvaluationThresholds.Default).Overall;

        Assert.Equal(EvaluationVerdict.InsufficientSample, overall.Verdict);
        Assert.Contains(overall.VerdictReasons, x => x.StartsWith("REALIZED_TRADES_BELOW_MIN", StringComparison.Ordinal));
        Assert.Contains(overall.VerdictReasons, x => x.StartsWith("SYMBOLS_BELOW_MIN", StringComparison.Ordinal));
        // 검증 불가여도 관측 자체는 그대로 보고한다(수치를 숨기지 않는다).
        Assert.Equal(2.0, overall.Pnl.MeanPercent);
    }

    /// <summary>한 종목에 몰린 표본은 거래 수가 충분해도 결론을 낼 수 없다.</summary>
    [Fact]
    public void SymbolConcentrationBlocksTheObservedVerdict()
    {
        var (rows, trades) = Sample(("E1", 1.0, 0), ("E2", 2.0, 1), ("E3", 3.0, 2));

        var overall = Evaluate(rows, trades, new EvaluationThresholds(3, 1, 1, 60d)).Overall;

        Assert.Equal(EvaluationVerdict.InsufficientSample, overall.Verdict);
        Assert.Contains(overall.VerdictReasons,
            x => x.StartsWith("SYMBOL_CONCENTRATION_ABOVE_MAX", StringComparison.Ordinal));
        Assert.Equal(100.0, overall.Scope.TopSymbolSharePercent);
        Assert.Equal(Vx.Symbol, overall.Scope.TopSymbol);
    }

    /// <summary>미수집(NotCollected)은 0건과도, 검증 불가와도 다른 결론이다.</summary>
    [Fact]
    public void UncollectedQualityIsItsOwnVerdictNotZero()
    {
        var rows = new[] { Vx.Row("obs-1", [Vx.Candidate("E1", "REJECTED", quality: null, planned: false)]) };

        var evaluation = Evaluate(rows, []);

        var cohort = Cohort(evaluation, "entryQuality", SimulationCohorts.QualityUncollectedKey);
        Assert.False(cohort.Collected);
        Assert.Equal(EvaluationVerdict.NotCollected, cohort.Verdict);
        Assert.Equal(["VALUE_NOT_COLLECTED"], cohort.VerdictReasons);
        Assert.Null(cohort.Pnl.MeanPercent);
        Assert.Equal(1, cohort.Scope.Candidates);
        Assert.Equal(0, cohort.Scope.Entered);

        var liquidity = Cohort(evaluation, "liquidity", LiquidityClass.Uncollected);
        Assert.Equal(EvaluationVerdict.NotCollected, liquidity.Verdict);
    }

    /// <summary>진입 품질 구간 경계는 이슈 #27 코호트와 같아야 두 보고서를 비교할 수 있다.</summary>
    [Fact]
    public void QualityBandBoundariesMatchTheSimulationCohortContract()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "READY", 24.9)], minute: 41),
            Vx.Row("obs-2", [Vx.Candidate("E2", "READY", 25)], minute: 42),
            Vx.Row("obs-3", [Vx.Candidate("E3", "READY", 74.9)], minute: 43),
            Vx.Row("obs-4", [Vx.Candidate("E4", "READY", 75)], minute: 44)
        };

        var evaluation = Evaluate(rows, []);

        Assert.Equal(1, Cohort(evaluation, "entryQuality", ValidationEvaluator.QualityBand0).Scope.Candidates);
        Assert.Equal(1, Cohort(evaluation, "entryQuality", ValidationEvaluator.QualityBand25).Scope.Candidates);
        Assert.Equal(1, Cohort(evaluation, "entryQuality", ValidationEvaluator.QualityBand50).Scope.Candidates);
        Assert.Equal(1, Cohort(evaluation, "entryQuality", ValidationEvaluator.QualityBand75).Scope.Candidates);
    }

    /// <summary>호가 결측 집단과 정상 집단은 분리되어야 비용 가정의 효과를 볼 수 있다.</summary>
    [Fact]
    public void LiquidityCohortsSeparateMissingFromObserved()
    {
        var (missingRow, missingTrade) = Vx.Entered("E1", -0.5, 0, missingLiquidity: true);
        var (okRow, okTrade) = Vx.Entered("E2", 1.5, 1);

        var evaluation = Evaluate([missingRow, okRow], [missingTrade, okTrade]);

        Assert.Equal(-0.5, Cohort(evaluation, "liquidity", LiquidityClass.Missing).Pnl.MeanPercent);
        Assert.Equal(1.5, Cohort(evaluation, "liquidity", LiquidityClass.Observed).Pnl.MeanPercent);
    }

    [Fact]
    public void EstimatedAndObservedExitsAreSeparateCohorts()
    {
        var (estimatedRow, estimatedTrade) = Vx.Entered("E1", 1.0, 0, exitEstimated: true);
        var (observedRow, observedTrade) = Vx.Entered("E2", 2.0, 1);

        var evaluation = Evaluate([estimatedRow, observedRow], [estimatedTrade, observedTrade]);

        Assert.Equal(1.0, Cohort(evaluation, "exitEstimation", ValidationEvaluator.ExitEstimatedKey).Pnl.MeanPercent);
        Assert.Equal(2.0, Cohort(evaluation, "exitEstimation", ValidationEvaluator.ExitObservedKey).Pnl.MeanPercent);
        Assert.Equal(1, evaluation.Overall.Scope.EstimatedExits);
    }

    // ── 거절 후보와 가상 평가 ────────────────────────────────────────────────────────────────

    [Fact]
    public void RejectedCandidatesHaveNoCollectedHypotheticalPathByDefault()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "REJECTED", planned: false, rejections: ["NET_R_BELOW_MIN"])], minute: 41),
            Vx.Row("obs-2", [Vx.Candidate("E2", "REJECTED", planned: false, rejections: ["NET_R_BELOW_MIN"])], minute: 42),
            Vx.Row("obs-3", [Vx.Candidate("E3", "EXPIRED", planned: false)], minute: 43)
        };

        var rejected = Evaluate(rows, []).Rejected;

        Assert.Equal(2, rejected.Rejected);
        Assert.Equal(1, rejected.Expired);
        Assert.Equal(new RejectReasonCount("NET_R_BELOW_MIN", 2), rejected.TopReasons[0]);
        Assert.False(rejected.Hypothetical.Collected);
        Assert.Equal(VirtualRejectedEvaluation.BasisNotRetained, rejected.Hypothetical.Basis);
        Assert.Null(rejected.Hypothetical.HypotheticalMeanPercent);
        Assert.Contains(LinkCodes.RejectedPathNotRetained, rejected.Hypothetical.Limitations);
    }

    /// <summary>가상 경로를 넣어도 실제 체결 성과와 섞이지 않는다 — 필드가 분리되어 있어야 한다.</summary>
    [Fact]
    public void SuppliedVirtualPathsStayInASeparateFieldFromRealizedPnl()
    {
        var (row, trade) = Vx.Entered("E1", 2.0);
        var rejected = Vx.Row("obs-2", [Vx.Candidate("E9", "REJECTED", planned: false)], minute: 45);

        var evaluation = Evaluate([row, rejected], [trade], Vx.Small,
            [new VirtualPathSample("E9", 8.0), new VirtualPathSample("UNKNOWN", 99.0)]);

        // 실현 손익 평균은 진입 건만으로 계산된다(가상 8.0이 섞이지 않는다).
        Assert.Equal(2.0, evaluation.Overall.Pnl.MeanPercent);
        Assert.Equal(1, evaluation.Overall.Scope.RealizedPnl);
        Assert.True(evaluation.Rejected.Hypothetical.Collected);
        Assert.Equal(VirtualRejectedEvaluation.BasisCallerSupplied, evaluation.Rejected.Hypothetical.Basis);
        Assert.Equal(1, evaluation.Rejected.Hypothetical.Samples);
        Assert.Equal(8.0, evaluation.Rejected.Hypothetical.HypotheticalMeanPercent);
    }

    // ── 진입 품질 구간의 구분력 ──────────────────────────────────────────────────────────────

    [Fact]
    public void QualityDiscriminationIsInconclusiveWithoutTwoComparableBands()
    {
        var (rows, trades) = Sample(("E1", 1.0, 0), ("E2", 2.0, 1), ("E3", 3.0, 2));

        var bands = Evaluate(rows, trades).QualityBands;

        Assert.Equal(EvaluationVerdict.InsufficientSample, bands.Verdict);
        Assert.Equal(1, bands.ComparableBands);
        Assert.Contains(bands.Reasons, x => x.StartsWith("COMPARABLE_BANDS_BELOW_MIN", StringComparison.Ordinal));
        Assert.Null(bands.MeanSpreadPercent);
        Assert.Null(bands.Monotonic);
    }

    /// <summary>두 구간을 비교할 수 있을 때도 "구분됐다"는 95% 구간이 겹치지 않을 때만 말할 수 있다.</summary>
    [Fact]
    public void QualityDiscriminationReportsSpreadAndIntervalSeparation()
    {
        var rows = new List<ObservationRow>();
        var trades = new List<SimTrade>();
        var low = new[] { -1.0, 0.0, 1.0 };
        var high = new[] { 2.0, 3.0, 4.0 };
        for (var i = 0; i < 3; i++)
        {
            var (lowRow, lowTrade) = Vx.Entered($"L{i}", low[i], i, quality: 10);
            var (highRow, highTrade) = Vx.Entered($"H{i}", high[i], i + 3, quality: 80);
            rows.Add(lowRow); rows.Add(highRow);
            trades.Add(lowTrade); trades.Add(highTrade);
        }

        var bands = Evaluate(rows.ToArray(), trades.ToArray()).QualityBands;

        Assert.Equal(EvaluationVerdict.Observed, bands.Verdict);
        Assert.Equal(2, bands.ComparableBands);
        Assert.Equal(3.0, bands.MeanSpreadPercent);
        Assert.True(bands.Monotonic);
        Assert.True(bands.IntervalsSeparated);
        Assert.Equal(0.0, Assert.Single(bands.Bands, x => x.Key == ValidationEvaluator.QualityBand0).MeanPercent);
        Assert.Equal(3.0, Assert.Single(bands.Bands, x => x.Key == ValidationEvaluator.QualityBand75).MeanPercent);
    }

    // ── walk-forward ───────────────────────────────────────────────────────────────────────

    /// <summary>검증 구간의 모든 거래일은 학습 구간보다 뒤이고, 같은 거래일이 두 구간에 들어가지 않는다.</summary>
    [Fact]
    public void WalkForwardFoldsAreChronologicalAndDisjoint()
    {
        var entries = Enumerable.Range(0, 8).Select(d => ($"E{d}", 1.0 + d, d)).ToArray();
        var (rows, trades) = Sample(entries);
        var candidates = Vx.Link(rows, trades).Candidates;

        var report = WalkForwardEvaluator.Evaluate(candidates, 3, Vx.Small);

        Assert.Equal(8, report.Sessions);
        Assert.Equal(3, report.Folds);
        Assert.Equal(WalkForwardEvaluator.Method, report.Method);
        foreach (var fold in report.Result)
        {
            Assert.True(fold.InSampleTo < fold.OutOfSampleFrom, "학습 구간이 검증 구간보다 뒤에 있으면 안 된다.");
            Assert.True(fold.InSampleSessions > 0 && fold.OutOfSampleSessions > 0);
        }
        // fold가 진행될수록 학습 구간은 커지고 검증 구간은 앞으로 겹치지 않게 이동한다.
        Assert.True(report.Result[0].OutOfSampleTo < report.Result[1].OutOfSampleFrom);
        Assert.True(report.Result[1].OutOfSampleTo < report.Result[2].OutOfSampleFrom);
        Assert.True(report.Result[0].InSampleSessions < report.Result[2].InSampleSessions);
    }

    [Fact]
    public void WalkForwardWithTooFewSessionsIsReportedAsUnverified()
    {
        var (rows, trades) = Sample(("E1", 1.0, 0), ("E2", 2.0, 1));

        var report = WalkForwardEvaluator.Evaluate(Vx.Link(rows, trades).Candidates, 3, Vx.Small);

        Assert.Equal(0, report.Folds);
        Assert.Empty(report.Result);
        Assert.Contains($"{WalkForwardEvaluator.InsufficientSessions}:2/4", report.Limitations);
    }

    // ── 비용 민감도 ────────────────────────────────────────────────────────────────────────

    /// <summary>보수적 비용 시나리오는 별도 필드다. 원본 실현 손익을 덮어쓰지 않는다.</summary>
    [Fact]
    public void CostScenarioNeverOverwritesRealizedPnl()
    {
        var (missingRow, missingTrade) = Vx.Entered("E1", 1.0, 0, missingLiquidity: true);
        var (okRow, okTrade) = Vx.Entered("E2", 2.0, 1);
        var candidates = Vx.Link([missingRow, okRow], [missingTrade, okTrade]).Candidates;

        var results = CostSensitivity.Evaluate(candidates);

        var missingOnly = Assert.Single(results, x => x.Name == "missing-liquidity-25bps");
        Assert.Equal(2, missingOnly.Samples);
        Assert.Equal(1, missingOnly.AdjustedSamples);
        Assert.Equal(1.5, missingOnly.RealizedMeanPercent);
        Assert.Equal(1.375, missingOnly.ScenarioMeanPercent);

        var all = Assert.Single(results, x => x.Name == "all-trades-25bps");
        Assert.Equal(2, all.AdjustedSamples);
        Assert.Equal(1.5, all.RealizedMeanPercent);
        Assert.Equal(1.25, all.ScenarioMeanPercent);

        // 연결 결과의 실현 손익은 시나리오 계산 후에도 그대로다.
        Assert.Equal([1.0, 2.0], candidates.Select(x => x.RealizedPnlPercent).ToArray());
    }

    [Fact]
    public void CostScenarioWithoutRealizedTradesReportsNullInsteadOfZero()
    {
        var results = CostSensitivity.Evaluate([]);

        Assert.All(results, x =>
        {
            Assert.Equal(0, x.Samples);
            Assert.Null(x.RealizedMeanPercent);
            Assert.Null(x.ScenarioMeanPercent);
            Assert.Null(x.ScenarioWinRatePercent);
        });
    }

    [Fact]
    public void EvaluationAlwaysCarriesTheNoGuaranteeCaveats()
    {
        var evaluation = ValidationEvaluator.Evaluate([], Vx.AsOf);

        Assert.Equal(ValidationEvaluator.StandardCaveats, evaluation.Caveats);
        Assert.Contains(evaluation.Caveats, x => x.Contains("보장하지 않는다", StringComparison.Ordinal));
    }
}
