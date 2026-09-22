import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import NewsPanel from "./NewsPanel";

describe("NewsPanel 기사 선택", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("한국어 제목을 표시하고 기사 행과 판정 근거를 연결해 상세 선택을 전달한다", async () => {
    const body = {
      enabled: true,
      count: 1,
      articles: [{
        id: "opaque:article/1",
        title: "English headline",
        titleKo: "한국어 제목",
        source: "wire",
        sourceKo: "로이터",
        createdAt: "2026-09-21T00:00:00Z",
        sentiment: "positive",
        strength: 2,
        reason: "본문 호재",
        inputKind: "body",
      }],
    };
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify(body), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    })));
    const onSelectArticle = vi.fn();
    render(<NewsPanel symbol="AAPL" onSelectArticle={onSelectArticle} />);

    const row = await screen.findByRole("button", { name: "한국어 제목 상세 보기" });
    const evidenceId = row.getAttribute("aria-describedby");
    expect(evidenceId).toBeTruthy();
    expect(document.getElementById(evidenceId!)).toBeTruthy();
    fireEvent.click(row);
    expect(onSelectArticle).toHaveBeenCalledWith(expect.objectContaining({ id: "opaque:article/1", titleKo: "한국어 제목" }), row);
  });
});
