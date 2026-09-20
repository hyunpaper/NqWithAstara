import { describe, expect, it } from "vitest";
import {
  findSymbolScore,
  normalizeArticlesResponse,
  normalizeNewsHealth,
  normalizeSentimentResponse,
  shouldRenderNewsUi,
} from "./newsTypes";

describe("normalizeArticlesResponse", () => {
  it("opaque 상세 ID와 실제 번역·snapshot 근거 계약을 보존한다", () => {
    const res = normalizeArticlesResponse({ enabled: true, articles: [{
      id: "opaque:save:20260921:abc%2F1", title: "English", titleKo: "한국어 제목", sourceKo: "로이터",
      summaryKo: "한국어 요약", contentKo: "한국어 본문", classificationText: "본문 기준 긍정",
      classificationSource: "body", evidence: [{ id: "opaque:e1", title: "Evidence", titleKo: "근거 기사", sourceKo: "블룸버그", sentiment: "positive", contribution: 1.25, weight: 0.8, createdAt: "2026-09-21T00:00:00Z" }],
      remainingEvidenceCount: 2, remainingContribution: -0.4,
    }] });
    const article = res.articles[0];
    expect(article.id).toBe("opaque:save:20260921:abc%2F1");
    expect(article.contentKo).toBe("한국어 본문");
    expect(article.classificationText).toBe("본문 기준 긍정");
    expect(article.evidence?.[0]).toMatchObject({ id: "opaque:e1", titleKo: "근거 기사", contribution: 1.25 });
    expect(article.remainingEvidenceCount).toBe(2);
  });

  it("알 수 없는 필드를 무시하고 알려진 필드만 남긴다", () => {
    const res = normalizeArticlesResponse({
      enabled: true,
      count: 1,
      articles: [
        {
          id: "a1",
          title: "제목",
          source: "saveticker",
          createdAt: "2026-09-12T00:00:00Z",
          tickers: ["AAPL"],
          matchedSymbols: ["AAPL"],
          symbols: ["AAPL"],
          sentiment: "positive",
          strength: 3,
          reason: "실적 호조",
          model: "qwen2.5:7b-instruct",
          latencyMs: 4000,
          classifiedAt: "2026-09-12T00:00:01Z",
          inputKind: "summary",
          unknownField: "무시되어야 함",
        },
      ],
    });
    expect(res.enabled).toBe(true);
    expect(res.count).toBe(1);
    expect(res.articles).toHaveLength(1);
    expect(res.articles[0]).not.toHaveProperty("unknownField");
    expect(res.articles[0].sentiment).toBe("positive");
  });

  it("id가 없는 기사는 제외한다", () => {
    const res = normalizeArticlesResponse({
      enabled: true,
      count: 1,
      articles: [{ title: "제목만 있음" }],
    });
    expect(res.articles).toHaveLength(0);
  });

  it("알 수 없는 sentiment 값은 unclassified로 정규화한다", () => {
    const res = normalizeArticlesResponse({
      enabled: true,
      count: 1,
      articles: [{ id: "a1", sentiment: "매우좋음" }],
    });
    expect(res.articles[0].sentiment).toBe("unclassified");
  });

  it("결측 필드는 안전한 기본값으로 채운다", () => {
    const res = normalizeArticlesResponse({ enabled: true, articles: [{ id: "a1" }] });
    const a = res.articles[0];
    expect(a.title).toBe("(제목 없음)");
    expect(a.tickers).toEqual([]);
    expect(a.strength).toBeNull();
    expect(a.inputKind).toBeNull();
  });

  it("응답 자체가 비정상(null/문자열)이어도 빈 목록을 반환한다", () => {
    expect(normalizeArticlesResponse(null).articles).toEqual([]);
    expect(normalizeArticlesResponse("garbage").articles).toEqual([]);
    expect(normalizeArticlesResponse(undefined).enabled).toBe(false);
  });
});

describe("normalizeSentimentResponse", () => {
  it("market·symbols를 정규화하고 score 없는 항목은 버린다", () => {
    const res = normalizeSentimentResponse({
      enabled: true,
      asOf: "2026-09-12T00:00:00Z",
      halfLifeMinutes: 240,
      market: { symbol: "MARKET", score: 1.5, count: 10, latestAt: "2026-09-12T00:00:00Z" },
      symbols: [
        { symbol: "AAPL", score: 3.2, count: 4, latestAt: "2026-09-12T00:00:00Z" },
        { symbol: "BAD" },
      ],
    });
    expect(res.market?.symbol).toBe("MARKET");
    expect(res.symbols).toHaveLength(1);
    expect(res.symbols[0].symbol).toBe("AAPL");
  });

  it("market이 null이면 그대로 null을 유지한다", () => {
    const res = normalizeSentimentResponse({ enabled: true, market: null, symbols: [] });
    expect(res.market).toBeNull();
  });

  it("결측 응답에서도 안전하게 기본값을 반환한다", () => {
    const res = normalizeSentimentResponse({});
    expect(res.enabled).toBe(false);
    expect(res.market).toBeNull();
    expect(res.symbols).toEqual([]);
  });
});

describe("normalizeNewsHealth", () => {
  it("ollama는 ok/down만 허용하고 그 외는 null이다", () => {
    expect(normalizeNewsHealth({ enabled: true, ollama: "ok" })?.ollama).toBe("ok");
    expect(normalizeNewsHealth({ enabled: true, ollama: "weird" })?.ollama).toBeNull();
  });

  it("news 필드 자체가 없으면 null이다(구버전 서버)", () => {
    expect(normalizeNewsHealth(undefined)).toBeNull();
    expect(normalizeNewsHealth(null)).toBeNull();
  });
});

describe("shouldRenderNewsUi", () => {
  it("health·sentiment 모두 true여야 렌더한다", () => {
    expect(shouldRenderNewsUi(true, true)).toBe(true);
  });
  it("health가 false/결측이면 렌더하지 않는다", () => {
    expect(shouldRenderNewsUi(false, true)).toBe(false);
    expect(shouldRenderNewsUi(null, true)).toBe(false);
    expect(shouldRenderNewsUi(undefined, true)).toBe(false);
  });
  it("sentiment가 false/결측이면 렌더하지 않는다", () => {
    expect(shouldRenderNewsUi(true, false)).toBe(false);
    expect(shouldRenderNewsUi(true, undefined)).toBe(false);
  });
});

describe("findSymbolScore", () => {
  const symbols = [{ symbol: "AAPL", score: 3, count: 2, latestAt: null }];
  it("대소문자 무시하고 심볼을 찾는다", () => {
    expect(findSymbolScore(symbols, "aapl")?.score).toBe(3);
  });
  it("없는 심볼은 null이다", () => {
    expect(findSymbolScore(symbols, "TSLA")).toBeNull();
  });
});
