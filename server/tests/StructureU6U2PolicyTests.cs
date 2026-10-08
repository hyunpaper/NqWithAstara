using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>REBOUND 전용 거절(U6)·정오 창 차단(U2) 정책 계약과 거절 판정(§9.3, #245).</summary>
public sealed class StructureU6U2PolicyTests
{
    [Fact]
    public void NullOverridesKeepTheDefaultHashAndCanonicalJson()
    {
        var json = StructurePolicy.Default.CanonicalJson;
        Assert.Null(StructurePolicy.Default.ReboundMaxTrendAlignment);
        Assert.Null(StructurePolicy.Default.ReboundMinInvalidationAtr);
        Assert.Null(StructurePolicy.Default.WindowBlockStartMinutesFromOpen);
        Assert.Null(StructurePolicy.Default.WindowBlockEndMinutesFromOpen);
        Assert.DoesNotContain("ReboundMaxTrendAlignment", json);
        Assert.DoesNotContain("ReboundMinInvalidationAtr", json);
        Assert.DoesNotContain("WindowBlockStartMinutesFromOpen", json);
        Assert.DoesNotContain("WindowBlockEndMinutesFromOpen", json);
        Assert.Equal("d7683e146a40f5ac329b5d9bc25fe081ff6755b58c78018ac05fbb8bf6049b9b", StructurePolicy.Default.PolicyHash);
    }

    [Fact]
    public void SettingAnOverrideChangesTheHash()
    {
        var baseline = StructurePolicy.Default.PolicyHash;
        var align = StructurePolicy.Default with { ReboundMaxTrendAlignment = 0.0 };
        var invalidation = StructurePolicy.Default with { ReboundMinInvalidationAtr = 2.0 };
        var window = StructurePolicy.Default with { WindowBlockStartMinutesFromOpen = 90, WindowBlockEndMinutesFromOpen = 300 };
        Assert.NotEqual(baseline, align.PolicyHash);
        Assert.NotEqual(baseline, invalidation.PolicyHash);
        Assert.NotEqual(baseline, window.PolicyHash);
        Assert.Contains("\"ReboundMaxTrendAlignment\":0", align.CanonicalJson);
        Assert.Contains("\"ReboundMinInvalidationAtr\":2", invalidation.CanonicalJson);
        Assert.Contains("\"WindowBlockStartMinutesFromOpen\":90", window.CanonicalJson);
        Assert.Contains("\"WindowBlockEndMinutesFromOpen\":300", window.CanonicalJson);
    }

    [Fact]
    public void TrendAlignedReboundIsRejectedOnlyAtOrAboveThreshold()
    {
        var policy = StructurePolicy.Default with { ReboundMaxTrendAlignment = 0.0 };
        Assert.True(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, 0.1, policy));
        Assert.True(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, 0.0, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, -0.1, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, null, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Pullback, 0.5, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, 0.5, StructurePolicy.Default));
    }

    [Fact]
    public void CloseInvalidationReboundIsRejectedOnlyBelowThreshold()
    {
        var policy = StructurePolicy.Default with { ReboundMinInvalidationAtr = 2.0 };
        Assert.True(SetupDetector.RejectsReboundInvalidationTooClose(SetupKind.Rebound, 1.5, policy));
        Assert.False(SetupDetector.RejectsReboundInvalidationTooClose(SetupKind.Rebound, 2.0, policy));
        Assert.False(SetupDetector.RejectsReboundInvalidationTooClose(SetupKind.Rebound, 2.5, policy));
        Assert.False(SetupDetector.RejectsReboundInvalidationTooClose(SetupKind.Rebound, null, policy));
        Assert.False(SetupDetector.RejectsReboundInvalidationTooClose(SetupKind.Pullback, 1.0, policy));
        Assert.False(SetupDetector.RejectsReboundInvalidationTooClose(SetupKind.Rebound, 1.0, StructurePolicy.Default));
    }

    [Fact]
    public void EntryBlockWindowCoversHalfOpenIntervalOnlyWhenBothBoundsSet()
    {
        var policy = StructurePolicy.Default with { WindowBlockStartMinutesFromOpen = 90, WindowBlockEndMinutesFromOpen = 300 };
        Assert.False(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(89)));
        Assert.True(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(90)));
        Assert.True(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(299)));
        Assert.False(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(300)));
        Assert.False(StructurePolicy.Default.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(120)));
        var startOnly = StructurePolicy.Default with { WindowBlockStartMinutesFromOpen = 90 };
        Assert.False(startOnly.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(120)));
    }
}
