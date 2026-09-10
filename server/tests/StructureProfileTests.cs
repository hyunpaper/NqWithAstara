using System.Collections.Immutable;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>설계 §6.2. 추정 분포이며 거래량 보존·zero-range·zero-volume·bin 상한을 고정한다.</summary>
public sealed class StructureProfileTests
{
    static readonly StructurePolicy P = StructurePolicy.Default;

    static ImmutableArray<StructureBar> Ladder(int count, decimal step, double volume = 1000) =>
        Enumerable.Range(0, count)
            .Select(i => Fx.Bar(i, 100 + i * step, 100.2m + i * step, 99.8m + i * step, 100.1m + i * step, volume))
            .ToImmutableArray();

    [Fact]
    public void BinWidthIsHalfAtrWithATickFloorAndZeroAlignedAxis()
    {
        var withAtr = ZoneBuilder.BuildVolumeProfile(Ladder(5, .05m), .40, P);
        Assert.Equal(.20m, withAtr.BinWidth);
        Assert.All(withAtr.Bins, bin => Assert.Equal(bin.Index * withAtr.BinWidth, bin.Lower));

        var withoutAtr = ZoneBuilder.BuildVolumeProfile(Ladder(5, .05m), null, P);
        Assert.Equal(P.PriceTick, withoutAtr.BinWidth);
        var zeroAtr = ZoneBuilder.BuildVolumeProfile(Ladder(5, .05m), 0, P);
        Assert.Equal(P.PriceTick, zeroAtr.BinWidth);
    }

    [Fact]
    public void TotalBinVolumeEqualsInputVolumeWithinTolerance()
    {
        var bars = Ladder(40, .03m, 777.5);
        var profile = ZoneBuilder.BuildVolumeProfile(bars, .30, P);
        var expected = bars.Sum(x => x.Volume);
        Assert.Equal(expected, profile.InputVolume, 6);
        Assert.Equal(expected, profile.Bins.Sum(x => x.Volume), 6);
        Assert.Equal(expected, profile.AllocatedVolume, 6);
        Assert.All(profile.Bins, bin => Assert.True(double.IsFinite(bin.Volume) && bin.Volume >= 0));
    }

    [Fact]
    public void ZeroRangeBarPutsAllVolumeInOneBin()
    {
        var bars = Fx.Bars(new StructureBar(Fx.At(0), Fx.At(1), 100m, 100m, 100m, 100m, 5000));
        var profile = ZoneBuilder.BuildVolumeProfile(bars, .20, P);
        var nonEmpty = profile.Bins.Where(x => x.Volume > 0).ToArray();
        Assert.Single(nonEmpty);
        Assert.Equal(5000, nonEmpty[0].Volume, 6);
        Assert.Equal(profile.PocIndex, nonEmpty[0].Index);
    }

    [Fact]
    public void ZeroVolumeProfileIsEmptyAndFlagged()
    {
        var bars = Ladder(5, .05m, 0);
        var profile = ZoneBuilder.BuildVolumeProfile(bars, .20, P);
        Assert.Empty(profile.Bins);
        Assert.Empty(profile.Nodes);
        Assert.Null(profile.PocIndex);
        Assert.Contains("ZERO_VOLUME_PROFILE", profile.Warnings);
    }

    [Fact]
    public void NoBarsProducesAnEmptyProfileRatherThanAnException()
    {
        var profile = ZoneBuilder.BuildVolumeProfile(ImmutableArray<StructureBar>.Empty, .20, P);
        Assert.Empty(profile.Bins);
        Assert.Contains("PROFILE_NO_BARS", profile.Warnings);
    }

    [Fact]
    public void BinCountStaysUnderTheLimitAndCoarseningIsFlagged()
    {
        // tick 폭(0.01)으로는 4000 bin이 필요한 넓은 범위. 정수배 확장으로 상한 안에 들어가야 한다.
        var bars = Fx.Bars(
            new StructureBar(Fx.At(0), Fx.At(1), 100m, 100m, 100m, 100m, 10),
            new StructureBar(Fx.At(1), Fx.At(2), 100m, 140m, 100m, 140m, 10));
        var profile = ZoneBuilder.BuildVolumeProfile(bars, null, P);
        Assert.True(profile.Bins.Length <= P.ProfileMaxBins, $"bins={profile.Bins.Length}");
        Assert.True(profile.Coarsened);
        Assert.Contains("CoarsenedProfile", profile.Warnings);
        Assert.Equal(P.PriceTick * profile.BinWidthMultiple, profile.BinWidth);
        Assert.Equal(20, profile.Bins.Sum(x => x.Volume), 6);
    }

    [Fact]
    public void ProfileIsMarkedAsAnEstimate()
    {
        var profile = ZoneBuilder.BuildVolumeProfile(Ladder(5, .05m), .20, P);
        Assert.Contains("EstimatedVolumeProfile", profile.Warnings);
    }

    [Fact]
    public void PocPrefersTheLowerPriceBinOnATie()
    {
        var bars = Fx.Bars(
            new StructureBar(Fx.At(0), Fx.At(1), 100.05m, 100.05m, 100.05m, 100.05m, 1000),
            new StructureBar(Fx.At(1), Fx.At(2), 100.35m, 100.35m, 100.35m, 100.35m, 1000));
        var profile = ZoneBuilder.BuildVolumeProfile(bars, .40, P);   // binWidth 0.20
        var top = profile.Bins.Where(x => x.Volume > 0).OrderBy(x => x.Index).ToArray();
        Assert.Equal(2, top.Length);
        Assert.Equal(top[0].Volume, top[1].Volume, 6);
        Assert.Equal(top[0].Index, profile.PocIndex);
    }

    [Fact]
    public void NodesAreCappedAtThreeAndMergeAdjacentBins()
    {
        var bars = new List<StructureBar>();
        var minute = 0;
        void Cluster(decimal price, int count)
        {
            for (var i = 0; i < count; i++, minute++)
                bars.Add(new StructureBar(Fx.At(minute), Fx.At(minute + 1), price, price, price, price, 100));
        }
        Cluster(100.05m, 10);
        Cluster(100.25m, 10);     // 100.05 bin과 연접(binWidth 0.20) · 동률 국소 최대
        Cluster(101.05m, 8);
        Cluster(102.05m, 7);
        Cluster(103.05m, 6);

        var profile = ZoneBuilder.BuildVolumeProfile(bars.ToImmutableArray(), .40, P);
        Assert.Equal(.20m, profile.BinWidth);
        Assert.True(profile.Nodes.Length <= P.ProfileMaxNodes);
        Assert.Equal(3, profile.Nodes.Length);
        var merged = Assert.Single(profile.Nodes.Where(x => x.EndIndex > x.StartIndex));
        Assert.Equal(2000, merged.Volume, 6);
        Assert.True(merged.IsPoc);
        Assert.Equal(bars.Sum(x => x.Volume), profile.Bins.Sum(x => x.Volume), 6);
    }

    [Fact]
    public void ProfileNeverLoopsForeverOnTinyOrZeroAtr()
    {
        foreach (var atr in new double?[] { null, 0, -1, double.NaN, 1e-12, 1e-9 })
        {
            var profile = ZoneBuilder.BuildVolumeProfile(Ladder(10, .05m), atr, P);
            Assert.True(profile.BinWidth > 0);
            Assert.True(profile.Bins.Length <= P.ProfileMaxBins);
        }
    }
}
