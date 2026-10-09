using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>REBOUND 전용 거절(U6)·정오 창 차단(U2) 정책 계약과 거절 판정(§9.3, #245 계열 I 통합 묶음).</summary>
public sealed class StructureU6U2PolicyTests
{
    [Fact]
    public void DefaultCarriesTheIntegratedBundleInHashAndCanonicalJson()
    {
        var json = StructurePolicy.Default.CanonicalJson;
        Assert.Equal(0.0, StructurePolicy.Default.ReboundMaxTrendAlignment);
        Assert.Null(StructurePolicy.Default.ReboundMinInvalidationAtr);
        Assert.Equal(150, StructurePolicy.Default.WindowBlockStartMinutesFromOpen);
        Assert.Equal(300, StructurePolicy.Default.WindowBlockEndMinutesFromOpen);
        Assert.Contains("\"ReboundMaxTrendAlignment\":0", json);
        Assert.Contains("\"WindowBlockStartMinutesFromOpen\":150", json);
        Assert.Contains("\"WindowBlockEndMinutesFromOpen\":300", json);
        Assert.DoesNotContain("ReboundMinInvalidationAtr", json);
        Assert.Equal("a072819da695ee03f9ead6f9e8885f3e29625a18f1bfe08c66785befed29a0fa", StructurePolicy.Default.PolicyHash);
    }

    [Fact]
    public void ClearingOrChangingBundleFieldsChangesTheHash()
    {
        var baseline = StructurePolicy.Default.PolicyHash;
        var cleared = StructurePolicy.Default with
        {
            ReboundMaxTrendAlignment = null,
            WindowBlockStartMinutesFromOpen = null,
            WindowBlockEndMinutesFromOpen = null
        };
        var invalidation = StructurePolicy.Default with { ReboundMinInvalidationAtr = 2.0 };
        var narrowerWindow = StructurePolicy.Default with { WindowBlockStartMinutesFromOpen = 60 };
        Assert.NotEqual(baseline, cleared.PolicyHash);
        Assert.NotEqual(baseline, invalidation.PolicyHash);
        Assert.NotEqual(baseline, narrowerWindow.PolicyHash);
        Assert.DoesNotContain("ReboundMaxTrendAlignment", cleared.CanonicalJson);
        Assert.Contains("\"ReboundMinInvalidationAtr\":2", invalidation.CanonicalJson);
    }

    [Fact]
    public void TrendAlignedReboundIsRejectedOnlyAtOrAboveThreshold()
    {
        var policy = StructurePolicy.Default with { ReboundMaxTrendAlignment = 0.0 };
        var noGate = StructurePolicy.Default with { ReboundMaxTrendAlignment = null };
        Assert.True(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, 0.1, policy));
        Assert.True(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, 0.0, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, -0.1, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, null, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Pullback, 0.5, policy));
        Assert.False(SetupDetector.RejectsReboundTrendAligned(SetupKind.Rebound, 0.5, noGate));
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
        var noWindow = StructurePolicy.Default with { WindowBlockStartMinutesFromOpen = null, WindowBlockEndMinutesFromOpen = null };
        Assert.False(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(89)));
        Assert.True(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(90)));
        Assert.True(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(299)));
        Assert.False(policy.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(300)));
        Assert.False(noWindow.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(120)));
        var startOnly = noWindow with { WindowBlockStartMinutesFromOpen = 90 };
        Assert.False(startOnly.IsInsideEntryBlockWindow(TimeSpan.FromMinutes(120)));
    }
}
