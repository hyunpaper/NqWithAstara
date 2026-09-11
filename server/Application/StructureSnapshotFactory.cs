using System.Collections.Immutable;
using Astra.Server.Domain.Structure;

namespace Astra.Server.Application;

// v5 구조 엔진 D3 — 설계 §5.1 입력 스냅샷 + §16B "snapshot factory가 NY 거래일 기준 현재 일봉을 제거한다".
// 한 평가 안에서 현재 시각을 다시 읽지 않기 위해 now/generation을 호출자로부터 명시적으로 받는다.

/// <summary>분석 가용 상태. 자료 부족을 0점이나 실패로 숨기지 않는다(§19-9).</summary>
public static class StructureAnalysisStatus
{
    public const string Disabled = "disabled";
    public const string Stopped = "stopped";
    public const string MarketClosed = "marketClosed";
    public const string Warmup = "warmup";
    public const string Unavailable = "unavailable";
    public const string Available = "available";
}

/// <summary>
/// snapshot 구성 결과. <see cref="Snapshot"/>이 null이면 구조 계산을 시작할 수 없는 상태이며
/// 그 이유는 <see cref="Status"/>와 <see cref="Quality"/>에 남는다.
/// </summary>
public sealed record StructureSnapshotBuild(StructureSnapshot? Snapshot, string Status, NormalizedBars Bars,
    ImmutableArray<StructureBar> FiveMinuteBars, ImmutableArray<StructureDailyBar> DailyBars,
    DataQuality Quality, ImmutableArray<string> Warnings)
{
    public DateTimeOffset? LastCompletedBarStart => Bars.Bars.Length == 0 ? null : Bars.Bars[^1].Start;
    public DateTimeOffset? LastCompletedBarEnd => Bars.Bars.Length == 0 ? null : Bars.Bars[^1].End;
}

/// <summary>
/// 설계 §5.1/§12.1. REST 완료 봉·quote·일봉을 불변 snapshot으로 묶는다.
/// 진행 중 1분봉과 NY 거래일 기준 진행 중 일봉은 Domain에 전달하기 전에 제거한다(§16B).
/// </summary>
public static class StructureSnapshotFactory
{
    public const string WarningNoSession = "NO_REGULAR_SESSION";
    public const string WarningOutsideSession = "OUTSIDE_REGULAR_SESSION";
    public const string WarningNoCompletedBar = "NO_COMPLETED_BAR";
    public const string WarningInsufficientBars = "INSUFFICIENT_1M_BARS";
    public const string WarningMissingDaily = "MISSING_DAILY_CONTEXT";
    public const string WarningCurrentDailyRemoved = "CURRENT_DAILY_BAR_REMOVED";
    public const string WarningInvalidDaily = "INVALID_DAILY_BAR_DROPPED";
    public const string WarningMissingQuote = "MISSING_QUOTE";
    public const string WarningStaleQuote = "STALE_QUOTE";
    public const string WarningQuoteInFuture = "QUOTE_IN_FUTURE";
    public const string WarningMissingLiquidity = "MISSING_LIQUIDITY_COST";
    public const string BlockerInvalidBars = "INVALID_BAR_VALUES";
    public const string BlockerBarGap = "BAR_GAP";
    public const string BlockerBarConflict = "BAR_CONFLICT";

    public static StructureSnapshotBuild Create(string symbol, MarketSession session, IEnumerable<Candle>? oneMinute,
        IEnumerable<Candle>? daily, double? quotePrice, DateTimeOffset? quoteAt, DateTimeOffset now, long generation,
        StructurePolicy policy, StructureLiquidity? liquidity = null)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);

        if (session.Start is not { } sessionStart || session.End is not { } sessionEnd || sessionEnd <= sessionStart)
            return Empty(StructureAnalysisStatus.MarketClosed, WarningNoSession);
        if (now < sessionStart || now >= sessionEnd)
            return Empty(StructureAnalysisStatus.MarketClosed, WarningOutsideSession);

        // 진행 중 1분봉을 완료 봉으로 쓰지 않는다. 정규장 종료 exclusive(§5.1).
        var boundary = FloorToMinute(now < sessionEnd ? now : sessionEnd);
        var normalized = BarAggregator.Normalize(oneMinute ?? [], sessionStart, sessionEnd, boundary);
        foreach (var warning in normalized.Warnings) warnings.Add(warning);

        var (dailyBars, dailyWarnings) = Daily(daily, sessionStart, policy);
        foreach (var warning in dailyWarnings) warnings.Add(warning);

        if (normalized.Bars.Length == 0)
        {
            warnings.Add(WarningNoCompletedBar);
            return new StructureSnapshotBuild(null, StructureAnalysisStatus.Warmup, normalized,
                ImmutableArray<StructureBar>.Empty, dailyBars,
                Quality(normalized, ImmutableArray<StructureBar>.Empty, dailyBars, sessionStart, boundary, null, null,
                    now, liquidity, policy, warnings),
                warnings.ToImmutableArray());
        }

        var analysisAsOf = normalized.Bars[^1].End;
        var fiveMinute = BarAggregator.Aggregate(normalized.Bars, sessionStart, analysisAsOf, policy);

        decimal? price = null;
        if (quotePrice is { } raw && double.IsFinite(raw) && raw > 0) price = (decimal)raw;
        else warnings.Add(WarningMissingQuote);
        if (quoteAt is null) warnings.Add(WarningMissingQuote);
        else
        {
            if ((now - quoteAt.Value).TotalSeconds > policy.NewEntryQuoteMaxAgeSeconds) warnings.Add(WarningStaleQuote);
            if ((quoteAt.Value - now).TotalSeconds > policy.QuoteFutureToleranceSeconds) warnings.Add(WarningQuoteInFuture);
        }
        if (liquidity is null) warnings.Add(WarningMissingLiquidity);
        if (normalized.Bars.Length < policy.Minimum1mBars) warnings.Add(WarningInsufficientBars);

        var quality = Quality(normalized, fiveMinute, dailyBars, sessionStart, analysisAsOf, analysisAsOf, quoteAt,
            now, liquidity, policy, warnings);

        var snapshot = new StructureSnapshot(symbol, sessionStart, sessionEnd, analysisAsOf, price, quoteAt,
            (oneMinute ?? []).ToImmutableArray(), (daily ?? []).ToImmutableArray(), null, liquidity, generation);

        // §16B/§19-9(이슈 #64): 봉 공백·충돌·무효 OHLCV로 후보가 전부 막힌 상태는 정상 분석이 아니다.
        // 자료 부족을 available로 숨기지 않는다. 경고만 있는 상태(호가 결측·일봉 부족)는 그대로 available이다.
        var status = normalized.Bars.Length < policy.Minimum1mBars
            ? StructureAnalysisStatus.Warmup
            : quality.BlockersForCandidate.Length > 0
                ? StructureAnalysisStatus.Unavailable
                : StructureAnalysisStatus.Available;
        return new StructureSnapshotBuild(snapshot, status, normalized, fiveMinute, dailyBars, quality,
            warnings.ToImmutableArray());

        StructureSnapshotBuild Empty(string status, string warning)
        {
            warnings.Add(warning);
            return new StructureSnapshotBuild(null, status, NormalizedBars.Empty, ImmutableArray<StructureBar>.Empty,
                ImmutableArray<StructureDailyBar>.Empty,
                DataQuality.Empty with { Warnings = warnings.ToImmutableArray() }, warnings.ToImmutableArray());
        }
    }

    /// <summary>
    /// §5.1/§16B: 일봉은 New York 거래일 기준 이전 거래일까지만 쓴다.
    /// 진행 중 일봉의 고저·거래량은 과거 저항 근거로 넣지 않는다.
    /// </summary>
    static (ImmutableArray<StructureDailyBar> Bars, ImmutableArray<string> Warnings) Daily(IEnumerable<Candle>? daily,
        DateTimeOffset sessionStart, StructurePolicy policy)
    {
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var sessionDate = MarketRules.TradingDate(sessionStart);
        var byDate = new SortedDictionary<DateOnly, StructureDailyBar>();
        var removedCurrent = false;
        var dropped = false;

        foreach (var candle in daily ?? [])
        {
            if (candle is null) { dropped = true; continue; }
            if (!double.IsFinite(candle.Open) || !double.IsFinite(candle.High) || !double.IsFinite(candle.Low) ||
                !double.IsFinite(candle.Close) || !double.IsFinite(candle.Volume) ||
                candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0 || candle.Volume < 0 ||
                candle.High < Math.Max(candle.Open, candle.Close) || candle.Low > Math.Min(candle.Open, candle.Close) ||
                candle.High < candle.Low)
            { dropped = true; continue; }

            var date = MarketRules.TradingDate(candle.Timestamp);
            if (date >= sessionDate) { removedCurrent = true; continue; }
            byDate[date] = new StructureDailyBar(date, (decimal)candle.Open, (decimal)candle.High,
                (decimal)candle.Low, (decimal)candle.Close, candle.Volume);
        }

        if (removedCurrent) warnings.Add(WarningCurrentDailyRemoved);
        if (dropped) warnings.Add(WarningInvalidDaily);
        if (byDate.Count == 0) warnings.Add(WarningMissingDaily);
        return (byDate.Values.TakeLast(policy.DailyLookbackSessions).ToImmutableArray(), warnings.ToImmutableArray());
    }

    /// <summary>
    /// §16B DataQuality: 원천별 status/count/expectedCount/first/last/gaps/conflicts/coverageRatio와
    /// 대상별 blockers 배열. expectedCount를 정의할 수 없는 원천은 ratio=null이다.
    /// 이 단계는 봉·일봉·호가만 채우고 지표/5m 구조/프로파일 사실은 <see cref="Complete"/>가 더한다.
    /// </summary>
    static DataQuality Quality(NormalizedBars bars, ImmutableArray<StructureBar> fiveMinute,
        ImmutableArray<StructureDailyBar> daily, DateTimeOffset sessionStart, DateTimeOffset boundary,
        DateTimeOffset? analysisAsOf, DateTimeOffset? quoteAt, DateTimeOffset now, StructureLiquidity? liquidity,
        StructurePolicy policy, SortedSet<string> warnings)
    {
        var elapsedMinutes = (int)Math.Max(Math.Floor((boundary - sessionStart).TotalMinutes), 0);
        var sources = ImmutableArray.CreateBuilder<DataSourceQuality>();

        var barStatus = bars.Bars.Length == 0 ? SourceStatus.Missing
            : bars.Bars.Length < policy.Minimum1mBars ? SourceStatus.Missing
            : bars.Conflicts.Length > 0 || bars.Gaps.Length > 0 ? SourceStatus.Approximate
            : SourceStatus.Available;
        sources.Add(new DataSourceQuality("bars1m", barStatus, bars.Bars.Length,
            elapsedMinutes == 0 ? null : elapsedMinutes,
            bars.Bars.Length == 0 ? null : bars.Bars[0].Start, analysisAsOf, bars.Gaps, bars.Conflicts,
            elapsedMinutes == 0 ? null : (double)bars.Bars.Length / elapsedMinutes,
            bars.Warnings));

        var expected5m = elapsedMinutes / policy.AggregationMinutes;
        sources.Add(new DataSourceQuality("bars5m", fiveMinute.Length == 0 ? SourceStatus.Missing : SourceStatus.Available,
            fiveMinute.Length, expected5m == 0 ? null : expected5m,
            fiveMinute.Length == 0 ? null : fiveMinute[0].Start, fiveMinute.Length == 0 ? null : fiveMinute[^1].End,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            expected5m == 0 ? null : (double)fiveMinute.Length / expected5m, ImmutableArray<string>.Empty));

        sources.Add(new DataSourceQuality("daily", daily.Length == 0 ? SourceStatus.Missing : SourceStatus.Available,
            daily.Length, policy.DailyLookbackSessions, null, null,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            (double)daily.Length / policy.DailyLookbackSessions,
            daily.Length == 0 ? [WarningMissingDaily] : ImmutableArray<string>.Empty));

        var quoteStatus = quoteAt is null ? SourceStatus.Missing
            : (now - quoteAt.Value).TotalSeconds > policy.NewEntryQuoteMaxAgeSeconds ? SourceStatus.Stale
            : SourceStatus.Available;
        sources.Add(new DataSourceQuality("quote", quoteStatus, quoteAt is null ? 0 : 1, 1, quoteAt, quoteAt,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, null, ImmutableArray<string>.Empty));

        // §9.1: 전체 관심종목에 5초 호가 조회를 추가하지 않는다. 없으면 비용 불확실성만 노출한다(§16B).
        sources.Add(new DataSourceQuality("liquidity", liquidity is null ? SourceStatus.Missing : SourceStatus.Available,
            liquidity is null ? 0 : 1, null, liquidity?.At, liquidity?.At,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, null,
            liquidity is null ? [WarningMissingLiquidity] : ImmutableArray<string>.Empty));

        // §16B: 잘못된 OHLCV·분봉 공백/충돌은 신규 분석 차단이므로 추세에도 동일하게 적용한다.
        var barBlockers = new SortedSet<string>(StringComparer.Ordinal);
        if (bars.DroppedInvalid > 0) barBlockers.Add(BlockerInvalidBars);
        if (bars.Gaps.Length > 0) barBlockers.Add(BlockerBarGap);
        if (bars.Conflicts.Length > 0) barBlockers.Add(BlockerBarConflict);
        var blockers = barBlockers.ToImmutableArray();

        return new DataQuality(sources.ToImmutable(), warnings.ToImmutableArray(),
            blockers, blockers, blockers, blockers);
    }

    /// <summary>
    /// 추세·Zone 계산 결과의 품질 사실을 합친다(§16B): 지표 결측은 Trend/READY 차단,
    /// 5m 구조 결측은 READY 차단, 프로파일 근사는 경고만이다.
    /// </summary>
    public static DataQuality Complete(DataQuality baseQuality, TrendAssessment? trend, VolumeProfile? profile,
        IEnumerable<string>? zoneWarnings, IEnumerable<string>? spreadReasons)
    {
        ArgumentNullException.ThrowIfNull(baseQuality);
        var warnings = new SortedSet<string>(baseQuality.Warnings, StringComparer.Ordinal);
        var forTrend = new SortedSet<string>(baseQuality.BlockersForTrend, StringComparer.Ordinal);
        var forZone = new SortedSet<string>(baseQuality.BlockersForZone, StringComparer.Ordinal);
        var forCandidate = new SortedSet<string>(baseQuality.BlockersForCandidate, StringComparer.Ordinal);
        var forReady = new SortedSet<string>(baseQuality.BlockersForReady, StringComparer.Ordinal);

        foreach (var warning in zoneWarnings ?? []) warnings.Add(warning);
        foreach (var reason in spreadReasons ?? []) warnings.Add(reason);
        foreach (var warning in profile?.Warnings ?? ImmutableArray<string>.Empty) warnings.Add(warning);

        var sources = baseQuality.Sources;
        if (profile is not null)
            sources = sources.Add(new DataSourceQuality("volumeProfile", SourceStatus.Approximate,
                profile.Bins.Length, null, null, null, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
                null, profile.Warnings));

        if (trend is not null)
        {
            foreach (var warning in trend.Warnings) warnings.Add(warning);
            foreach (var blocker in trend.BlockersForTrend) forTrend.Add(blocker);
            foreach (var blocker in trend.BlockersForReady) forReady.Add(blocker);
            sources = sources.Add(new DataSourceQuality("indicators",
                trend.Available ? SourceStatus.Available : SourceStatus.Missing,
                trend.BarCount, null, null, trend.AnalysisCutoff, ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty, null, trend.MissingComponents));
            sources = sources.Add(new DataSourceQuality("pivots5m",
                trend.StructureEvidenceMissing ? SourceStatus.Missing : SourceStatus.Available,
                trend.StructureDirection is null ? 0 : 4, 4, null, null, ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty, null,
                trend.StructureEvidenceMissing ? [TrendEvaluator.BlockerMissing5mStructure] : ImmutableArray<string>.Empty));
        }

        return new DataQuality(sources, warnings.ToImmutableArray(), forTrend.ToImmutableArray(),
            forZone.ToImmutableArray(), forCandidate.ToImmutableArray(), forReady.ToImmutableArray());
    }

    static DateTimeOffset FloorToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);
}
