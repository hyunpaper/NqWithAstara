using Astra.Server.Domain.Confluence;
using Astra.Server.Domain.Structure;
using Xunit;

/// <summary>ConfluencePolicy 계약과 v5 진입 경로 미접촉 (C2·C3·C6, #167).</summary>
public sealed class ConfluencePolicyTests
{
    [Fact]
    public void PolicyHashIsDeterministicForTheSamePolicy()
    {
        var a = ConfluencePolicy.Default;
        var b = new ConfluencePolicy();
        var c = ConfluencePolicy.Default with { };
        Assert.Equal(a.PolicyHash, b.PolicyHash);
        Assert.Equal(a.PolicyHash, c.PolicyHash);
        Assert.Equal(64, a.PolicyHash.Length);
        Assert.All(a.PolicyHash, ch => Assert.True(char.IsAsciiDigit(ch) || (ch >= 'a' && ch <= 'f')));
    }

    [Fact]
    public void EveryPolicyValueChangeChangesTheHash()
    {
        var baseline = ConfluencePolicy.Default.PolicyHash;
        var variants = new[]
        {
            ConfluencePolicy.Default with { MacdFastPeriod = 13 },
            ConfluencePolicy.Default with { BollingerDeviations = 2.5 },
            ConfluencePolicy.Default with { OpeningRangeMinutes = 30 },
            ConfluencePolicy.Default with { BenchmarkSyncToleranceSeconds = 90 },
            ConfluencePolicy.Default with { OrderBookPollWindow = 5 },
            ConfluencePolicy.Default with { AtrChannelAtrFactor = 3 },
            ConfluencePolicy.Default with { IndicatorVariants = "ema:ema-seed" },
            ConfluencePolicy.Default with { CorrelationGroups = ["oscillator:RSI"] },
            ConfluencePolicy.Default with { Version = "confluence.2" },
            ConfluencePolicy.Default with { VolatilityBreakoutK = .6 },
            ConfluencePolicy.Default with { KeltnerAtrFactor = 2 },
            ConfluencePolicy.Default with { CandleEngulfingScore = .7 },
            ConfluencePolicy.Default with { LeeReadyMinimumTrades = 30 }
        };
        var hashes = variants.Select(x => x.PolicyHash).ToArray();
        Assert.DoesNotContain(baseline, hashes);
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    /// <summary>2군 편입으로 바뀐 confluence.1 기본 해시 (C3-2, #170).</summary>
    [Fact]
    public void DefaultPolicyHashIsPinnedToTheDesignValues()
    {
        Assert.Equal("9369046ba1f7312094ded40e41dd4b6f6acca9f2dd58d026b07eef52551f9cff",
            ConfluencePolicy.Default.PolicyHash);
    }

    [Fact]
    public void CanonicalJsonIsKeySortedAndFreeOfRuntimeContext()
    {
        var json = ConfluencePolicy.Default.CanonicalJson;
        var keys = System.Text.RegularExpressions.Regex.Matches(json, "[{,]\"(\\w+)\":")
            .Select(x => x.Groups[1].Value).ToArray();
        Assert.Equal(keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), keys);
        Assert.DoesNotContain("PolicyHash", keys);
        Assert.DoesNotContain("CanonicalJson", keys);
        Assert.DoesNotContain("2026", json);
    }

    [Fact]
    public void PolicyCarriesTheDesignTableValues()
    {
        var p = ConfluencePolicy.Default;
        Assert.Equal(12, p.MacdFastPeriod);
        Assert.Equal(26, p.MacdSlowPeriod);
        Assert.Equal(9, p.MacdSignalPeriod);
        Assert.Equal(14, p.RsiPeriod);
        Assert.Equal(20, p.BollingerPeriod);
        Assert.Equal(2.0, p.BollingerDeviations);
        Assert.Equal(14, p.AtrPeriod);
        Assert.Equal(14, p.AdxPeriod);
        Assert.Equal(14, p.StochasticFastKPeriod);
        Assert.Equal(3, p.StochasticSlowKPeriod);
        Assert.Equal(3, p.StochasticSlowDPeriod);
        Assert.Equal(20, p.DonchianPeriod);
        Assert.Equal(15, p.OpeningRangeMinutes);
        Assert.Equal(2.0, p.AtrChannelAtrFactor);
        Assert.Equal(60, p.BenchmarkSyncToleranceSeconds);
        Assert.Equal(3, p.OrderBookPollWindow);
        Assert.Equal(20, p.RelativeVolumeLookbackBars);
        Assert.Equal("ema:sma-seed;rsi:wilder;bb:population-sigma;vwap:hlc3-vw-sigma;adx:talib-seed",
            p.IndicatorVariants);
        Assert.Equal(.5, p.VolatilityBreakoutK);
        Assert.Equal(3, p.VolatilityBreakoutFilterDays);
        Assert.Equal(20, p.KeltnerEmaPeriod);
        Assert.Equal(20, p.KeltnerAtrPeriod);
        Assert.Equal(1.5, p.KeltnerAtrFactor);
        Assert.Equal(.8, p.SqueezeReleaseScore);
        Assert.Equal(9, p.MtaEmaFastPeriod);
        Assert.Equal(21, p.MtaEmaSlowPeriod);
        Assert.Equal(.6, p.MtaHigherTimeframeWarmupConfidence);
        Assert.Equal(5, p.CandlePriorLowLookbackBars);
        Assert.Equal(.3, p.CandleBodyAtrRatio);
        Assert.Equal(20, p.DailyRelativeVolumeLookbackSessions);
        Assert.Equal(15, p.LeeReadyWindowMinutes);
        Assert.Equal(20, p.LeeReadyMinimumTrades);
    }

    [Fact]
    public void CorrelationGroupsResolveByTechniqueName()
    {
        var p = ConfluencePolicy.Default;
        Assert.Equal("oscillator", p.CorrelationGroupOf(TechniqueNames.Rsi));
        Assert.Equal("volatilityBand", p.CorrelationGroupOf(TechniqueNames.BollingerPercentB));
        Assert.Equal("volatilityBand", p.CorrelationGroupOf(TechniqueNames.Squeeze));
        Assert.Equal("range", p.CorrelationGroupOf(TechniqueNames.OpeningRange));
        Assert.Equal("range", p.CorrelationGroupOf(TechniqueNames.VolatilityBreakout));
        Assert.Equal("trend", p.CorrelationGroupOf(TechniqueNames.AdxDmi));
        Assert.Equal("trend", p.CorrelationGroupOf(TechniqueNames.MultiTimeframeAlignment));
        Assert.Equal("volume", p.CorrelationGroupOf(TechniqueNames.RelativeVolume));
        Assert.Equal("volume", p.CorrelationGroupOf(TechniqueNames.RelativeVolumeDaily));
        Assert.Equal("flow", p.CorrelationGroupOf(TechniqueNames.OrderBookImbalance));
        Assert.Equal("flow", p.CorrelationGroupOf(TechniqueNames.LeeReadyDelta));
        Assert.Null(p.CorrelationGroupOf(TechniqueNames.Candle));
        Assert.Null(p.CorrelationGroupOf(TechniqueNames.Macd));
        Assert.Null(p.CorrelationGroupOf("UNKNOWN"));
    }

    /// <summary>컨플루언스와 분리된 StructurePolicy 직렬화 계보에는 기본 비활성 실험 필드도 포함된다.</summary>
    [Fact]
    public void StructurePolicyHashIsUntouchedByTheConfluenceLayer()
    {
        Assert.True(StructurePolicy.Default.RequirePullbackNearVwap);
        Assert.True(StructurePolicy.Default.RequireBreakoutNearVwap);
        Assert.True(StructurePolicy.Default.RequireMinimumReboundEntryQuality);
        Assert.True(StructurePolicy.Default.RequireBreakoutAboveVwap);
        Assert.True(StructurePolicy.Default.RequireMaximumReboundNetR);
        Assert.True(StructurePolicy.Default.EnableTwoRFeeBreakEvenStop);
        Assert.False(StructurePolicy.Default.RejectHighVolatilityRangeEntries);
        Assert.True(StructurePolicy.Default.CapStructuralTargetAtTwoR);
        Assert.True(StructurePolicy.Default.EnableHalfRFeeBreakEvenStopForPositiveBenchmark);
        Assert.Contains("\"RequirePullbackNearVwap\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"PullbackMaximumVwapDistanceAtr\":2.1", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"RequireBreakoutNearVwap\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"BreakoutMaximumVwapDistanceAtr\":5", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"RequireMinimumReboundEntryQuality\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"MinimumReboundEntryQuality\":50", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"RequireBreakoutAboveVwap\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"RequireMaximumReboundNetR\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"MaximumReboundNetR\":1.9", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"EnableTwoRFeeBreakEvenStop\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"RejectHighVolatilityRangeEntries\":false", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"CapStructuralTargetAtTwoR\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"EnableHalfRFeeBreakEvenStopForPositiveBenchmark\":true", StructurePolicy.Default.CanonicalJson);
        Assert.Contains("\"ExemptBreakoutFromPositiveBenchmarkHalfRStop\":true", StructurePolicy.Default.CanonicalJson);
        // #245 Cycle77 PULLBACK 롱 비활성을 기본값으로 올린 뒤의 정책 해시(v5-structure.2).
        Assert.Equal("5fcfe23c70195acd5082ee2a9373e7d831c045529288600f6298f6ce9a1d5788",
            StructurePolicy.Default.PolicyHash);
        Assert.NotEqual(StructurePolicy.Default.PolicyHash, ConfluencePolicy.Default.PolicyHash);
    }

    /// <summary>C1 1단계: 진입 판정 경로는 컨플루언스를 참조하지 않는다. 소스에 문자열 자체가 없다.</summary>
    [Fact]
    public void EntryPathSourcesNeverMentionConfluence()
    {
        var root = ServerRoot();
        string[] files =
        [
            Path.Combine(root, "Domain", "Structure", "SetupDetector.cs"),
            Path.Combine(root, "Domain", "Structure", "StructuralPlanner.cs"),
            Path.Combine(root, "Domain", "Structure", "StructuralLifecycle.cs"),
            Path.Combine(root, "Domain", "StructuralSimulation.cs")
        ];
        foreach (var file in files)
        {
            Assert.True(File.Exists(file), file);
            Assert.DoesNotContain("Confluence", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    static string ServerRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Astra.Server.csproj"))) return directory.FullName;
            if (File.Exists(Path.Combine(directory.FullName, "server", "Astra.Server.csproj"))) return Path.Combine(directory.FullName, "server");
        }
        Assert.Fail("Astra.Server.csproj 기준의 서버 루트를 찾지 못했다.");
        return null!;
    }
}


