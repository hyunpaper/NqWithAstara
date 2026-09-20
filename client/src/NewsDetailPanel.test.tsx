import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import NewsDetailPanel from "./NewsDetailPanel";
import type { NewsArticle } from "./newsTypes";

const article: NewsArticle = {
  id: "opaque:article/1", title: "English headline", titleKo: "한국어 제목", source: "wire", sourceKo: "로이터",
  createdAt: "2026-09-21T00:00:00Z", tickers: [], matchedSymbols: ["AAPL"], symbols: ["AAPL"], sentiment: "positive",
  strength: 2, reason: "본문 호재", model: "test", latencyMs: 1, classifiedAt: null, inputKind: "body", entities: [],
  translationStatus: "translated",
};

describe("NewsDetailPanel 계약 상호작용", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({
      id: article.id, title: article.title, titleKo: article.titleKo, source: article.source, sourceKo: article.sourceKo,
      sentiment: "positive", inputKind: "body", translationStatus: "translated", contentKo: "실제 번역된 본문",
      content: "Original body", evidence: [{ id: "opaque:evidence/1", title: "Evidence", titleKo: "근거 기사", sentiment: "positive", contribution: 1.2, createdAt: article.createdAt }],
    }), { status: 200, headers: { "Content-Type": "application/json" } })));
  });
  afterEach(() => vi.unstubAllGlobals());

  it("opaque ID로 단일 evidence 요청을 하고 contentKo와 snapshot 근거를 표시한다", async () => {
    render(<NewsDetailPanel article={article} onClose={() => undefined} />);
    await waitFor(() => expect(screen.getByText("실제 번역된 본문")).toBeTruthy());
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(fetch).toHaveBeenCalledWith(`/api/news/${encodeURIComponent(article.id)}/evidence`, expect.anything());
    expect(screen.getByText(/근거 기사/)).toBeTruthy();
  });

  it("원문 버튼은 실제 원문을 표시한다", async () => {
    render(<NewsDetailPanel article={article} onClose={() => undefined} />);
    await waitFor(() => expect(screen.getByText("실제 번역된 본문")).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "원문" }));
    expect(screen.getByText("Original body")).toBeTruthy();
  });

  it("Escape는 닫기 콜백을 호출한다", () => {
    const onClose = vi.fn();
    render(<NewsDetailPanel article={article} onClose={onClose} />);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(onClose).toHaveBeenCalledOnce();
  });
});
