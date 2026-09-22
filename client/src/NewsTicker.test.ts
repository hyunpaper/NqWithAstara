import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { createElement } from "react";
import { aggregateTickerArticles } from "./NewsTicker";
import type { NewsArticle } from "./newsTypes";
import rawCss from "./styles.css?raw";
vi.mock("./useVisiblePolling", () => ({ useVisiblePolling: () => undefined }));

const css = rawCss.replace(/\r\n/g, "\n");

const article = (id: string, sentiment: NewsArticle["sentiment"], createdAt: string): NewsArticle => ({ id, title: id, source: "test", createdAt, tickers: [], matchedSymbols: [], symbols: [], sentiment, strength: 1, reason: "", model: "", latencyMs: 0, classifiedAt: createdAt, inputKind: "headline", entities: [] });

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

  it("기사 0건에서도 상태 문구가 있는 티커 section을 렌더링한다", async () => {
    const { default: NewsTicker } = await import("./NewsTicker");
    render(createElement(NewsTicker));
    expect(screen.getByRole("region", { name: "전체 뉴스 감성 티커" })).toBeTruthy();
    expect(screen.getByText("뉴스 상태: 대기")).toBeTruthy();
  });

  it("반복 트랙을 220초에 이동하고 마우스·키보드 정지와 동작 축소를 유지한다", () => {
    expect(css).toContain("animation: news-ticker-scroll 220s linear infinite");
    expect(css).toContain(".news-ticker-viewport:hover .news-ticker-track, .news-ticker-viewport:focus-within .news-ticker-track { animation-play-state: paused; }");
    expect(css).toContain("@media (prefers-reduced-motion: reduce) { .news-ticker-track { animation: none; } }");
    expect(css).not.toContain("animation: news-ticker-scroll 110s linear infinite");
  });
});
