using Astra.Server.Domain.Validation;
using Xunit;

namespace Astra.Server.Tests;

/// <summary>
/// 이슈 #28 — 후보 → 결정 → 거래 → 결과 연결의 회귀 근거.
/// 축: 이벤트 연결 · 반복 poll 중복 제거 · 누락 · 버전/모드 혼합 방지 · 미래 데이터 누출 방지.
/// </summary>
public sealed class ValidationLinkerTests
{
    [Fact]
    public void CandidateIsLinkedToTheTradeThatSharesItsEventId()
    {
        var (row, trade) = Vx.Entered("E1", 1.4);

        var result = Vx.Link([row], [trade]);

        var linked = Assert.Single(result.Candidates);
        Assert.Equal("E1", linked.EventId);
        Assert.Equal(CandidateOutcome.Entered, linked.Outcome);
        Assert.Equal("t-E1", linked.Trade?.TradeId);
        Assert.True(linked.HasRealizedPnl);
        Assert.Equal(1.4, linked.RealizedPnlPercent);
        Assert.Equal(1, result.Audit.TradesLinked);
        Assert.Equal(0, result.Audit.TradesUnlinked);
        Assert.Empty(result.UnlinkedTrades);
    }

    /// <summary>같은 이벤트를 15초마다 다시 본 행은 독립 표본이 아니다 — 한 건으로 접히고 최종 상태만 남는다.</summary>
    [Fact]
    public void RepeatedPollsForOneEventCollapseIntoASingleSample()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "WAIT", planned: false)], minute: 41),
            Vx.Row("obs-2", [Vx.Candidate("E1", "READY")], minute: 42),
            Vx.Row("obs-3", [Vx.Candidate("E1", "ENTERED")], minute: 43)
        };

        var result = Vx.Link(rows, []);

        var linked = Assert.Single(result.Candidates);
        Assert.Equal("ENTERED", linked.FinalState);
        Assert.Equal(3, linked.ObservationCount);
        Assert.Equal(2, linked.PollRowsCollapsed);
        Assert.Equal(Vx.At(0, 41), linked.FirstObservedAt);
        Assert.Equal(Vx.At(0, 43), linked.LastObservedAt);
        Assert.Equal(3, result.Audit.CandidateRows);
        Assert.Equal(2, result.Audit.DuplicatePollRows);
        Assert.Equal(1, result.Audit.DistinctEvents);
    }

    /// <summary>재시작으로 같은 줄이 두 번 append된 경우. 첫 줄만 쓰고 사실을 감사에 남긴다.</summary>
    [Fact]
    public void DuplicateObservationIdIsUsedOnceAndReported()
    {
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "READY")]);

        var result = Vx.Link([row, row], []);

        var linked = Assert.Single(result.Candidates);
        Assert.Equal(1, linked.ObservationCount);
        Assert.Equal(1, result.Audit.DuplicateObservationIds);
        Assert.Contains($"{LinkCodes.DuplicateObservationId}:obs-1", result.Audit.Conflicts);
    }

    /// <summary>엔진 버전이 섞인 이벤트는 합치지 않고 통째로 제외한다 — 다른 계산 규칙의 표본을 한 코호트에 넣지 않는다.</summary>
    [Fact]
    public void EventObservedUnderTwoEngineVersionsIsDroppedNotMerged()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "READY")], minute: 41),
            Vx.Row("obs-2", [Vx.Candidate("E1", "ENTERED")], minute: 42, engine: "v5-structure.3")
        };

        var result = Vx.Link(rows, []);

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Audit.DroppedEvents);
        Assert.Contains($"{LinkCodes.EventVersionMixed}:E1", result.Audit.Conflicts);
    }

    /// <summary>shadow와 active는 실행 성격이 다른 별개 집단이라 한 이벤트로 합치지 않는다.</summary>
    [Fact]
    public void EventObservedUnderTwoModesIsDroppedNotMerged()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "READY")], minute: 41, mode: "shadow"),
            Vx.Row("obs-2", [Vx.Candidate("E1", "READY")], minute: 42, mode: "active")
        };

        var result = Vx.Link(rows, []);

        Assert.Empty(result.Candidates);
        Assert.Contains($"{LinkCodes.EventModeMixed}:E1", result.Audit.Conflicts);
    }

    [Fact]
    public void TradeWhosePlanVersionDiffersFromTheObservationIsNotLinked()
    {
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "ENTERED")]);
        var trade = Vx.Trade("t-1", "E1", 2.0, engine: "v5-structure.3");

        var result = Vx.Link([row], [trade]);

        var linked = Assert.Single(result.Candidates);
        Assert.Null(linked.Trade);
        Assert.Contains($"{LinkCodes.TradeVersionMismatch}:t-1", result.Audit.Conflicts);
        Assert.Equal(1, result.Audit.TradesUnlinked);
    }

    /// <summary>관측이 보존되지 않아 근거 후보를 찾지 못한 거래. 없는 것으로 치지 않고 별도 목록으로 드러낸다.</summary>
    [Fact]
    public void TradeWithoutAnyObservationIsReportedAsUnlinked()
    {
        var result = Vx.Link([], [Vx.Trade("t-1", "E1", 3.0)]);

        Assert.Empty(result.Candidates);
        var orphan = Assert.Single(result.UnlinkedTrades);
        Assert.Equal("t-1", orphan.TradeId);
        Assert.Contains($"{LinkCodes.TradeWithoutObservation}:t-1", result.Audit.Conflicts);
        Assert.Contains(LinkCodes.TradeRetentionCapped, result.Audit.Limitations);
    }

    // ── 미래 데이터 누출 방지 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ObservationRecordedAfterAsOfIsExcluded()
    {
        var asOf = Vx.At(0, 50);
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "READY")], minute: 41),
            Vx.Row("obs-2", [Vx.Candidate("E2", "READY")], minute: 55)
        };

        var result = Vx.Link(rows, [], asOf);

        Assert.Equal("E1", Assert.Single(result.Candidates).EventId);
        Assert.Equal(1, result.Audit.ObservationRowsAfterCutoff);
    }

    [Fact]
    public void TradeEnteredAfterAsOfIsNotCountedAsEntered()
    {
        var asOf = Vx.At(0, 50);
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "READY")], minute: 41);
        var trade = Vx.Trade("t-1", "E1", 5.0, entryMinute: 60, exitMinute: 70);

        var result = Vx.Link([row], [trade], asOf);

        var linked = Assert.Single(result.Candidates);
        Assert.Null(linked.Trade);
        Assert.Equal(CandidateOutcome.Ready, linked.Outcome);
        Assert.Equal(1, result.Audit.TradesAfterCutoff);
        Assert.Equal(0, result.Audit.TradesLinked);
    }

    /// <summary>진입은 과거지만 청산이 평가 시각 이후인 거래. 손익을 "아직 모름"으로 절단해 표본에 넣지 않는다.</summary>
    [Fact]
    public void ExitAfterAsOfIsCensoredSoFuturePnlNeverEntersTheSample()
    {
        var asOf = Vx.At(0, 50);
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "ENTERED")], minute: 41);
        var trade = Vx.Trade("t-1", "E1", 9.9, entryMinute: 42, exitMinute: 80);

        var result = Vx.Link([row], [trade], asOf);

        var linked = Assert.Single(result.Candidates);
        Assert.NotNull(linked.Trade);
        Assert.Equal("OPEN", linked.Trade!.StatusAsOf);
        Assert.Equal("TARGET", linked.Trade.StoredStatus);
        Assert.Null(linked.Trade.PnlPercent);
        Assert.Null(linked.Trade.ExitAt);
        Assert.False(linked.Trade.OutcomeKnown);
        Assert.False(linked.HasRealizedPnl);
        Assert.Null(linked.RealizedPnlPercent);
        Assert.Equal(1, result.Audit.OutcomesCensoredByCutoff);
    }

    // ── 누락·결측 ───────────────────────────────────────────────────────────────────────────

    /// <summary>계획이 없는 후보의 비용 가정은 null(미수집)이다. false(=비용 정상)로 바꾸면 결측이 사라진다.</summary>
    [Fact]
    public void CandidateWithoutPlanKeepsCostAssumptionsUncollectedInsteadOfFalse()
    {
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "REJECTED", planned: false, rejections: ["NET_R_BELOW_MIN"])]);

        var result = Vx.Link([row], []);

        var linked = Assert.Single(result.Candidates);
        Assert.Null(linked.MissingLiquidityCost);
        Assert.Null(linked.ValidSpread);
        Assert.Null(linked.EligibilityCostModelVersion);
        Assert.Equal(LiquidityClass.Uncollected, linked.Liquidity);
        Assert.Contains(LinkCodes.CostAssumptionOnlyForPlannedCandidates, result.Audit.Limitations);
        Assert.Contains(LinkCodes.RejectedPathNotRetained, result.Audit.Limitations);
    }

    /// <summary>호가 결측(0 가정)과 실제로 0으로 관측된 스프레드는 다른 집단이다.</summary>
    [Fact]
    public void MissingLiquidityAndObservedZeroSpreadAreDifferentClasses()
    {
        var rows = new[]
        {
            Vx.Row("obs-1", [Vx.Candidate("E1", "ENTERED", missingLiquidity: true)], minute: 41),
            Vx.Row("obs-2", [Vx.Candidate("E2", "ENTERED", spread: 0m)], minute: 42),
            Vx.Row("obs-3", [Vx.Candidate("E3", "ENTERED", spread: 0.03m)], minute: 43)
        };

        var result = Vx.Link(rows, []);

        Assert.Equal(LiquidityClass.Missing, Single(result, "E1").Liquidity);
        Assert.Equal(LiquidityClass.ObservedZeroSpread, Single(result, "E2").Liquidity);
        Assert.Equal(LiquidityClass.Observed, Single(result, "E3").Liquidity);
    }

    [Fact]
    public void SummaryOnlyEventIsFlaggedBecauseEvidenceIsOmitted()
    {
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "READY")], detail: "summary");

        var result = Vx.Link([row], []);

        Assert.True(Assert.Single(result.Candidates).SummaryOnly);
        Assert.Equal(1, result.Audit.SummaryOnlyEvents);
        Assert.Contains(LinkCodes.SummaryObservationOmitsEvidence, result.Audit.Limitations);
    }

    /// <summary>대표 거절 사유는 ordinal 최소값으로 고정한다 — 같은 입력이면 같은 결과가 나와야 한다.</summary>
    [Fact]
    public void PrimaryRejectReasonIsDeterministicAndCodesAreDeduplicated()
    {
        var row = Vx.Row("obs-1",
            [Vx.Candidate("E1", "REJECTED", planned: false, rejections: ["RISK_ABOVE_MAX", "NET_R_BELOW_MIN", "NET_R_BELOW_MIN"])]);

        var linked = Assert.Single(Vx.Link([row], []).Candidates);

        Assert.Equal("NET_R_BELOW_MIN", linked.PrimaryRejectReason);
        Assert.Equal(["NET_R_BELOW_MIN", "RISK_ABOVE_MAX"], linked.RejectionCodes);
    }

    [Fact]
    public void SessionAndTradingDateComeFromTheObservationNotTheTrade()
    {
        var row = Vx.Row("obs-1", [Vx.Candidate("E1", "ENTERED", day: 2)], day: 2);

        var linked = Assert.Single(Vx.Link([row], [Vx.Trade("t-1", "E1", 1.0, day: 2)]).Candidates);

        Assert.Equal(Vx.SessionOn(2), linked.SessionStart);
        Assert.Equal(DateOnly.FromDateTime(Vx.SessionOn(2).DateTime), linked.TradingDate);
    }

    static LinkedCandidate Single(LinkResult result, string eventId) =>
        Assert.Single(result.Candidates, x => x.EventId == eventId);
}
