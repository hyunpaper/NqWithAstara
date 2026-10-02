using System.Collections.Immutable;
using Astra.Server.Domain.Structure;

/// <summary>
/// v5 구조 엔진 D2 테스트 fixture. 임의 값이며 실제 종목 추천이 아니다.
/// D1 fixture(<see cref="Fx"/>)의 세션·봉 헬퍼를 재사용하고, D2는 Zone/추세 스냅샷을 직접 구성해
/// 계획·품질·상태 로직만 분리해 검증한다.
/// </summary>
static class D2
{
    public static readonly StructurePolicy P = StructurePolicy.Default;

    /// <summary>지정한 값을 그대로 갖는 강도. D1 계산은 별도 D1 테스트가 검증한다.</summary>
    public static ZoneStrength Strength(double value, int success = 1, int failed = 0, int families = 2) =>
        new(1 - Math.Exp(-1.0 / 2), .5, 1, 1 - Math.Exp(-families / 2.0), Math.Exp(-failed), value,
            success + failed, success, failed, 0, families, families,
            ["touchEvidence", "reactionEvidence", "recency", "confluence"], ImmutableArray<string>.Empty);

    public static PriceZone Zone(string id, decimal lower, decimal upper, ZoneRole role, double? strength,
        bool eligible = true, int confirmedMinute = 5, bool retired = false, bool profileOnly = false,
        params ZoneRoleChange[] history)
    {
        var source = new ZoneSource(StructureMath.SourceId("d2-source", id), ZoneSourceFamily.Pivot,
            "pivot-1m-L", (lower + upper) / 2m, Fx.At(confirmedMinute), Fx.At(confirmedMinute), false);
        var daily = new ZoneSource(StructureMath.SourceId("d2-daily", id), ZoneSourceFamily.ContextLevel,
            "daily-L", (lower + upper) / 2m, Fx.SessionStart, Fx.SessionStart, false, 1);
        return new PriceZone(id, 1, 1, lower, upper, role, role, Fx.At(confirmedMinute), Fx.At(confirmedMinute),
            [source, daily], ImmutableArray<EvidenceGroup>.Empty, ImmutableArray<string>.Empty,
            strength is null ? null : Strength(strength.Value), eligible,
            eligible ? ImmutableArray<string>.Empty : ["INSUFFICIENT_INDEPENDENT_EVIDENCE"],
            ImmutableArray<string>.Empty, history.ToImmutableArray(), profileOnly, retired);
    }

    public static readonly StructurePolicy PreCycle45 = StructurePolicy.Default with
    {
        RequirePullbackNearVwap = false,
        RequireBreakoutNearVwap = false,
        RequireMinimumReboundEntryQuality = false,
        RequireBreakoutAboveVwap = false,
        RequireMaximumReboundNetR = false,
        EnableTwoRFeeBreakEvenStop = false,
        CapStructuralTargetAtTwoR = false,
        EnableHalfRFeeBreakEvenStopForPositiveBenchmark = false,
        AllowQualifiedTransitionPullback = false,
        AllowQualifiedTransitionBreakout = false,
        ExemptBreakoutFromPositiveBenchmarkHalfRStop = false
    };

    /// <summary>#209 netR 상한 밖의 주제를 다루는 fixture용 정책. 상한 자체는 <c>StructureMaxNetRTests</c>가 고정한다.</summary>
    public static readonly StructurePolicy WideNetR = StructurePolicy.Default with { MaxNetR = 100 };

    public static PriceZone Support(decimal lower, decimal upper, double strength = .6, string id = "support-zone",
        int confirmedMinute = 5, bool eligible = true) =>
        Zone(id, lower, upper, ZoneRole.Support, strength, eligible, confirmedMinute);

    public static PriceZone Resistance(decimal lower, decimal upper, double strength = .7, string id = "resistance-zone",
        int confirmedMinute = 5, bool eligible = true) =>
        Zone(id, lower, upper, ZoneRole.Resistance, strength, eligible, confirmedMinute);

    public static TouchEpisode Episode(string zoneId, int startMinute, int? resolvedMinute = null,
        EpisodeOutcome outcome = EpisodeOutcome.Success, ZoneRole role = ZoneRole.Support,
        double? excursion = 1.0, decimal favorableExtreme = 0m) =>
        new(zoneId, Fx.At(startMinute), resolvedMinute is null ? null : Fx.At(resolvedMinute.Value), outcome,
            role, .2, excursion, favorableExtreme, ImmutableArray<string>.Empty);

    /// <summary>추세 스냅샷을 직접 구성한다. 추세 계산 자체는 <c>StructureD2TrendTests</c>가 검증한다.</summary>
    public static TrendAssessment Trend(TrendState state = TrendState.Up, double? signedTrend = 40,
        double? structureDirection = .5, int barCount = 40, params string[] readyBlockers) =>
        new(state, signedTrend, signedTrend is null ? null : signedTrend.Value / 100, structureDirection, .5, .2,
            100.1, 100.0, 100.05, .1, structureDirection is null, barCount, Fx.At(barCount),
            ImmutableArray<TrendComponent>.Empty,
            structureDirection is null ? ["price"] : ["price", "structure"],
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty,
            readyBlockers.ToImmutableArray());

    public static StructureLiquidity Quote(decimal bid, decimal ask, int minute, double size = 100) =>
        new(bid, ask, Fx.At(minute), size, size);

    /// <summary>설계 §14 예시 A의 계획 입력. Entry=100.00, support=[99.20,99.40], 눌림 Low=99.15, ATR=0.20, spread=0.02.</summary>
    public static PlanRequest ExampleA(params PriceZone[] zones)
    {
        var support = Support(99.20m, 99.40m);
        var all = zones.Length > 0 ? zones.ToImmutableArray() : [support, Resistance(101.80m, 102.10m)];
        return new PlanRequest(Fx.Symbol, "event-A", "PULLBACK", 100.00m, 99.15m,
            all.FirstOrDefault(x => x.Role == ZoneRole.Support) ?? support, all, .20, .02m,
            Fx.At(40), Fx.At(45));
    }
}
