import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import NewsDetailPanel from "./NewsDetailPanel";
import type { NewsArticle } from "./newsTypes";

const article = (changes: Partial<NewsArticle> = {}): NewsArticle => ({
  id: "opaque:article/1", title: "삼성전자 실적 발표", source: "SBHNews",
  createdAt: "2026-09-21T00:00:00Z", tickers: [], matchedSymbols: ["AAPL"], symbols: ["AAPL"], sentiment: "positive",
  strength: 2, reason: "본문 호재", model: "test", latencyMs: 1, classifiedAt: null, inputKind: "body", entities: [], ...changes,
});

describe("NewsDetailPanel 표시와 키보드", () => {
  it("원문 제목·출처·본문과 동일 시점 근거를 표시한다", () => {
    render(<NewsDetailPanel article={article({ body: "원문 본문", evidence: [{ id: "e1", title: "근거 기사", sentiment: "positive", strength: 1, weight: 0.5, contribution: 1.2, createdAt: null }] })} onClose={() => undefined} />);
    expect(screen.getByRole("heading", { name: "삼성전자 실적 발표" })).toBeTruthy();
    expect(screen.getByText(/SBHNews/)).toBeTruthy();
    expect(screen.getByText("원문 본문")).toBeTruthy();
    expect(screen.getByText(/근거 기사/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "한국어" })).toBeNull();
    expect(screen.queryByRole("button", { name: "원문" })).toBeNull();
    expect(screen.queryByText(/번역/)).toBeNull();
  });

  it("본문이 없으면 요약을, 둘 다 없으면 안내 문구를 표시한다", () => {
    const view = render(<NewsDetailPanel article={article({ summary: "요약 본문" })} onClose={() => undefined} />);
    expect(screen.getByText("요약 본문")).toBeTruthy();
    view.rerender(<NewsDetailPanel article={article()} onClose={() => undefined} />);
    expect(screen.getByText("기사 본문이 제공되지 않았습니다.")).toBeTruthy();
  });

  it("판정 근거는 원문 분류 입력을 그대로 표시한다", () => {
    const view = render(<NewsDetailPanel article={article({ body: "본문", classificationText: "분류 입력 원문" })} onClose={() => undefined} />);
    expect(screen.getByText(/분류 입력 원문/)).toBeTruthy();
    view.rerender(<NewsDetailPanel article={article({ id: "opaque:article/2", body: "둘째 본문", classificationText: "둘째 분류 입력" })} onClose={() => undefined} />);
    expect(screen.getByText("둘째 본문")).toBeTruthy();
    expect(screen.getByText(/둘째 분류 입력/)).toBeTruthy();
  });

  it("원문 링크는 http와 https만 허용한다", () => {
    const view = render(<NewsDetailPanel article={article({ url: "javascript:alert(1)" })} onClose={() => undefined} />);
    expect(screen.queryByRole("link", { name: "원문 열기" })).toBeNull();
    view.rerender(<NewsDetailPanel article={article({ url: "https://example.com/news" })} onClose={() => undefined} />);
    expect(screen.getByRole("link", { name: "원문 열기" }).getAttribute("href")).toBe("https://example.com/news");
  });

  it("404를 구분하고 Escape로 닫는다", () => {
    const onClose = vi.fn();
    render(<NewsDetailPanel article={article()} state="not-found" onClose={onClose} />);
    expect(screen.getByRole("alert").textContent).toContain("찾을 수 없습니다");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledOnce();
  });
});
