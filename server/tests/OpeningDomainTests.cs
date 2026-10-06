using System.Collections.Immutable;
using System.Text.Json;
using Astra.Server;
using Astra.Server.Application;
using Astra.Server.Domain;
using Astra.Server.Domain.Indicators;
using Astra.Server.Domain.Opening;
using Xunit;

public sealed class OpeningScanPolicyTests
{
    [Fact]
    public void DefaultHashIsStableAndSixtyFourHex()
    {
        var hash = OpeningScanPolicy.Default.PolicyHash;
        Assert.Equal(64, hash.Length);
        Assert.All(hash, ch => Assert.True(char.IsAsciiDigit(ch) || (ch >= 'a' && ch <= 'f')));
        Assert.Equal(OpeningScanPolicy.Default.PolicyHash, new OpeningScanPolicy().PolicyHash);
    }

    [Fact]
    public void ThresholdChangeChangesHashButEnabledDoesNot()
    {
        Assert.NotEqual(OpeningScanPolicy.Default.PolicyHash, (OpeningScanPolicy.Default with { VolumeStrongRatio = 1.5 }).PolicyHash);
        Assert.NotEqual(OpeningScanPolicy.Default.PolicyHash, (OpeningScanPolicy.Default with { PriceUpPercent = 0.6 }).PolicyHash);
        Assert.Equal(OpeningScanPolicy.Default.PolicyHash, (OpeningScanPolicy.Default with { Enabled = false }).PolicyHash);
    }

    [Fact]
    public void CanonicalJsonCarriesThresholds()
    {
        var json = OpeningScanPolicy.Default.CanonicalJson;
        Assert.Contains("\"VolumeStrongRatio\":2", json);
        Assert.Contains("\"PriceUpPercent\":0.5", json);
        Assert.DoesNotContain("Enabled", json);
    }
}

public sealed class OpeningRvolTests
{
    static SessionVolumeProfile Profile(params decimal[] cumulative) =>
        new(new DateOnly(2026, 1, 1), [.. cumulative]);

    static IReadOnlyList<SessionVolumeProfile> Flat(int count, decimal at5) =>
        Enumerable.Range(0, count).Select(_ => Profile(10, 20, 30, 40, at5)).ToArray();

    [Fact]
    public void BelowMinimumSessionsReturnsNull()
    {
        Assert.Null(OpeningRvol.Compute(100, 5, Flat(4, 50), minimumSessions: 5));
    }

    [Fact]
    public void PartialSamplesComputeRatioAndCount()
    {
        var result = OpeningRvol.Compute(100, 5, Flat(12, 50), minimumSessions: 5);
        Assert.NotNull(result);
        Assert.Equal(12, result!.SampleCount);
        Assert.Equal(2.0, result.Ratio, 3);
        Assert.Equal(50, result.BaselineMean, 3);
        Assert.Equal(50, result.BaselineMedian, 3);
    }

    [Fact]
    public void ElapsedOutsideRangeIsNull()
    {
        Assert.Null(OpeningRvol.Compute(100, 0, Flat(20, 50)));
        Assert.Null(OpeningRvol.Compute(100, 31, Flat(20, 50)));
    }

    [Fact]
    public void ZeroMeanReturnsNull()
    {
        Assert.Null(OpeningRvol.Compute(100, 5, Enumerable.Range(0, 20).Select(_ => Profile(0, 0, 0, 0, 0)).ToArray()));
    }

    [Fact]
    public void MedianUsesMiddleOfSortedSamples()
    {
        var profiles = new[] { Profile(0, 0, 0, 0, 10), Profile(0, 0, 0, 0, 30), Profile(0, 0, 0, 0, 20),
            Profile(0, 0, 0, 0, 40), Profile(0, 0, 0, 0, 50) };
        var result = OpeningRvol.Compute(100, 5, profiles, minimumSessions: 5)!;
        Assert.Equal(30, result.BaselineMedian, 3);
    }
}

public sealed class OpeningSessionProfileTests
{
    static DateTimeOffset Open(DateOnly date) =>
        new(new DateTime(date.Year, date.Month, date.Day, 9, 30, 0),
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York").GetUtcOffset(new DateTime(date.Year, date.Month, date.Day, 9, 30, 0)));

    static StoredBarSession Legacy(DateOnly date, int minutes, decimal openBarVolume)
    {
        var baseLabel = Open(date);
        var builder = ImmutableArray.CreateBuilder<IndicatorBar>();
        builder.Add(Bar(baseLabel, 100, 2));
        for (var i = 0; i < minutes; i++)
            builder.Add(Bar(baseLabel.AddMinutes(i + 1), 100, i == 0 ? openBarVolume : 10));
        return new StoredBarSession(builder.ToImmutable(), BarTimeConvention.Legacy, null);
    }

    static IndicatorBar Bar(DateTimeOffset label, decimal price, decimal volume) =>
        new(label, label.AddMinutes(1), price, price, price, price, volume);

    [Fact]
    public void LegacyShiftsLabelBackOneMinuteAndDropsPremarket()
    {
        var date = new DateOnly(2026, 10, 2);
        var session = Legacy(date, 29, 77);
        var profile = OpeningSessionProfile.FromStoredSession(date, Open(date), session.Bars, session.Convention, session.Rejection, out var status);
        Assert.Equal(OpeningSessionProfile.StatusLegacyShifted, status);
        Assert.NotNull(profile);
        Assert.Equal(77, profile!.At(1));
    }

    [Fact]
    public void BarStartConventionIsNotShifted()
    {
        var date = new DateOnly(2026, 10, 2);
        var open = Open(date);
        var builder = ImmutableArray.CreateBuilder<IndicatorBar>();
        for (var i = 0; i < 29; i++) builder.Add(Bar(open.AddMinutes(i), 100, i == 0 ? 55 : 10));
        var profile = OpeningSessionProfile.FromStoredSession(date, open, builder.ToImmutable(), BarTimeConvention.BarStart, null, out var status);
        Assert.Equal(OpeningSessionProfile.StatusOk, status);
        Assert.Equal(55, profile!.At(1));
    }

    [Fact]
    public void MixedConventionIsRejected()
    {
        var date = new DateOnly(2026, 10, 2);
        var profile = OpeningSessionProfile.FromStoredSession(date, Open(date), ImmutableArray<IndicatorBar>.Empty, "mixed", "BAR_TIME_CONVENTION_MIXED: ...", out var status);
        Assert.Null(profile);
        Assert.Equal(OpeningSessionProfile.StatusRejected, status);
    }

    [Fact]
    public void FewerThanTwentyFiveMinutesIsIncomplete()
    {
        var date = new DateOnly(2026, 10, 2);
        var open = Open(date);
        var builder = ImmutableArray.CreateBuilder<IndicatorBar>();
        for (var i = 0; i < 10; i++) builder.Add(Bar(open.AddMinutes(i), 100, 10));
        var profile = OpeningSessionProfile.FromStoredSession(date, open, builder.ToImmutable(), BarTimeConvention.BarStart, null, out var status);
        Assert.Null(profile);
        Assert.Equal(OpeningSessionProfile.StatusIncomplete, status);
    }

    [Fact]
    public void MissingOpenBarIsRejected()
    {
        var date = new DateOnly(2026, 10, 2);
        var open = Open(date);
        var builder = ImmutableArray.CreateBuilder<IndicatorBar>();
        for (var i = 5; i < 30; i++) builder.Add(Bar(open.AddMinutes(i), 100, 10));
        var profile = OpeningSessionProfile.FromStoredSession(date, open, builder.ToImmutable(), BarTimeConvention.BarStart, null, out var status);
        Assert.Null(profile);
        Assert.Equal(OpeningSessionProfile.StatusNoOpen, status);
    }
}
