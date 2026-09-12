using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §7 추세 상태 히스테리시스(D10). 진입·이탈 임계값 분리와 라벨 채터링 감소를 고정한다(§7, #148).</summary>
public sealed class StructureTrendHysteresisTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static TrendStateSample Bar(double signedTrend, double efficiency, bool opposed = false) =>
        new(true, signedTrend, efficiency, opposed);

    static TrendState Run(params TrendStateSample[] samples) => TrendEvaluator.StateSequence(samples, P);

    [Fact]
    public void ASingleQualifyingBarDoesNotEnterUp()
    {
        Assert.Equal(TrendState.Range, Run(Bar(10, .10), Bar(60, .40)));
    }

    [Fact]
    public void TwoConsecutiveQualifyingBarsEnterUp()
    {
        Assert.Equal(TrendState.Up, Run(Bar(10, .10), Bar(60, .40), Bar(62, .38)));
    }

    [Fact]
    public void TwoConsecutiveQualifyingBarsBelowZeroEnterDown()
    {
        Assert.Equal(TrendState.Down, Run(Bar(-10, .10), Bar(-60, .40), Bar(-62, .38)));
    }

    [Fact]
    public void AOneBarEfficiencySpikeBetweenNonQualifyingBarsIsIgnored()
    {
        Assert.Equal(TrendState.Range, Run(Bar(60, .10), Bar(60, .40), Bar(60, .10), Bar(60, .40)));
    }

    [Fact]
    public void AnEfficiencyDipAboveTheExitThresholdKeepsUp()
    {
        Assert.Equal(TrendState.Up, Run(Bar(60, .40), Bar(60, .40), Bar(60, .22), Bar(60, .22)));
    }

    [Fact]
    public void OneBarBelowTheExitEfficiencyKeepsUpButTwoBarsLeaveToRange()
    {
        Assert.Equal(TrendState.Up, Run(Bar(60, .40), Bar(60, .40), Bar(60, .19)));
        Assert.Equal(TrendState.Range, Run(Bar(60, .40), Bar(60, .40), Bar(60, .19), Bar(60, .19)));
    }

    [Fact]
    public void OneBarBelowTheExitSignedTrendKeepsUpButTwoBarsLeaveToRange()
    {
        Assert.Equal(TrendState.Up, Run(Bar(60, .40), Bar(60, .40), Bar(19, .40)));
        Assert.Equal(TrendState.Range, Run(Bar(60, .40), Bar(60, .40), Bar(19, .40), Bar(19, .40)));
    }

    [Fact]
    public void TheEntryAndExitThresholdsAreDistinctSoTheMiddleBandHolds()
    {
        Assert.True(P.TrendExitEfficiency < P.TrendEfficiencyThreshold);
        Assert.True(P.TrendExitSignedTrend < P.TrendStateThreshold);
        Assert.Equal(TrendState.Up, Run(Bar(60, .40), Bar(60, .40), Bar(22, .22), Bar(22, .22), Bar(22, .22)));
    }

    [Fact]
    public void AnOpposedBarBecomesTransitionOnItsFirstBar()
    {
        Assert.Equal(TrendState.Transition, Run(Bar(60, .40), Bar(60, .40), Bar(60, .40, opposed: true)));
    }

    [Fact]
    public void AnOpposedBarBelowTheEntryEfficiencyIsNotATrendLabel()
    {
        Assert.Equal(TrendState.Range, Run(Bar(10, .05, opposed: true), Bar(10, .05, opposed: true)));
    }

    [Fact]
    public void TransitionLeavesToRangeOnlyAfterTwoExitBars()
    {
        Assert.Equal(TrendState.Transition, Run(Bar(60, .40, opposed: true), Bar(60, .19)));
        Assert.Equal(TrendState.Range, Run(Bar(60, .40, opposed: true), Bar(60, .19), Bar(60, .19)));
    }

    [Fact]
    public void AnUnavailableBarResetsToUnknownAndTheRunStartsOver()
    {
        Assert.Equal(TrendState.Unknown, Run(Bar(60, .40), Bar(60, .40), new TrendStateSample(false, 0, 0, false)));
        Assert.Equal(TrendState.Range,
            Run(Bar(60, .40), new TrendStateSample(false, 0, 0, false), Bar(60, .40)));
    }

    [Fact]
    public void ADirectionFlipRestartsTheEntryRun()
    {
        Assert.Equal(TrendState.Range, Run(Bar(60, .40), Bar(-60, .40)));
        Assert.Equal(TrendState.Down, Run(Bar(60, .40), Bar(-60, .40), Bar(-60, .40)));
    }

    [Fact]
    public void TheSameSampleSequenceAlwaysProducesTheSameStateAfterARestart()
    {
        var samples = Intc.Samples();
        Assert.Equal(TrendEvaluator.StateSequence(samples, P), TrendEvaluator.StateSequence(samples, P));
        Assert.Equal(TrendEvaluator.StateSequence(samples, P),
            TrendEvaluator.StateSequence(samples.ToArray(), P));
    }

    // ── 2026-09-11 INTC 00:38~01:12 관측 회귀 ──

    [Fact]
    public void TheRecordedIntcWindowFlippedEightTimesBeforeTheHysteresis()
    {
        Assert.Equal(8, Flips(Intc.Rows.Select(x => x.RecordedState)));
    }

    [Fact]
    public void TheHysteresisReducesTheIntcWindowFlipsFromEightToFive()
    {
        var recorded = Flips(Intc.Rows.Select(x => x.RecordedState));
        var labels = Labels();

        Assert.Equal(5, Flips(labels));
        Assert.True(Flips(labels) < recorded, $"recorded={recorded} hysteresis={Flips(labels)}");
    }

    [Fact]
    public void TheThreeMinuteUpRangeUpRangeChatterAtTheIntcOpenIsGone()
    {
        var labels = Labels();
        var window = Intc.Rows.Select((x, i) => (x.Minute, Label: labels[i]))
            .Where(x => x.Minute is "00:43" or "00:44" or "00:45" or "00:46").ToArray();

        Assert.All(window, x => Assert.Equal(TrendState.Up, x.Label));
        Assert.Equal(["UP", "RANGE", "UP", "RANGE"], Intc.Rows
            .Where(x => x.Minute is "00:43" or "00:44" or "00:45" or "00:46")
            .Select(x => x.RecordedState).ToArray());
    }

    static List<TrendState> Labels()
    {
        var samples = Intc.Samples();
        var labels = new List<TrendState>();
        for (var i = 1; i <= samples.Count; i++)
            labels.Add(TrendEvaluator.StateSequence(samples.Take(i), P));
        return labels;
    }

    static int Flips<T>(IEnumerable<T> labels)
    {
        var list = labels.ToArray();
        var flips = 0;
        for (var i = 1; i < list.Length; i++)
            if (!Equals(list[i], list[i - 1])) flips++;
        return flips;
    }

    /// <summary>App_Data 관측 JSONL에서 뽑은 실제 봉별 추세 값. 종목 추천이 아니라 회귀 고정용이다.</summary>
    static class Intc
    {
        internal sealed record Row(string Minute, double SignedTrend, double Efficiency, double PriceDirection,
            double StructureDirection, string RecordedState);

        internal static List<TrendStateSample> Samples() => Rows
            .Select(x => new TrendStateSample(true, x.SignedTrend, x.Efficiency,
                Math.Sign(x.PriceDirection) * Math.Sign(x.StructureDirection) < 0))
            .ToList();

        internal static readonly Row[] Rows =
        [
            new("00:38", 8.575677, 0.013145, 0.195133, -0.023620, "RANGE"),
            new("00:39", 8.961992, 0.164828, 0.200549, -0.021309, "RANGE"),
            new("00:40", 56.786070, 0.293592, 0.169578, 0.966143, "UP"),
            new("00:41", 59.679075, 0.293592, 0.230666, 0.962916, "UP"),
            new("00:42", 59.765978, 0.318844, 0.227636, 0.967684, "UP"),
            new("00:43", 60.403361, 0.291170, 0.235378, 0.972690, "UP"),
            new("00:44", 63.155778, 0.184354, 0.286956, 0.976160, "RANGE"),
            new("00:45", 67.383722, 0.284672, 0.372690, 0.974984, "UP"),
            new("00:46", 65.473619, 0.049180, 0.336453, 0.973020, "RANGE"),
            new("00:47", 56.108966, 0.188406, 0.157892, 0.964287, "RANGE"),
            new("00:48", 61.561632, 0.031576, 0.273581, 0.957652, "RANGE"),
            new("00:49", 64.185336, 0.006061, 0.323649, 0.960057, "RANGE"),
            new("00:50", 62.819406, 0.092105, 0.292167, 0.964221, "RANGE"),
            new("00:51", 65.322809, 0.151899, 0.339501, 0.966955, "RANGE"),
            new("00:52", 65.903016, 0.109677, 0.348057, 0.970003, "RANGE"),
            new("00:53", 65.150955, 0.114423, 0.328628, 0.974391, "RANGE"),
            new("00:54", 59.668822, 0.012422, 0.220734, 0.972642, "RANGE"),
            new("00:55", 37.603008, 0.015974, 0.177978, 0.574082, "RANGE"),
            new("00:56", 32.907218, 0.056604, 0.085081, 0.573064, "RANGE"),
            new("00:57", 28.299716, 0.062500, -0.008965, 0.574960, "RANGE"),
            new("00:58", 28.585119, 0.028391, -0.005411, 0.577113, "RANGE"),
            new("00:59", 29.644933, 0.025316, 0.014165, 0.578733, "RANGE"),
            new("01:00", 31.856629, 0.070229, 0.054937, 0.582195, "RANGE"),
            new("01:01", 25.938674, 0.102620, -0.055765, 0.574538, "RANGE"),
            new("01:02", 24.915793, 0.092541, -0.079564, 0.577880, "RANGE"),
            new("01:03", 27.760376, 0.034686, -0.024241, 0.579448, "RANGE"),
            new("01:04", 29.452187, 0.059179, 0.005000, 0.584043, "RANGE"),
            new("01:05", 20.272807, 0.254172, -0.169995, 0.575452, "TRANSITION"),
            new("01:06", 19.927841, 0.190905, -0.182512, 0.581068, "RANGE"),
            new("01:07", 19.319586, 0.059218, -0.198780, 0.585172, "RANGE"),
            new("01:08", 19.010711, 0.248100, -0.212442, 0.592656, "RANGE"),
            new("01:09", 14.847161, 0.371268, -0.294246, 0.591189, "TRANSITION"),
            new("01:10", 7.898474, 0.425664, -0.427091, 0.585060, "TRANSITION"),
            new("01:11", 12.947500, 0.312142, -0.319747, 0.578697, "TRANSITION"),
            new("01:12", 21.315173, 0.144492, -0.139705, 0.566009, "RANGE"),
        ];
    }
}
