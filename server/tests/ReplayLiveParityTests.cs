using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

public sealed class ReplayLiveParityTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-25T13:30:00Z");

    [Fact]
    public void 동일한_봉과_결과는_패리티를_충족한다()
    {
        var report = ReplayLiveParity.Compare(Snapshot(EnginePathKind.Replay), Snapshot(EnginePathKind.Live));

        Assert.True(report.Matches);
        Assert.Empty(report.Mismatches);
        Assert.Empty(report.Causes);
        Assert.Equal("replay-live-parity.1", report.Version);
    }

    [Fact]
    public void 후보_진입_계획_체결가_차이를_필드별로_보고한다()
    {
        var replay = Snapshot(EnginePathKind.Replay);
        var live = Snapshot(EnginePathKind.Live) with
        {
            Candidate = new(false, null, "REJECTED"),
            Execution = new(false, 98, 111, 100.5, "OBSERVED_ORDERBOOK", .03, .02)
        };

        var report = ReplayLiveParity.Compare(replay, live);

        Assert.False(report.Matches);
        Assert.Contains(report.Mismatches, x => x.Field == "candidate.present");
        Assert.Contains(report.Mismatches, x => x.Field == "entry.entered");
        Assert.Contains(report.Mismatches, x => x.Field == "plan.stop");
        Assert.Contains(report.Mismatches, x => x.Field == "plan.target");
        Assert.Contains(report.Mismatches, x => x.Field == "execution.fill");
        Assert.Contains(ReplayLiveParityCause.Cost, report.Causes);
    }

    [Fact]
    public void 미래봉_입력은_look_ahead로_분류한다()
    {
        var replay = Snapshot(EnginePathKind.Replay) with
        {
            Bar = Bar() with { LastInputBarStart = Start.AddMinutes(1) },
            Candidate = new(false, null, "REJECTED")
        };

        var report = ReplayLiveParity.Compare(replay, Snapshot(EnginePathKind.Live));

        Assert.Contains(report.Mismatches, x => x.Field == "replay.lookAhead" &&
            x.Cause == ReplayLiveParityCause.LookAhead);
        Assert.Equal(ReplayLiveParityCause.LookAhead,
            report.Mismatches.Single(x => x.Field == "candidate.present").Cause);
    }

    [Fact]
    public void 봉_마감전_판정은_별도_원인으로_분류한다()
    {
        var live = Snapshot(EnginePathKind.Live) with
        {
            Bar = Bar() with { EvaluatedAt = Start.AddSeconds(59) },
            Execution = Execution() with { Entered = false }
        };

        var report = ReplayLiveParity.Compare(Snapshot(EnginePathKind.Replay), live);

        Assert.Contains(report.Mismatches, x => x.Field == "live.barClosed" &&
            x.Cause == ReplayLiveParityCause.BarNotClosed);
        Assert.Equal(ReplayLiveParityCause.BarNotClosed,
            report.Mismatches.Single(x => x.Field == "entry.entered").Cause);
    }

    [Fact]
    public void 동일한_현지시각의_다른_offset은_timezone_차이로_보고한다()
    {
        var liveStart = new DateTimeOffset(2026, 9, 25, 13, 30, 0, TimeSpan.FromHours(-4));
        var live = Snapshot(EnginePathKind.Live) with
        {
            Bar = Bar() with
            {
                BarStart = liveStart,
                LastInputBarStart = liveStart,
                EvaluatedAt = liveStart.AddMinutes(1),
                TimeZoneId = "America/New_York"
            }
        };

        var report = ReplayLiveParity.Compare(Snapshot(EnginePathKind.Replay), live);

        Assert.Contains(report.Mismatches, x => x.Field == "bar.start" &&
            x.Cause == ReplayLiveParityCause.TimeZone);
        Assert.Contains(report.Mismatches, x => x.Field == "bar.timeZone" &&
            x.Cause == ReplayLiveParityCause.TimeZone);
    }

    [Fact]
    public void 비용만_다르면_비용_원인으로_격리한다()
    {
        var live = Snapshot(EnginePathKind.Live) with
        {
            Execution = Execution() with { CostSource = "OBSERVED_ORDERBOOK", SpreadCost = .03 }
        };

        var report = ReplayLiveParity.Compare(Snapshot(EnginePathKind.Replay), live);

        Assert.Equal(ReplayLiveParityCause.Cost, Assert.Single(report.Causes));
        Assert.All(report.Mismatches, x => Assert.Equal(ReplayLiveParityCause.Cost, x.Cause));
    }

    [Fact]
    public void 경로가_뒤바뀐_입력은_거부한다()
    {
        Assert.Throws<ArgumentException>(() => ReplayLiveParity.Compare(
            Snapshot(EnginePathKind.Live), Snapshot(EnginePathKind.Replay)));
    }

    static EngineParitySnapshot Snapshot(EnginePathKind path) => new(path, Bar(),
        new(true, "event-1", "READY"), Execution());

    static EngineBarContract Bar() => new("TSLA", Start, TimeSpan.FromMinutes(1), Start,
        Start.AddMinutes(1), "UTC");

    static EngineExecutionContract Execution() => new(true, 99, 110, 101,
        "MODELED_V2", .02, .01);
}
