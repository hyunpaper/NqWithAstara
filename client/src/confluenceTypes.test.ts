import { describe, expect, it } from "vitest";
import {
  normalizeConfluenceResponse,
  normalizeConfluenceSummary,
  normalizeTechnique,
} from "./confluenceTypes";

describe("normalizeConfluenceResponse", () => {
  it("알 수 없는 필드를 무시하고 알려진 필드만 남긴다", () => {
    const res = normalizeConfluenceResponse({
      symbol: "AAPL",
      status: "ready",
      policyHash: "abc123",
      weightsVersion: "uniform.1",
      barEnd: "2026-09-12T13:31:00Z",
      score: 0.42,
      warmupCount: 2,
      techniques: [
        {
          name: "MACD",
          score: 0.5,
          confidence: 1,
          weight: 1,
          warmup: false,
          contributing: true,
          correlationGroup: null,
          evidence: { histogram: 0.1 },
          unknownField: "무시되어야 함",
        },
      ],
      unknownTop: "무시되어야 함",
    });
    expect(res.symbol).toBe("AAPL");
    expect(res.status).toBe("ready");
    expect(res.score).toBe(0.42);
    expect(res.warmupCount).toBe(2);
    expect(res.techniques).toHaveLength(1);
    expect(res.techniques[0]).not.toHaveProperty("unknownField");
    expect(res.techniques[0].name).toBe("MACD");
  });

  it("결측 응답에서도 안전한 기본값을 반환한다", () => {
    const res = normalizeConfluenceResponse({});
    expect(res.symbol).toBe("");
    expect(res.status).toBe("warmup");
    expect(res.score).toBeNull();
    expect(res.warmupCount).toBe(0);
    expect(res.techniques).toEqual([]);
  });

  it("응답 자체가 비정상(null/문자열)이어도 안전한 기본값을 반환한다", () => {
    expect(normalizeConfluenceResponse(null).techniques).toEqual([]);
    expect(normalizeConfluenceResponse("garbage").status).toBe("warmup");
    expect(normalizeConfluenceResponse(undefined).warmupCount).toBe(0);
  });

  it("status가 ready/warmup이 아니면 warmup으로 취급한다", () => {
    expect(normalizeConfluenceResponse({ status: "weird" }).status).toBe("warmup");
  });
});

describe("normalizeTechnique", () => {
  it("name이 없으면 제외한다", () => {
    expect(normalizeTechnique({ score: 0.1 })).toBeNull();
  });

  it("evidence의 숫자 아닌 값은 null로 정규화한다", () => {
    const t = normalizeTechnique({ name: "RSI", evidence: { rsi: 55, adx: "NaN문자열" } });
    expect(t?.evidence.rsi).toBe(55);
    expect(t?.evidence.adx).toBeNull();
  });

  it("evidence가 없으면 빈 객체다", () => {
    expect(normalizeTechnique({ name: "RSI" })?.evidence).toEqual({});
  });
});

describe("normalizeConfluenceSummary", () => {
  it("알려진 필드만 남기고 top·bottom 기여를 정규화한다", () => {
    const s = normalizeConfluenceSummary({
      score: -0.1,
      warmupCount: 3,
      weightsVersion: "uniform.1",
      barEnd: "2026-09-12T00:31:00Z",
      top: [{ name: "VWAP_DEVIATION", score: 0.8, contribution: 0.3, evidence: { deviation: 1.2 } }],
      bottom: [{ name: "RSI", score: -0.5, contribution: -0.2, evidence: { rsi: 74 } }],
      extra: 1,
    });
    expect(s).toEqual({
      score: -0.1,
      warmupCount: 3,
      weightsVersion: "uniform.1",
      barEnd: "2026-09-12T00:31:00Z",
      top: [{ name: "VWAP_DEVIATION", score: 0.8, contribution: 0.3, evidence: { deviation: 1.2 } }],
      bottom: [{ name: "RSI", score: -0.5, contribution: -0.2, evidence: { rsi: 74 } }],
    });
  });

  it("top·bottom이 없으면 빈 배열이고 이름 없는 기여는 버린다", () => {
    const s = normalizeConfluenceSummary({
      score: 0.2,
      warmupCount: 0,
      weightsVersion: "uniform.1",
      barEnd: null,
      top: [{ score: 0.5, contribution: 0.5, evidence: {} }],
    });
    expect(s?.top).toEqual([]);
    expect(s?.bottom).toEqual([]);
  });

  it("결측이면 null이다", () => {
    expect(normalizeConfluenceSummary(null)).toBeNull();
    expect(normalizeConfluenceSummary(undefined)).toBeNull();
  });
});
