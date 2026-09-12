// 이슈 #27 — 대시보드 헬퍼(순수 함수) 검증.
// 관점: v4 거래는 기존 점수 축 그대로, v5 거래는 동결 근거만(재계산 없음) 보여 주고
// 계획 netR을 실현 손익처럼 읽히게 하지 않는다.
import { describe, expect, it } from "vitest";
import {
  plannedNetRText,
  tradeEntryTooltip,
  trendKo,
  winRateText,
  type CohortStats,
  type TradeStructure,
} from "./dashboardTypes";

const stats = (over: Partial<CohortStats> = {}): CohortStats => ({
  total: 0,
  open: 0,
  closed: 0,
  validClosed: 0,
  wins: 0,
  winRate: null,
  avgPnl: null,
  missingPnl: 0,
  estimatedExits: 0,
  avgPlannedNetR: null,
  plannedNetRSamples: 0,
  ...over,
});

const structure: TradeStructure = {
  entryEventId: "TEST|e1",
  planSnapshot: {
    planId: "plan-1",
    kind: "REBOUND",
    entryReference: 99.62,
    invalidationAnchor: 99.44,
    stop: 99.32,
    target: 101.45,
    invalidationZoneId: "z-support",
    invalidationLower: 99.44,
    invalidationUpper: 99.7,
    targetZoneId: "z-resist",
    targetLower: 101.5,
    targetUpper: 101.9,
    netR: 1.63,
    missingLiquidity: true,
    eligibilityCostModelVersion: "cost-eligibility.1",
    realizedFillCostModelVersion: "cost-fill.1",
    engineVersion: "v5-structure.1",
    policyHash: "hash-A",
    reasonCodes: ["MISSING_LIQUIDITY_COST"],
    explanation: "지지 반응 후 저항 하단 앞 계획",
  },
  trendAtEntry: "UP",
  signedTrendAtEntry: 41,
  entryQualityAtEntry: 55.5,
  analysisAsOf: "2026-09-09T14:10:00-04:00",
  quoteAt: "2026-09-09T14:10:00-04:00",
  structuralExitPolicyVersion: "v5-exit.frozen-plan.1",
};

describe("tradeEntryTooltip", () => {
  it("v4 거래(동결 컨텍스트 없음)는 기존 점수 축을 유지한다", () => {
    const tip = tradeEntryTooltip({
      score: 80,
      extSigma: 1.2,
      relVolume: 1.5,
      buyShare: 61,
      rsi: 55,
      reasons: ["돌파 확인"],
    });
    expect(tip).toContain("점수 80");
    expect(tip).toContain("돌파 확인");
    expect(tip).not.toContain("동결");
  });

  it("v5 거래는 동결 근거(품질·추세·계획·zone·엔진)를 보여 주고 계획 netR을 실현 손익과 구분한다", () => {
    const tip = tradeEntryTooltip({ structure });
    expect(tip).toContain("동결 근거");
    expect(tip).toContain("EntryQuality 55.5");
    expect(tip).toContain("추세 상승 (41.0)");
    expect(tip).toContain("셋업 REBOUND");
    expect(tip).toContain("계획 netR 1.63R (실현 손익 아님)");
    expect(tip).toContain("비용 결측(스프레드 0 가정)");
    expect(tip).toContain("z-support");
    expect(tip).toContain("z-resist");
    expect(tip).toContain("v5-structure.1");
    expect(tip).toContain("hash-A");
    expect(tip).toContain("지지 반응 후 저항 하단 앞 계획");
    // v4 축의 결측 나열("점수 —")로 채우지 않는다.
    expect(tip).not.toContain("점수 —");
  });

  it("동결 값이 미수집이면 0으로 채우지 않고 '미수집'으로 적는다", () => {
    const tip = tradeEntryTooltip({
      structure: {
        ...structure,
        entryQualityAtEntry: null,
        signedTrendAtEntry: null,
        trendAtEntry: "UNKNOWN",
      },
    });
    expect(tip).toContain("EntryQuality 미수집");
    expect(tip).toContain("추세 판정 불가 (미수집)");
    expect(tip).not.toContain("EntryQuality 0");
  });
});

describe("winRateText / plannedNetRText / trendKo", () => {
  it("표본 0이면 0%가 아니라 '표본 없음'이다", () => {
    expect(winRateText(stats())).toBe("표본 없음");
  });
  it("승률에는 항상 분모가 붙는다", () => {
    expect(winRateText(stats({ winRate: 63.6, wins: 7, validClosed: 11 }))).toBe(
      "63.6% (7/11건)",
    );
  });
  it("계획 netR 표본이 없으면 '미수집'이다", () => {
    expect(plannedNetRText(stats())).toBe("미수집");
    expect(
      plannedNetRText(stats({ avgPlannedNetR: 1.62, plannedNetRSamples: 14 })),
    ).toBe("1.62R (14건)");
  });
  it("추세 한글화는 알 수 없는 값을 미수집으로 처리한다", () => {
    expect(trendKo("UP")).toBe("상승");
    expect(trendKo("UNKNOWN")).toBe("판정 불가");
    expect(trendKo(null)).toBe("미수집");
  });
});
