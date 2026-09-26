import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import NewsDetailPanel from "./NewsDetailPanel";
import type { NewsArticle } from "./newsTypes";
import { useNewsDetail } from "./useNewsDetail";

const article = (id: string, title = id): NewsArticle => ({ id, title, source: null, createdAt: null, tickers: [], matchedSymbols: [], symbols: [], sentiment: "neutral", strength: null, reason: null, model: null, latencyMs: null, classifiedAt: null, inputKind: null, entities: [] });
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; };

function Harness() {
  const detail = useNewsDetail();
  return <div>
    <button onClick={(event) => detail.open(article("opaque:a/1", "첫 기사"), event.currentTarget, "AAPL")}>첫 기사</button>
    <button onClick={(event) => detail.open(article("opaque:b/2", "둘째 기사"), event.currentTarget, "MSFT")}>둘째 기사</button>
    {detail.selected && <NewsDetailPanel article={detail.detail ?? detail.selected} state={detail.state} onClose={detail.close} />}
  </div>;
}

describe("useNewsDetail 요청 생명주기", () => {
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

  it("불투명 ID와 종목을 질의로 인코딩하고 닫은 뒤 원래 배지에 초점을 복원한다", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response({ ...article("opaque:a/1"), contentKo: "상세 번역", contentTranslationStatus: "translated" })));
    render(<Harness />);
    const opener = screen.getByRole("button", { name: "첫 기사" });
    fireEvent.click(opener);
    await screen.findByText("상세 번역");
    expect(fetch).toHaveBeenCalledWith("/api/news/detail?id=opaque%3Aa%2F1&symbol=AAPL", expect.anything());
    fireEvent.click(screen.getByRole("button", { name: "뉴스 상세 닫기" }));
    await waitFor(() => expect(document.activeElement).toBe(opener));
  });

  it("늦게 도착한 이전 기사 응답이 새 선택을 덮지 않는다", async () => {
    const first = deferred<Response>();
    const second = deferred<Response>();
    const fetchMock = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    vi.stubGlobal("fetch", fetchMock);
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "첫 기사" }));
    const firstSignal = fetchMock.mock.calls[0][1].signal as AbortSignal;
    fireEvent.click(screen.getByRole("button", { name: "둘째 기사" }));
    expect(firstSignal.aborted).toBe(true);
    await act(async () => second.resolve(response({ ...article("opaque:b/2"), contentKo: "둘째 상세", contentTranslationStatus: "translated" })));
    expect(await screen.findByText("둘째 상세")).toBeTruthy();
    await act(async () => first.resolve(response({ ...article("opaque:a/1"), contentKo: "늦은 첫 상세", contentTranslationStatus: "translated" })));
    expect(screen.queryByText("늦은 첫 상세")).toBeNull();
  });

  it("404 상태를 표시하고 컴포넌트 해제 시 진행 요청을 취소한다", async () => {
    const pending = deferred<Response>();
    const fetchMock = vi.fn().mockResolvedValueOnce(response({}, 404)).mockReturnValueOnce(pending.promise);
    vi.stubGlobal("fetch", fetchMock);
    const view = render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "첫 기사" }));
    expect(await screen.findByText(/상세를 찾을 수 없습니다/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "둘째 기사" }));
    const signal = fetchMock.mock.calls[1][1].signal as AbortSignal;
    view.unmount();
    expect(signal.aborted).toBe(true);
  });

  it("상세에 번역 본문이 없으면 한 번만 번역을 요청하고 재조회 결과를 표시한다", async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === "POST") return Promise.resolve(new Response(null, { status: 202 }));
      const detailCalls = fetchMock.mock.calls.filter(([calledUrl, calledInit]) => String(calledUrl).startsWith("/api/news/detail") && !calledInit?.method).length;
      return Promise.resolve(response({ ...article("opaque:a/1"), body: "Original body", contentKo: detailCalls > 1 ? "재조회 번역 본문" : null, contentTranslationStatus: detailCalls > 1 ? "translated" : "failed" }));
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "첫 기사" }));
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(1);
    expect(screen.getByText("Original body")).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(800); });
    expect(screen.getByText("재조회 번역 본문")).toBeTruthy();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(1);
  });

  it("이미 번역 대기 중이면 중복 요청 없이 상세만 재조회한다", async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === "POST") return Promise.resolve(new Response(null, { status: 202 }));
      const detailCalls = fetchMock.mock.calls.filter(([calledUrl, calledInit]) => String(calledUrl).startsWith("/api/news/detail") && !calledInit?.method).length;
      return Promise.resolve(response({ ...article("opaque:a/1"), body: "Original body", contentKo: detailCalls > 1 ? "대기 후 번역 본문" : null, contentTranslationStatus: detailCalls > 1 ? "translated" : "pending" }));
    });
    vi.stubGlobal("fetch", fetchMock);
    render(<Harness />);
    fireEvent.click(screen.getByRole("button", { name: "첫 기사" }));
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === "POST")).toHaveLength(0);
    await act(async () => { await vi.advanceTimersByTimeAsync(800); });
    expect(screen.getByText("대기 후 번역 본문")).toBeTruthy();
  });
});
