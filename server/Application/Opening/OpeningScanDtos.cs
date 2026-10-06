using Astra.Server.Domain.Opening;

namespace Astra.Server.Application.Opening;

public sealed record OpeningFirst5Dto(int BarsSeen, int UpBars, int NewHighs);

public sealed record OpeningPremarketDto(double High, double Volume, bool AbovePremarketHigh);

public sealed record OpeningScanRowDto(string Symbol, string Name, string Grade, double Score, string VolumeStatus,
    double? RvolNow, OpeningRvolWindow Rvol3, OpeningRvolWindow Rvol5, OpeningRvolWindow Rvol20, int SampleCount,
    double? ChangeFromOpenPercent, double? ChangeFromPrevClosePercent,
    double? GapPercent, string? PrevCloseSource, bool? AboveVwap, OpeningFirst5Dto? First5, bool? BrokeOpeningRange,
    OpeningPremarketDto? Premarket, string QuoteStatus, string[] Reasons, DateTimeOffset ObservedAt, int ElapsedMinutes);

public sealed record OpeningFollowupRowDto(string Symbol, string Name, string GradeAt5, double? ReturnPercent30,
    double? MfePercent30, double? MaePercent30, double? Rvol5);

public sealed record OpeningCloseRowDto(string Symbol, string Name, string GradeAt5, double? ReturnPercentClose);

public sealed record OpeningScanSummaryDto(IReadOnlyList<OpeningScanRowDto> At5, IReadOnlyList<OpeningScanRowDto> At30,
    IReadOnlyList<OpeningFollowupRowDto> Followup30, IReadOnlyList<OpeningCloseRowDto> Close);

public sealed record OpeningScanResponse(string? SessionDate, string Phase, DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd, DateTimeOffset AsOf, int ElapsedMinutes, string PolicyVersion, int LookbackSessions,
    int MinimumSessions, int RefreshSeconds, IReadOnlyList<OpeningScanRowDto> Rows, OpeningScanSummaryDto? Summary,
    string[] Warnings);

public sealed record OpeningSnapshotRecord(string ObservationId, string Kind, string RecordVersion, string PolicyVersion,
    string PolicyHash, string Symbol, string Name, DateOnly SessionDate, DateTimeOffset SessionOpen, DateTimeOffset ObservedAt,
    int ElapsedMinutes, DateTimeOffset LastBarStart, double LastBarClose, double QuotePrice, DateTimeOffset QuoteAt,
    string QuoteStatus, double CumulativeVolume, double? RvolNow, OpeningRvolWindow Rvol3, OpeningRvolWindow Rvol5,
    OpeningRvolWindow Rvol20, double? BaselineMean, double? BaselineMedian,
    int SampleCount, string VolumeStatus, double? PrevClose, string? PrevCloseSource, double? Open, bool OpenBarMissing,
    double? GapPercent, double? ChangeFromPrevClosePercent, double? ChangeFromOpenPercent, double? Vwap, bool? AboveVwap,
    OpeningFirst5Dto? First5, double? OpeningRangeHigh5, bool? BrokeOpeningRange, OpeningPremarketDto? Premarket,
    string Grade, double Score, string[] Reasons, string[] Warnings);

public sealed record OpeningFollowup30Record(string ObservationId, string Kind, string RecordVersion, string Symbol,
    string Name, DateOnly SessionDate, int AnchorMinute, double AnchorBarClose, double? BarCloseAt35, double? ReturnPercent30,
    double? MfePercent30, double? MaePercent30, string GradeAt5, double ScoreAt5, double? Rvol5);

public sealed record OpeningCloseRecord(string ObservationId, string Kind, string RecordVersion, string Symbol, string Name,
    DateOnly SessionDate, double? AnchorBarClose, double? LastQuote, DateTimeOffset? LastQuoteAt, double? ReturnPercentClose,
    string GradeAt5, string[] Warnings);

public static class OpeningScanDtoMapper
{
    public static OpeningScanRowDto Row(OpeningScanSnapshot s) => new(
        s.Symbol, s.Name, s.Grade, s.Score, s.VolumeStatus, s.RvolNow, s.Rvol3, s.Rvol5, s.Rvol20, s.SampleCount,
        s.ChangeFromOpenPercent, s.ChangeFromPrevClosePercent, s.GapPercent, s.PrevCloseSource, s.AboveVwap,
        s.First5 is null ? null : new OpeningFirst5Dto(s.First5.BarsSeen, s.First5.UpBars, s.First5.NewHighs),
        s.BrokeOpeningRange,
        s.Premarket is null ? null : new OpeningPremarketDto(s.Premarket.High, s.Premarket.Volume, s.Premarket.AbovePremarketHigh),
        s.QuoteStatus, s.Reasons, s.ObservedAt, s.ElapsedMinutes);

    /// <summary>등급(STRONG>VOLUME_ONLY>PRICE_ONLY>WEAK) → score 내림차순 정렬 키.</summary>
    public static int GradeRank(string grade) => grade switch
    {
        OpeningSnapshotEvaluator.GradeStrong => 0,
        OpeningSnapshotEvaluator.GradeVolumeOnly => 1,
        OpeningSnapshotEvaluator.GradePriceOnly => 2,
        _ => 3,
    };
}
