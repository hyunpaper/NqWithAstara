import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import NewsDetailPanel from "./NewsDetailPanel";
import type { NewsArticle } from "./newsTypes";

const article = (changes: Partial<NewsArticle> = {}): NewsArticle => ({
  id: "opaque:article/1", title: "English headline", titleKo: "한국어 제목", source: "wire", sourceKo: "로이터",
  createdAt: "2026-09-21T00:00:00Z", tickers: [], matchedSymbols: ["AAPL"], symbols: ["AAPL"], sentiment: "positive",
  strength: 2, reason: "본문 호재", model: "test", latencyMs: 1, classifiedAt: null, inputKind: "body", entities: [], ...changes,
});

describe("NewsDetailPanel 표시와 키보드", () => {
  it("실제 contentKo와 동일 시점 근거를 표시한다", () => {
    render(<NewsDetailPanel article={article({ contentKo: "실제 번역된 본문", contentTranslationStatus: "translated", evidence: [{ id: "e1", title: "Evidence", titleKo: "근거 기사", sentiment: "positive", strength: 1, weight: 0.5, contribution: 1.2, createdAt: null }] })} onClose={() => undefined} />);
    expect(screen.getByText("실제 번역된 본문")).toBeTruthy();
    expect(screen.getByText(/근거 기사/)).toBeTruthy();
    expect(screen.getByText("기사 발췌 번역")).toBeTruthy();
  });

  it("번역이 없을 때 상태와 영문 원문 fallback을 구분해 표시하고 재시도한다", () => {
    const retry = vi.fn();
    render(<NewsDetailPanel article={article({ body: "Original body", contentKo: null, contentTranslationStatus: "failed" })} onRetryTranslation={retry} onClose={() => undefined} />);
    expect(screen.getByText("한국어 번역에 실패했습니다.")).toBeTruthy();
    expect(screen.getAllByText("원문")).toHaveLength(2);
    expect(screen.getByText("Original body")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "번역 요청" }));
    expect(retry).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole("button", { name: "원문" }));
    expect(screen.getByText("Original body")).toBeTruthy();
  });

  it("번역 처리 중에는 중복 요청을 막는다", () => {
    const retry = vi.fn();
    render(<NewsDetailPanel article={article({ body: "Original body", contentTranslationStatus: "pending" })} translationState="queued" onRetryTranslation={retry} onClose={() => undefined} />);
    const button = screen.getByRole("button", { name: "번역 처리 대기 중…" });
    expect(button.hasAttribute("disabled")).toBe(true);
    fireEvent.click(button);
    expect(retry).not.toHaveBeenCalled();
  });

  it("번역 할당량 대기와 본문 미제공을 구분한다", () => {
    const view = render(<NewsDetailPanel article={article({ contentTranslationStatus: "quota_wait" })} onClose={() => undefined} />);
    expect(screen.getByText("번역 할당량이 복구되기를 기다리고 있습니다.")).toBeTruthy();
    expect(screen.getByText("번역 할당량 대기")).toBeTruthy();
    view.rerender(<NewsDetailPanel article={article({ contentTranslationStatus: "not_available" })} onClose={() => undefined} />);
    expect(screen.getByText("번역할 기사 본문이 제공되지 않았습니다.")).toBeTruthy();
    expect(screen.getByText("본문 미제공")).toBeTruthy();
  });

  it("본문이 없으면 요약 번역과 요약 상태를 함께 사용한다", () => {
    render(<NewsDetailPanel article={article({ summary: "Original summary", summaryKo: "한국어 요약", summaryTranslationStatus: "translated", contentTranslationStatus: "not_available" })} onClose={() => undefined} />);
    expect(screen.getByText("한국어 요약")).toBeTruthy();
    expect(screen.getByText("기사 발췌 번역")).toBeTruthy();
  });

  it("다른 기사를 열면 한국어 탭으로 돌아가고 영어 판정 근거를 번역처럼 표시하지 않는다", () => {
    const view = render(<NewsDetailPanel article={article({ body: "Original body", contentKo: "첫 번역", classificationText: "Raw classification" })} onClose={() => undefined} />);
    fireEvent.click(screen.getByRole("button", { name: "원문" }));
    expect(screen.getByText(/Raw classification/)).toBeTruthy();
    view.rerender(<NewsDetailPanel article={article({ id: "opaque:article/2", body: "Second original", contentKo: "둘째 번역", classificationText: "Second raw" })} onClose={() => undefined} />);
    expect(screen.getByText("둘째 번역")).toBeTruthy();
    expect(screen.queryByText("Second raw")).toBeNull();
    expect(screen.getByRole("button", { name: "한국어" }).getAttribute("aria-pressed")).toBe("true");
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
