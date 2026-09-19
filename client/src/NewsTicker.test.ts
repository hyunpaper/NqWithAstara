import { describe, expect, it } from "vitest";
import { aggregateTickerArticles } from "./NewsTicker";
import type { NewsArticle } from "./newsTypes";

const article = (id: string, sentiment: NewsArticle["sentiment"], createdAt: string): NewsArticle => ({ id, title: id, source: "test", createdAt, tickers: [], matchedSymbols: [], symbols: [], sentiment, strength: 1, reason: "", model: "", latencyMs: 0, classifiedAt: createdAt, inputKind: "headline" });

describe("NewsTicker", () => {
  it("최근 24시간 기사만 감성별로 집계한다", () => {
    const result = aggregateTickerArticles([
      article("p", "positive", "2026-09-20T00:00:00Z"),
      article("n", "negative", "2026-09-19T12:00:00Z"),
      article("old", "neutral", "2026-09-18T00:00:00Z"),
    ], Date.parse("2026-09-20T12:00:00Z"));
    expect(result.recent.map((x) => x.id)).toEqual(["p", "n"]);
    expect(result.counts).toEqual({ positive: 1, negative: 1 });
  });
});
