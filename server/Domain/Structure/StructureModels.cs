using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Astra.Server;

namespace Astra.Server.Domain.Structure;

// v5 구조 엔진 D1 공통 계약. 설계 §11 DTO 의미를 유지하며 단위 규칙은 §16A를 따른다.
// 가격·경계는 decimal, 지표·정규화 값은 double. 미지 값은 null + 사유이며 0으로 대체하지 않는다.

public enum BarTimeframe { OneMinute, FiveMinute }

public enum PivotKind { High, Low }

/// <summary>설계 §6.4 / §16B 역할. BROKEN은 원래 역할의 붕괴, UNRESOLVED는 역할 미확정 또는 진행 중인 flip 재검증이다.</summary>
public enum ZoneRole { Unresolved, Support, Resistance, FlippedSupport, FlippedResistance, Broken }

/// <summary>설계 §6.5 independent source families. profile은 하나로만 센다.</summary>
public enum ZoneSourceFamily { Pivot, ContextLevel, Profile }

/// <summary>설계 §16A 마지막 문단 / §16B. pending은 완료 episode 수에 넣지 않는다.</summary>
public enum EpisodeOutcome { Pending, Success, Failure, Neutral }

/// <summary>설계 §16B DataQuality source status.</summary>
public enum SourceStatus { Available, Missing, Stale, Approximate }

/// <summary>구조 엔진 전용 봉. Start=봉 시작, End=봉 종료(exclusive). 가격 decimal, 거래량 double(§16A).</summary>
public sealed record StructureBar(DateTimeOffset Start, DateTimeOffset End, decimal Open, decimal High, decimal Low, decimal Close, double Volume)
{
    public TimeSpan Duration => End - Start;

    /// <summary>봉의 [Low,High]가 [lower,upper]와 겹치는지. 경계 접촉은 겹침으로 본다(§6.4 Touch).</summary>
    public bool Touches(decimal lower, decimal upper) => Low <= upper && High >= lower;
}

/// <summary>완료 일봉. 거래일은 New York 거래일이며 UTC/KST 날짜로 나누지 않는다(§5.1).</summary>
public sealed record StructureDailyBar(DateOnly TradingDate, decimal Open, decimal High, decimal Low, decimal Close, double Volume);

/// <summary>확정 피벗(§5.3). ConfirmedAt은 우측 두 번째 봉의 종료 시각이다(§16B).</summary>
public sealed record ConfirmedPivot(string SourceId, PivotKind Kind, BarTimeframe Timeframe, decimal Price,
    DateTimeOffset OccurredAt, DateTimeOffset ConfirmedAt);

/// <summary>Zone 원천(§16B). Id는 canonical 문자열의 SHA-256이다. Temporary=true는 profile 임시 ID로 영구 대표가 될 수 없다.</summary>
public sealed record ZoneSource(string Id, ZoneSourceFamily Family, string Kind, decimal Price,
    DateTimeOffset OccurredAt, DateTimeOffset ConfirmedAt, bool Temporary, int? AgeSessions = null);

/// <summary>같은 움직임에서 나온 증거 묶음(§6.4). 시간 구간과 family를 공유하는 원천은 한 group이다.</summary>
public sealed record EvidenceGroup(string Key, ZoneSourceFamily Family, DateTimeOffset From, DateTimeOffset To,
    ImmutableArray<string> SourceIds);

/// <summary>Zone 역할 전이 기록(§16B). 화면에서 원인을 설명하기 위해 보존한다.</summary>
public sealed record ZoneRoleChange(DateTimeOffset At, ZoneRole From, ZoneRole To, string Reason);

/// <summary>구간 접촉 episode(§6.4, §16B). 형성 봉은 제외되고 pending은 완료 개수에 넣지 않는다.</summary>
public sealed record TouchEpisode(string ZoneId, DateTimeOffset StartAt, DateTimeOffset? ResolvedAt,
    EpisodeOutcome Outcome, ZoneRole RoleAtTouch, double? AtrAtTouch, double? FavorableExcursionAtr,
    decimal FavorableExtreme, ImmutableArray<string> Notes)
{
    /// <summary>결정성 비교·관측 중복 방지용 canonical 표현. record 자동 Equals는 배열 참조를 비교하므로 이 값을 쓴다.</summary>
    public string Fingerprint() => string.Join('|', ZoneId, StructureMath.Iso(StartAt),
        ResolvedAt is null ? "null" : StructureMath.Iso(ResolvedAt.Value), Outcome.ToString(), RoleAtTouch.ToString(),
        StructureMath.Number(AtrAtTouch), StructureMath.Number(FavorableExcursionAtr),
        StructureMath.Price(FavorableExtreme), string.Join(',', Notes));
}

/// <summary>구간 강도 구성요소(§6.5). 모두 0~1이며 확률이 아니다. 결측 요소는 기하평균에서 생략하고 목록으로 남긴다.</summary>
public sealed record ZoneStrength(double? TouchEvidence, double? ReactionEvidence, double? Recency, double? Confluence,
    double BreachPenalty, double? Value, int CompletedEpisodes, int SuccessEpisodes, int FailedEpisodes,
    int PendingEpisodes, int IndependentFamilies, int IndependentNonProfileFamilies,
    ImmutableArray<string> UsedComponents, ImmutableArray<string> MissingComponents)
{
    public string Fingerprint() => string.Join('|', StructureMath.Number(TouchEvidence),
        StructureMath.Number(ReactionEvidence), StructureMath.Number(Recency), StructureMath.Number(Confluence),
        StructureMath.Number(BreachPenalty), StructureMath.Number(Value), CompletedEpisodes, SuccessEpisodes,
        FailedEpisodes, PendingEpisodes, IndependentFamilies, IndependentNonProfileFamilies,
        string.Join(',', UsedComponents), string.Join(',', MissingComponents));
}

/// <summary>지지·저항 구간(§11 PriceZone). 경계는 decimal, ID는 원천 기반이며 평가마다 새로 발급하지 않는다(§6.3).</summary>
public sealed record PriceZone(string Id, int BoundsRevision, int SnapshotRevision, decimal Lower, decimal Upper,
    ZoneRole Role, ZoneRole OriginalRole, DateTimeOffset FirstConfirmedAt, DateTimeOffset LastConfirmedAt,
    ImmutableArray<ZoneSource> Sources, ImmutableArray<EvidenceGroup> EvidenceGroups, ImmutableArray<string> Aliases,
    ZoneStrength? Strength, bool Eligible, ImmutableArray<string> RejectReasons,
    ImmutableArray<string> ApproximationFlags, ImmutableArray<ZoneRoleChange> RoleHistory,
    bool ProfileOnly, bool Retired)
{
    public decimal Width => Upper - Lower;
    public ImmutableArray<string> SourceIds => Sources.Select(x => x.Id).ToImmutableArray();
    public bool Overlaps(decimal low, decimal high) => low <= Upper && high >= Lower;

    /// <summary>
    /// 공개 snapshot의 canonical 표현. record 자동 Equals는 ImmutableArray 멤버를 참조로 비교하므로
    /// 결정성 검증·관측 중복 방지·revision 판단에는 이 값을 사용한다.
    /// </summary>
    public string Fingerprint() => string.Join('|', Id, BoundsRevision, SnapshotRevision,
        StructureMath.Price(Lower), StructureMath.Price(Upper), Role.ToString(), OriginalRole.ToString(),
        StructureMath.Iso(FirstConfirmedAt), StructureMath.Iso(LastConfirmedAt),
        string.Join(',', Sources.Select(x => x.Id)), string.Join(',', EvidenceGroups.Select(x => x.Key)),
        string.Join(',', Aliases), Strength?.Fingerprint() ?? "null", Eligible ? "1" : "0",
        string.Join(',', RejectReasons), string.Join(',', ApproximationFlags),
        string.Join(',', RoleHistory.Select(x => $"{StructureMath.Iso(x.At)}:{x.From}>{x.To}:{x.Reason}")),
        ProfileOnly ? "1" : "0", Retired ? "1" : "0");
}

public sealed record VolumeProfileBin(int Index, decimal Lower, decimal Upper, double Volume);

/// <summary>연접 국소 최대 bin을 하나로 묶은 거래량 집중 구간(§6.2).</summary>
public sealed record ProfileNode(int StartIndex, int EndIndex, decimal Lower, decimal Upper, double Volume, bool IsPoc);

/// <summary>봉 기반 추정 거래량 분포(§6.1: 실제 가격별 체결량·기관 원가가 아니다).</summary>
public sealed record VolumeProfile(decimal BinWidth, int BinWidthMultiple, ImmutableArray<VolumeProfileBin> Bins,
    ImmutableArray<ProfileNode> Nodes, int? PocIndex, double InputVolume, double AllocatedVolume,
    bool Coarsened, ImmutableArray<string> Warnings)
{
    public static VolumeProfile Empty(decimal binWidth, params string[] warnings) => new(binWidth, 1,
        ImmutableArray<VolumeProfileBin>.Empty, ImmutableArray<ProfileNode>.Empty, null, 0, 0, false,
        warnings.ToImmutableArray());
}

/// <summary>원천별 데이터 품질(§16B). expectedCount를 정의할 수 없으면 CoverageRatio=null이다.</summary>
public sealed record DataSourceQuality(string Source, SourceStatus Status, int Count, int? ExpectedCount,
    DateTimeOffset? First, DateTimeOffset? Last, ImmutableArray<string> Gaps, ImmutableArray<string> Conflicts,
    double? CoverageRatio, ImmutableArray<string> Warnings);

/// <summary>품질을 승률이라고 부르지 않는다(§3). 차단 사유는 대상별로 분리한다(§16B).</summary>
public sealed record DataQuality(ImmutableArray<DataSourceQuality> Sources, ImmutableArray<string> Warnings,
    ImmutableArray<string> BlockersForTrend, ImmutableArray<string> BlockersForZone,
    ImmutableArray<string> BlockersForCandidate, ImmutableArray<string> BlockersForReady)
{
    public static readonly DataQuality Empty = new(ImmutableArray<DataSourceQuality>.Empty,
        ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
}

/// <summary>
/// 선택적 호가 컨텍스트(§9.3, §16B). 없으면 비용 불확실성을 노출하고 0으로 가정한다.
/// 잔량은 §16B "양쪽 양수 가격/잔량" 검사 대상이다. 결측·비양수면 검증되지 않은 spread이므로 비용으로 쓰지 않는다.
/// </summary>
public sealed record StructureLiquidity(decimal? BestBid, decimal? BestAsk, DateTimeOffset? At,
    double? BidSize = null, double? AskSize = null);

/// <summary>선택적 틱 룰 체결 흐름. 표본 수 없이 50%로 만들지 않는다(§7).</summary>
public sealed record StructureFlow(int BuyTicks, int SellTicks, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>한 평가에 쓰는 불변 입력(§5.1). Domain은 현재 시각을 다시 읽지 않는다.</summary>
public sealed record StructureSnapshot(string Symbol, DateTimeOffset SessionStart, DateTimeOffset SessionEnd,
    DateTimeOffset AnalysisAsOf, decimal? QuotePrice, DateTimeOffset? QuoteAt,
    ImmutableArray<Candle> Completed1mBars, ImmutableArray<Candle> CompletedDailyBars,
    StructureFlow? OptionalFlow, StructureLiquidity? OptionalLiquidity, long Generation);

/// <summary>정규화된 1분봉과 그 과정에서 발견한 품질 사실(§5.1).</summary>
public sealed record NormalizedBars(ImmutableArray<StructureBar> Bars, ImmutableArray<string> Gaps,
    ImmutableArray<string> Conflicts, ImmutableArray<string> Warnings, int DroppedInvalid, int DroppedDuplicate,
    int DroppedIncomplete, int DroppedOutOfSession)
{
    public static readonly NormalizedBars Empty = new(ImmutableArray<StructureBar>.Empty,
        ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, 0, 0, 0, 0);
}

/// <summary>설계 §16A 수치 규칙. 가격은 decimal 경로를 벗어나지 않고 정규화 함수만 double을 쓴다.</summary>
public static class StructureMath
{
    /// <summary>§16A floorToCent. double 왕복 없이 decimal로만 계산한다.</summary>
    public static decimal FloorToCent(decimal x) => decimal.Floor(x * 100m) / 100m;

    /// <summary>§16A geometricMean. 음수/비유한 입력은 거절, 0이 하나라도 있으면 0, 그 외 exp(mean(log x)).</summary>
    public static double GeometricMean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("geometricMean requires at least one component.", nameof(values));
        foreach (var v in values)
        {
            if (!double.IsFinite(v) || v < 0)
                throw new ArgumentOutOfRangeException(nameof(values), "geometricMean rejects negative or non-finite components.");
            if (v == 0) return 0;
        }
        double sum = 0;
        foreach (var v in values) sum += Math.Log(v);
        return Math.Exp(sum / values.Count);
    }

    /// <summary>결측(null)은 기하평균에서 생략한다. 사용 가능한 요소가 없으면 null이며 0으로 대체하지 않는다.</summary>
    public static double? GeometricMeanOfAvailable(IEnumerable<double?> values)
    {
        var present = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return present.Length == 0 ? null : GeometricMean(present);
    }

    /// <summary>double 지표를 decimal 가격 폭으로 옮길 때의 유일한 경로. 비유한/음수는 null이다(§16A).</summary>
    public static decimal? ToPriceDelta(double? indicatorValue)
    {
        if (indicatorValue is null || !double.IsFinite(indicatorValue.Value) || indicatorValue.Value < 0) return null;
        return (decimal)indicatorValue.Value;
    }

    /// <summary>max(floor, factor*indicator). indicator가 없으면 floor를 쓰고 호출자가 근사 플래그를 남긴다.</summary>
    public static decimal ScaledFloor(decimal floor, double factor, double? indicatorValue)
    {
        var scaled = ToPriceDelta(indicatorValue is null ? null : indicatorValue.Value * factor);
        return scaled is null || scaled.Value < floor ? floor : scaled.Value;
    }

    public static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public static string Price(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>지표·정규화 값의 canonical 표현. NaN/Infinity는 직렬화하지 않고 결측으로 표시한다(§11).</summary>
    public static string Number(double? value) =>
        value is null || !double.IsFinite(value.Value) ? "null" : value.Value.ToString("R", CultureInfo.InvariantCulture);

    public static string Sha256Hex(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>§16B 원천 ID: canonical 문자열의 SHA-256. 가격 소수점 문자열만으로 ID를 만들지 않는다(§6.3).</summary>
    public static string SourceId(params string[] parts) => Sha256Hex(string.Join('|', parts));
}
