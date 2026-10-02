using System.Collections.Immutable;
using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

/// <summary>경제일정 PIT·consensus 빈티지가 없어 항상 unavailable을 내는 빈 원천. 결측을 coverage에 남긴다(§3.4, #309).</summary>
public sealed class MacroCalendarEvidenceSource : IScoreEvidenceSource
{
    public const string SourceName = "macro_calendar";
    public const string UnavailableReason = "no_point_in_time_vintage";
    public string Name => SourceName;

    public Task<ScoreEvidenceBatch> ReadAsync(ScoreEvidenceRequest request, CancellationToken ct)
        => Task.FromResult(new ScoreEvidenceBatch(SourceName, request.AsOf, false,
            ImmutableArray<EventEvidence>.Empty, "unavailable", UnavailableReason));
}
