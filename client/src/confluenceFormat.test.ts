import { describe, expect, it } from "vitest";
import {
  allWarmup,
  barOpacity,
  barWidthPercent,
  gaugeTone,
  isTechniqueWarmup,
  scoreText2,
  techniqueLabel,
} from "./confluenceFormat";
import type { ConfluenceTechnique } from "./confluenceTypes";

const technique = (over: Partial<ConfluenceTechnique> = {}): ConfluenceTechnique => ({
  name: "MACD",
  score: 0,
  confidence: 1,
  weight: 1,
  warmup: false,
  contributing: true,
  correlationGroup: null,
  evidence: {},
  ...over,
});

describe("gaugeTone", () => {
  it("−0.3 이하는 bearish다", () => {
    expect(gaugeTone(-0.3)).toBe("bearish");
    expect(gaugeTone(-0.31)).toBe("bearish");
    expect(gaugeTone(-1)).toBe("bearish");
  });
  it("+0.3 이상은 bullish다", () => {
    expect(gaugeTone(0.3)).toBe("bullish");
    expect(gaugeTone(0.31)).toBe("bullish");
    expect(gaugeTone(1)).toBe("bullish");
  });
  it("경계 안쪽은 neutral이다", () => {
    expect(gaugeTone(0.29)).toBe("neutral");
    expect(gaugeTone(-0.29)).toBe("neutral");
    expect(gaugeTone(0)).toBe("neutral");
  });
  it("결측은 neutral이다", () => {
    expect(gaugeTone(null)).toBe("neutral");
    expect(gaugeTone(undefined)).toBe("neutral");
    expect(gaugeTone(NaN)).toBe("neutral");
  });
});

describe("techniqueLabel", () => {
  it("사전에 등록된 기법명을 한국어로 바꾼다", () => {
    expect(techniqueLabel("BB_PERCENT_B")).toBe("볼린저 %B");
    expect(techniqueLabel("RS_QQQ")).toBe("상대강도(QQQ)");
    expect(techniqueLabel("OBI")).toBe("호가 불균형");
  });
  it("미등록 기법명은 원문 그대로 폴백한다", () => {
    expect(techniqueLabel("UNKNOWN_TECHNIQUE")).toBe("UNKNOWN_TECHNIQUE");
  });
  it("결측이면 —다", () => {
    expect(techniqueLabel(null)).toBe("—");
    expect(techniqueLabel(undefined)).toBe("—");
  });
});

describe("barWidthPercent", () => {
  it("score 절대값을 0~100% 폭으로 바꾼다", () => {
    expect(barWidthPercent(0.5)).toBe(50);
    expect(barWidthPercent(-0.5)).toBe(50);
    expect(barWidthPercent(1)).toBe(100);
  });
  it("결측은 0이다", () => {
    expect(barWidthPercent(null)).toBe(0);
  });
});

describe("barOpacity", () => {
  it("confidence를 0~1로 클램프한다", () => {
    expect(barOpacity(0.5)).toBe(0.5);
    expect(barOpacity(1.5)).toBe(1);
    expect(barOpacity(-0.5)).toBe(0);
  });
  it("결측은 완전 불투명(1)이다", () => {
    expect(barOpacity(null)).toBe(1);
  });
});

describe("isTechniqueWarmup", () => {
  it("서버 status가 warmup이면 기법 자체 플래그와 무관하게 true다", () => {
    expect(isTechniqueWarmup(technique({ warmup: false }), true)).toBe(true);
  });
  it("status가 ready면 기법 자체 warmup 플래그를 따른다", () => {
    expect(isTechniqueWarmup(technique({ warmup: true }), false)).toBe(true);
    expect(isTechniqueWarmup(technique({ warmup: false }), false)).toBe(false);
  });
});

describe("allWarmup", () => {
  it("status가 warmup이면 true다", () => {
    expect(allWarmup("warmup", [technique({ warmup: false })])).toBe(true);
  });
  it("기법이 하나도 없으면 true다", () => {
    expect(allWarmup("ready", [])).toBe(true);
  });
  it("모든 기법이 warmup이면 true다", () => {
    expect(allWarmup("ready", [technique({ warmup: true }), technique({ warmup: true })])).toBe(true);
  });
  it("하나라도 warmup이 아니면 false다", () => {
    expect(allWarmup("ready", [technique({ warmup: true }), technique({ warmup: false })])).toBe(false);
  });
});

describe("scoreText2", () => {
  it("소수 2자리·부호를 붙인다", () => {
    expect(scoreText2(0.4213)).toBe("+0.42");
    expect(scoreText2(-0.4213)).toBe("-0.42");
    expect(scoreText2(0)).toBe("+0.00");
  });
  it("결측은 —다", () => {
    expect(scoreText2(null)).toBe("—");
    expect(scoreText2(undefined)).toBe("—");
  });
});
