using Astra.Server.Domain.ScoreCore;

namespace Astra.Server.Application.ScoreCore;

/// <summary>후보 생성 시점 asOf에 쓸 수 있던 Score Core snapshot 참조를 돌려주는 포트(§8 1단계, #314).</summary>
public interface IEntryScoreCoreAttachment
{
    Task<EntryScoreCoreTags> ResolveAsync(string symbol, DateTimeOffset asOf, CancellationToken ct);
}

/// <summary>저장된 shadow snapshot 중 asOf 이전 최신 건의 captureId·상태·버전만 읽는다. 비활성이면 저장소를 열지 않는다(#314).</summary>
public sealed class EntryScoreCoreAttachment(ScoreCoreOptions options, IScoreCoreSnapshotStore store,
    ScoreCorePolicy? policy = null) : IEntryScoreCoreAttachment
{
    public const string StatusDisabled = "disabled";
    public const string StatusUnavailable = "unavailable";
    public const string StatusNotFound = "not_found";
    public const string ReasonHistoricalReplay = "historical_point_in_time_unavailable";
    public const string ReasonSnapshotAfterAsOf = "snapshot_after_as_of";
    public const string ReasonNoSnapshot = "no_snapshot_for_target";

    readonly string _policyVersion = (policy ?? ScoreCorePolicy.ShadowV1).Version;

    public static EntryScoreCoreTags Disabled(ScoreCorePolicy? policy = null)
        => new(StatusDisabled, null, (policy ?? ScoreCorePolicy.ShadowV1).Version, null, null);

    public static EntryScoreCoreTags HistoricalReplay(ScoreCorePolicy? policy = null)
        => new(StatusUnavailable, null, (policy ?? ScoreCorePolicy.ShadowV1).Version, null, null,
            ReasonHistoricalReplay);

    public async Task<EntryScoreCoreTags> ResolveAsync(string symbol, DateTimeOffset asOf, CancellationToken ct)
    {
        if (!options.Enabled) return new(StatusDisabled, null, _policyVersion, null, null);
        var target = (symbol ?? "").Trim().ToUpperInvariant();
        if (target.Length == 0) return new(StatusNotFound, null, _policyVersion, null, null, ReasonNoSnapshot);
        try
        {
            var snapshot = await store.FindLatestAsync(ImpactTargetKind.Company, target, ct);
            if (snapshot is null) return new(StatusNotFound, null, _policyVersion, null, null, ReasonNoSnapshot);
            if (snapshot.Score.AsOf > asOf)
                return new(StatusNotFound, null, _policyVersion, null, null, ReasonSnapshotAfterAsOf);
            return new(snapshot.Status, snapshot.CaptureId, snapshot.Score.PolicyVersion,
                snapshot.Score.ScoreSchemaVersion, snapshot.Score.AsOf);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new(StatusUnavailable, null, _policyVersion, null, null, exception.GetType().Name);
        }
    }
}
