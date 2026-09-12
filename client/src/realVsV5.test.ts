import { describe, expect, it } from "vitest";
import {
  atrText,
  classLabel,
  limitationLabel,
  matchRateText,
  sellPositionLabel,
  summaryTiles,
  tableRows,
  type RealVsV5Data,
  type RealVsV5Row,
  type RealVsV5Summary,
} from "./realVsV5";

const report = (over: Partial<RealVsV5Summary> = {}): RealVsV5Summary => ({
  tradingDate: "2026-09-11",
  windowMinutes: 5,
  realBuys: 4,
  realSells: 2,
  matched: 3,
  engineOnly: 1,
  userOnly: 1,
  matchRatePercent: 75,
  deviationSamples: 3,
  deviationMedianAtr: 0.5,
  deviationQ1Atr: 0.25,
  deviationQ3Atr: 1.5,
  topUserOnlyRejections: [],
  rows: [],
  ...over,
});

const data = (over: Partial<RealVsV5Summary> = {}): RealVsV5Data => ({
  tradingDate: "2026-09-11",
  fillsCollected: true,
  collectedAt: "2026-09-11T20:10:00Z",
  observationLines: 12,
  observationFileFound: true,
  report: report(over),
  limitations: [],
});

const row = (over: Partial<RealVsV5Row> = {}): RealVsV5Row => ({
  classification: "MATCHED",
  symbol: "TEST",
  side: "BUY",
  at: "2026-09-11T14:00:00Z",
  fillPrice: 100.25,
  fillQuantity: 10,
  eventId: "E1",
  v5State: "READY",
  kind: "PULLBACK",
  trendState: "UP",
  entryQuality: 62,
  entryReference: 100,
  deviationAtr: 1.25,
  sellPosition: null,
  rejectionCodes: [],
  ...over,
});

describe("summaryTiles", () => {
  it("4칸을 매칭률·엔진 단독·사용자 단독·중앙 괴리 순으로 만든다", () => {
    const tiles = summaryTiles(data());

    expect(tiles.map((x) => x.label)).toEqual(["매칭률", "엔진 단독", "사용자 단독", "중앙 괴리"]);
    expect(tiles[0].value).toBe("75.0%");
    expect(tiles[0].help).toBe("실매수 4건 중 3건이 v5 READY/ENTERED 동반 (±5분)");
    expect(tiles[3].value).toBe("+0.50 ATR");
    expect(tiles[3].help).toContain("1사분위 +0.25 ATR");
  });

  it("표본이 없으면 0%가 아니라 미수집으로 적는다", () => {
    const tiles = summaryTiles(
      data({ realBuys: 0, matched: 0, matchRatePercent: null, deviationSamples: 0, deviationMedianAtr: null, deviationQ1Atr: null, deviationQ3Atr: null }),
    );

    expect(tiles[0].value).toBe("—");
    expect(tiles[3].value).toBe("—");
    expect(tiles[3].help).toContain("사분위 표본 없음");
  });
});

describe("tableRows", () => {
  it("매수 행은 v5 상태와 ATR 단위 괴리를 그대로 옮긴다", () => {
    const [mapped] = tableRows([row()], (x) => x);

    expect(mapped.side).toBe("매수");
    expect(mapped.price).toBe("100.25");
    expect(mapped.state).toBe("READY");
    expect(mapped.deviation).toBe("+1.25 ATR");
    expect(mapped.note).toBe("PULLBACK");
    expect(mapped.muted).toBe(false);
  });

  it("거절 코드가 있으면 비고에 코드를 적는다", () => {
    const [mapped] = tableRows(
      [row({ classification: "USER_ONLY", v5State: "REJECTED", rejectionCodes: ["A", "B"], deviationAtr: null })],
      (x) => x,
    );

    expect(mapped.state).toBe("REJECTED");
    expect(mapped.note).toBe("A, B");
    expect(mapped.deviation).toBe("—");
  });

  it("엔진 단독 행은 체결가가 없고 흐리게 표시한다", () => {
    const [mapped] = tableRows(
      [row({ classification: "ENGINE_ONLY", fillPrice: null, v5State: "ENTERED" })],
      (x) => x,
    );

    expect(mapped.price).toBe("—");
    expect(mapped.muted).toBe(true);
  });

  it("매도 행은 stop/target 대비 위치를 비고에 적는다", () => {
    const [mapped] = tableRows(
      [row({ classification: "SELL", side: "SELL", v5State: null, sellPosition: "BELOW_STOP" })],
      (x) => x,
    );

    expect(mapped.side).toBe("매도");
    expect(mapped.state).toBe("매도");
    expect(mapped.note).toBe("손절가 아래");
  });
});

describe("표기 헬퍼", () => {
  it("분류·위치·한계 코드를 한국어로 옮기고 모르는 값은 그대로 둔다", () => {
    expect(classLabel("MATCHED")).toBe("일치");
    expect(classLabel("UNKNOWN_CODE")).toBe("UNKNOWN_CODE");
    expect(sellPositionLabel(null)).toBe("—");
    expect(sellPositionLabel("ABOVE_TARGET")).toBe("목표가 위");
    expect(limitationLabel("REAL_FILLS_NOT_COLLECTED")).toContain("수집되지");
    expect(limitationLabel("X")).toBe("X");
  });

  it("괴리와 매칭률의 미수집은 대시다", () => {
    expect(atrText(null)).toBe("—");
    expect(atrText(-0.5)).toBe("-0.50 ATR");
    expect(matchRateText(report({ matchRatePercent: null }))).toBe("—");
  });
});
