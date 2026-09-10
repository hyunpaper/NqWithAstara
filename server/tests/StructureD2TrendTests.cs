using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>
/// 설계 §7 + §16B 추세 평가. 방향 강도와 진입 위치 품질은 분리되며 상태 label과 숫자를 둘 다 노출한다.
/// 고정 가점·hard cap을 만들지 않고 작은 입력 변화가 연속적으로 반영되는지 고정한다(§15).
/// </summary>
public sealed class StructureD2TrendTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    /// <summary>1분봉을 만든다. 5분봉은 D1 집계기로 만들어 구조 family가 실제 경로로 생기게 한다.</summary>
    static StructureBar Bar(int minute, decimal center, decimal half = .10m, decimal? close = null, double volume = 1000)
        => new(Fx.At(minute), Fx.At(minute + 1), center, center + half, center - half, close ?? center, volume);

    /// <summary>bucket별 중심값을 5개 1분봉으로 펼친다. 같은 bucket 안에서는 같은 모양이라 5분 집계가 그 중심을 그린다.</summary>
    static ImmutableArray<StructureBar> FromBuckets(IReadOnlyList<decimal> centers, decimal half = .10m,
        double volume = 1000)
    {
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var k = 0; k < centers.Count; k++)
            for (var i = 0; i < 5; i++)
                bars.Add(Bar(k * 5 + i, centers[k], half, volume: volume));
        return bars.ToImmutable();
    }

    static TrendAssessment Evaluate(ImmutableArray<StructureBar> bars, StructurePolicy? policy = null)
    {
        var p = policy ?? P;
        var cutoff = bars.Length == 0 ? Fx.SessionStart : bars[^1].End;
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, p);
        return TrendEvaluator.Evaluate(TrendRequest.Create(Fx.Symbol, Fx.SessionStart, cutoff, bars, five), p);
    }

    /// <summary>확정 5분 피벗 2+2가 생기는 지그재그. 상승 지그재그이므로 구조 방향은 +다.</summary>
    static readonly decimal[] RisingZigzag =
        [100.0m, 100.4m, 100.9m, 100.5m, 100.1m, 100.6m, 101.1m, 100.7m, 100.3m, 100.8m, 101.3m, 100.9m, 100.5m];

    // ── 최소 관측과 결측 ──

    [Fact]
    public void FewerThanThirtyCompletedBarsIsUnknownAndNeverZero()
    {
        var bars = FromBuckets([100.0m, 100.1m, 100.2m, 100.3m, 100.4m, 100.5m]).Take(29).ToImmutableArray();
        var trend = Evaluate(bars);

        Assert.Equal(TrendState.Unknown, trend.State);
        Assert.Null(trend.SignedTrend);
        Assert.False(trend.Available);
        Assert.Contains(TrendEvaluator.WarningInsufficientBars, trend.Warnings);
        Assert.Contains(TrendEvaluator.BlockerTrendUnavailable, trend.BlockersForReady);
        Assert.Equal(29, trend.BarCount);
    }

    [Fact]
    public void EmptyInputIsUnknownWithoutThrowingOrFabricatingIndicators()
    {
        var trend = Evaluate(ImmutableArray<StructureBar>.Empty);

        Assert.Equal(TrendState.Unknown, trend.State);
        Assert.Null(trend.SignedTrend);
        Assert.Null(trend.Atr1m);
        Assert.Null(trend.Vwap);
        Assert.Null(trend.PriceDirection);
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);
    }

    /// <summary>§16B: ATR&lt;=0 또는 필수 지표 결측이면 trend=null이다.</summary>
    [Fact]
    public void ZeroRangeBarsGiveZeroAtrAndThereforeNullTrend()
    {
        var bars = Enumerable.Range(0, 40).Select(i => Bar(i, 100m, 0m)).ToImmutableArray();
        var trend = Evaluate(bars);

        Assert.Equal(0, trend.Atr1m);
        Assert.Equal(TrendState.Unknown, trend.State);
        Assert.Null(trend.SignedTrend);
        Assert.Contains(TrendEvaluator.WarningAtrUnavailable, trend.Warnings);
        Assert.Contains("atr1m", trend.MissingComponents);
    }

    [Fact]
    public void ZeroVolumeSessionLeavesVwapNullAndBlocksTheTrend()
    {
        var bars = FromBuckets(RisingZigzag, volume: 0);
        var trend = Evaluate(bars);

        Assert.Null(trend.Vwap);
        Assert.Null(trend.VwapSd);
        Assert.Equal(TrendState.Unknown, trend.State);
        Assert.Contains("vwapDirection", trend.MissingComponents);
        Assert.Contains(TrendEvaluator.BlockerTrendUnavailable, trend.BlockersForReady);
    }

    // ── family 구성 ──

    /// <summary>§7: 상관된 ema/slope/vwap은 priceDirection 한 family로 묶는다. family 수로 가중치를 몰래 바꾸지 않는다.</summary>
    [Fact]
    public void CorrelatedPriceComponentsCollapseIntoASinglePriceFamily()
    {
        var trend = Evaluate(FromBuckets(RisingZigzag));
        var price = trend.Components.Where(x => x.Family == "price").ToArray();

        Assert.Equal(3, price.Length);
        Assert.All(price, component => Assert.NotNull(component.Value));
        Assert.Equal(price.Average(x => x.Value!.Value), trend.PriceDirection!.Value, 12);
        Assert.Contains("price", trend.UsedFamilies);
    }

    /// <summary>§7/§16B: 구조 family가 있으면 signedTrend=100*mean(price,structure)다.</summary>
    [Fact]
    public void SignedTrendIsTheMeanOfThePriceAndStructureFamilies()
    {
        var trend = Evaluate(FromBuckets(RisingZigzag));

        Assert.NotNull(trend.StructureDirection);
        Assert.False(trend.StructureEvidenceMissing);
        Assert.Contains("structure", trend.UsedFamilies);
        Assert.Equal(100 * (trend.PriceDirection!.Value + trend.StructureDirection!.Value) / 2,
            trend.SignedTrend!.Value, 12);
        Assert.DoesNotContain(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);
    }

    /// <summary>§7/§16B: 구조 증거가 없으면 가격 family만 쓰고 MISSING_5M_STRUCTURE로 신규 READY를 막는다.</summary>
    [Fact]
    public void MissingFiveMinuteStructureUsesThePriceFamilyOnlyAndBlocksReady()
    {
        // 30개 완료 봉은 있지만 5분 피벗 2+2가 아직 확정되지 않은 구간이다.
        var bars = FromBuckets([100.0m, 100.1m, 100.2m, 100.3m, 100.4m, 100.5m, 100.6m]);
        var trend = Evaluate(bars);

        Assert.Null(trend.StructureDirection);
        Assert.True(trend.StructureEvidenceMissing);
        Assert.NotNull(trend.SignedTrend);
        Assert.Equal(100 * trend.PriceDirection!.Value, trend.SignedTrend!.Value, 12);
        Assert.Equal(new[] { "price" }, trend.UsedFamilies.ToArray());
        Assert.Contains(TrendEvaluator.BlockerMissing5mStructure, trend.BlockersForReady);
        Assert.Contains("structureDirection", trend.MissingComponents);
    }

    /// <summary>§14 E와 같은 규칙: 5분 피벗은 우측 확인에 10분이 필요하며 그 전에는 구조 방향이 없다.</summary>
    [Fact]
    public void FiveMinutePivotsOnlyAppearAfterTheirRightSideConfirmation()
    {
        var bars = FromBuckets(RisingZigzag);
        // 두 번째 low pivot(bucket 8)의 우측 확인 봉은 bucket 10이고 확인 시각은 55분이다.
        var before = Evaluate(bars.Where(x => x.End <= Fx.At(50)).ToImmutableArray());
        var after = Evaluate(bars.Where(x => x.End <= Fx.At(55)).ToImmutableArray());

        Assert.Null(before.StructureDirection);
        Assert.NotNull(after.StructureDirection);
    }

    // ── 상태 판정 ──

    /// <summary>§16B: efficiency&lt;0.25이면 RANGE다.</summary>
    [Fact]
    public void LowEfficiencyIsRangeRegardlessOfTheDirectionScore()
    {
        var centers = new decimal[26];
        for (var i = 0; i < centers.Length; i++) centers[i] = 100m + (i % 2 == 0 ? 0m : .30m);
        var bars = FromBuckets(centers);
        var trend = Evaluate(bars);

        Assert.True(trend.Efficiency < P.TrendEfficiencyThreshold);
        Assert.Equal(TrendState.Range, trend.State);
        Assert.NotNull(trend.SignedTrend);       // 숫자는 여전히 보인다(§19-9)
    }

    /// <summary>§16B: 임계값을 넘고 두 family 부호가 반대가 아닐 때만 UP이다.</summary>
    [Fact]
    public void StrongAlignedUptrendIsUpWithBothFamiliesPresent()
    {
        var trend = Evaluate(FromBuckets(AlignedUptrend));

        Assert.True(trend.PriceDirection > 0, $"price={trend.PriceDirection}");
        Assert.True(trend.StructureDirection > 0, $"structure={trend.StructureDirection}");
        Assert.True(trend.SignedTrend >= P.TrendStateThreshold, $"signedTrend={trend.SignedTrend}");
        Assert.True(trend.Efficiency >= P.TrendEfficiencyThreshold, $"efficiency={trend.Efficiency}");
        Assert.Equal(TrendState.Up, trend.State);
        Assert.Equal(new[] { "price", "structure" }, trend.UsedFamilies.ToArray());
    }

    [Fact]
    public void StrongAlignedDowntrendIsDownAndIsNotAShortEntryInstruction()
    {
        var down = FromBuckets(AlignedUptrend).Select(x =>
            new StructureBar(x.Start, x.End, 300m - x.Open, 300m - x.Low, 300m - x.High, 300m - x.Close, x.Volume))
            .ToImmutableArray();
        var trend = Evaluate(down);

        Assert.Equal(TrendState.Down, trend.State);
        Assert.True(trend.SignedTrend <= -P.TrendStateThreshold);
        Assert.True(trend.StructureDirection < 0);
    }

    /// <summary>§16B: 부호가 반대면 임계값을 넘어도 UP/DOWN이 아니라 TRANSITION이다.</summary>
    [Fact]
    public void OpposedFamilySignsBecomeTransitionNotUpOrDown()
    {
        var trend = Evaluate(OpposedFixture());

        Assert.NotNull(trend.StructureDirection);
        Assert.True(Math.Sign(trend.PriceDirection!.Value) * Math.Sign(trend.StructureDirection!.Value) < 0,
            $"price={trend.PriceDirection} structure={trend.StructureDirection}");
        Assert.True(trend.Efficiency >= P.TrendEfficiencyThreshold, $"efficiency={trend.Efficiency}");
        Assert.Equal(TrendState.Transition, trend.State);
    }

    /// <summary>§16B: 부호는 수학적 sign이며 0은 반대 부호가 아니다.</summary>
    [Fact]
    public void AZeroStructureDirectionIsNotAnOpposingSign()
    {
        // 두 high pivot과 두 low pivot의 가격이 같으면 delta=0 → structureDirection=0이다.
        var centers = new List<decimal>();
        for (var k = 0; k < 13; k++) centers.Add((k % 4) switch { 0 => 100.0m, 1 => 100.2m, 2 => 100.4m, _ => 100.2m });
        var trend = Evaluate(FromBuckets(centers));

        Assert.Equal(0, trend.StructureDirection);
        Assert.Equal(TrendState.Range, trend.State);   // 지그재그이므로 efficiency가 낮다
        Assert.NotNull(trend.SignedTrend);
    }

    // ── 연속성·결정성·수치 안전 ──

    /// <summary>§15/§19-2: 작은 입력 변화가 계단이 아니라 연속적으로 반영된다.</summary>
    [Fact]
    public void SmallInputChangesMoveTheScoreContinuously()
    {
        var baseline = Evaluate(StrongUptrend());
        var nudged = Evaluate(StrongUptrend(extraClose: .01m));
        var pushed = Evaluate(StrongUptrend(extraClose: .06m));

        var small = Math.Abs(nudged.SignedTrend!.Value - baseline.SignedTrend!.Value);
        var large = Math.Abs(pushed.SignedTrend!.Value - baseline.SignedTrend!.Value);

        Assert.True(small > 0, "작은 변화가 완전히 무시되면 계단식 배점이다.");
        Assert.True(small < large, $"small={small} large={large}");
        Assert.True(large < 100);
    }

    /// <summary>tanh는 큰 입력에서 둔화하지만 2배 이후 동일 가점을 주는 hard cap이 아니다(§7).</summary>
    [Fact]
    public void LargeInputsKeepMovingTheScoreWithoutAHardCap()
    {
        var single = Evaluate(StrongUptrend(extraClose: .10m)).SignedTrend!.Value;
        var doubled = Evaluate(StrongUptrend(extraClose: .20m)).SignedTrend!.Value;
        var quadrupled = Evaluate(StrongUptrend(extraClose: .40m)).SignedTrend!.Value;

        Assert.True(doubled > single, $"1배={single} 2배={doubled}");
        Assert.True(quadrupled > doubled, $"2배={doubled} 4배={quadrupled}");
        Assert.True(quadrupled < 100);
    }

    /// <summary>연속 변환은 tanh 그대로여야 한다. 계단식 ±4/±5 가점을 함수 이름 안에 숨기지 않는다(§19-2).</summary>
    [Fact]
    public void EachDirectionComponentIsExactlyTanhOfItsRawInput()
    {
        var trend = Evaluate(FromBuckets(AlignedUptrend));

        foreach (var component in trend.Components.Where(x => x.Family == "price"))
            Assert.Equal(Math.Tanh(component.Raw!.Value), component.Value!.Value, 12);
    }

    [Fact]
    public void TheSameSnapshotAlwaysProducesTheSameAssessment()
    {
        var bars = FromBuckets(RisingZigzag);
        Assert.Equal(Evaluate(bars).Fingerprint(), Evaluate(bars).Fingerprint());
    }

    /// <summary>D1 결정 16과 동일하게 cutoff 초과 입력은 잘라내고 경고를 남긴다(미래 봉은 계산에 쓰이지 않는다).</summary>
    [Fact]
    public void FutureBarsAreTruncatedRatherThanUsed()
    {
        var bars = FromBuckets(RisingZigzag);
        var cutoff = Fx.At(50);
        var five = BarAggregator.Aggregate(bars, Fx.SessionStart, cutoff, P);
        var withFuture = TrendEvaluator.Evaluate(
            TrendRequest.Create(Fx.Symbol, Fx.SessionStart, cutoff, bars, five), P);
        var truncated = TrendEvaluator.Evaluate(TrendRequest.Create(Fx.Symbol, Fx.SessionStart, cutoff,
            bars.Where(x => x.End <= cutoff).ToImmutableArray(), five), P);

        Assert.Contains(TrendEvaluator.WarningFutureBars, withFuture.Warnings);
        Assert.Equal(50, withFuture.BarCount);
        Assert.Equal(truncated.SignedTrend, withFuture.SignedTrend);
        Assert.Equal(truncated.Atr1m, withFuture.Atr1m);
    }

    // ── 효율성과 상대 거래량의 0 분모 ──

    [Fact]
    public void EfficiencyIsZeroRatherThanNaNWhenAllClosesAreEqual()
    {
        var bars = Enumerable.Range(0, 40).Select(i => Bar(i, 100m, .10m)).ToImmutableArray();
        Assert.Equal(0, SessionIndicators.Efficiency(bars, P.EfficiencyLookbackBars));
        Assert.Null(SessionIndicators.Efficiency(bars.Take(5).ToImmutableArray(), P.EfficiencyLookbackBars));
    }

    [Fact]
    public void RelativeVolumeFollowsTheBaselineRules()
    {
        var bars = Enumerable.Range(0, 25).Select(i => Bar(i, 100m, .10m, volume: i == 24 ? 3000 : 1000))
            .ToImmutableArray();

        Assert.Equal(3, SessionIndicators.RelativeVolume(bars, Fx.At(24), 20)!.Value, 12);
        // 분모에서 트리거를 제외한다: 직전 20봉 평균만 쓴다.
        Assert.Equal(3, SessionIndicators.RelativeVolume(bars, Fx.At(24), 20)!.Value, 12);
        // 20봉 미만이면 null이다.
        Assert.Null(SessionIndicators.RelativeVolume(bars, Fx.At(5), 20));
        // 분모 0이면 null이다(§16A NO_VOLUME_BASELINE).
        var zeroBaseline = Enumerable.Range(0, 25)
            .Select(i => Bar(i, 100m, .10m, volume: i == 24 ? 1000 : 0)).ToImmutableArray();
        Assert.Null(SessionIndicators.RelativeVolume(zeroBaseline, Fx.At(24), 20));
        // 트리거 거래량 0과 양수 분모는 RV=0이다.
        var zeroTrigger = Enumerable.Range(0, 25)
            .Select(i => Bar(i, 100m, .10m, volume: i == 24 ? 0 : 1000)).ToImmutableArray();
        Assert.Equal(0, SessionIndicators.RelativeVolume(zeroTrigger, Fx.At(24), 20));
    }

    [Fact]
    public void EmaAndVwapFollowTheSessionInitializationContract()
    {
        var bars = ImmutableArray.Create(Bar(0, 100m, .10m), Bar(1, 101m, .10m));
        var ema = SessionIndicators.EmaSeries(bars, 9);

        Assert.Equal(100, ema[0]);                                   // 첫 완료 Close를 seed로 쓴다
        Assert.Equal(100 + .2 * 1, ema[1]!.Value, 12);               // alpha=2/(9+1)
        var (vwap, sd) = SessionIndicators.Vwap(bars);
        Assert.Equal((100 + 101) / 2.0, vwap!.Value, 12);            // typical=(H+L+C)/3, 같은 거래량
        Assert.Equal(.5, sd!.Value, 12);
    }

    // ── fixture ──

    /// <summary>지그재그를 유지하되 상승 폭이 커서 두 family가 모두 +이고 efficiency가 높은 경로.</summary>
    static ImmutableArray<decimal> StrongUptrendCenters(decimal slope)
    {
        var centers = ImmutableArray.CreateBuilder<decimal>();
        for (var k = 0; k < 13; k++)
            centers.Add(100m + slope * k + (k % 4 == 3 ? -slope / 2 : 0m));
        return centers.ToImmutable();
    }

    static ImmutableArray<StructureBar> StrongUptrend(decimal slope = .20m, decimal extraClose = 0m)
    {
        var centers = StrongUptrendCenters(slope);
        var bars = ImmutableArray.CreateBuilder<StructureBar>();
        for (var k = 0; k < centers.Length; k++)
            for (var i = 0; i < 5; i++)
            {
                var minute = k * 5 + i;
                var center = centers[k];
                var isLast = k == centers.Length - 1 && i == 4;
                bars.Add(new StructureBar(Fx.At(minute), Fx.At(minute + 1), center, center + .10m, center - .10m,
                    center + (isLast ? extraClose : 0m), 1000));
            }
        return bars.ToImmutable();
    }

    /// <summary>지그재그 상승이면서 순진행이 커서 두 family가 모두 +이고 efficiency도 높은 경로.</summary>
    static readonly decimal[] AlignedUptrend =
    [
        100.0m, 100.6m, 101.4m, 101.0m, 100.8m, 101.6m, 102.4m, 102.0m, 101.8m, 102.6m, 103.4m, 103.0m, 102.8m
    ];

    /// <summary>구조 family는 상승(고점·저점 상승)인데 최근 가격 파생 항목은 하락으로 돌아선 경로.</summary>
    static ImmutableArray<StructureBar> OpposedFixture()
    {
        var bars = FromBuckets(RisingZigzag).ToBuilder();
        var last = bars[^1];
        var center = last.Close;
        for (var i = 0; i < 10; i++)
        {
            var minute = bars.Count;
            center -= .30m;
            bars.Add(new StructureBar(Fx.At(minute), Fx.At(minute + 1), center, center + .10m, center - .10m,
                center, 1000));
        }
        return bars.ToImmutable();
    }
}
