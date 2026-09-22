import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { NewsScorePopover } from "./NewsEvidenceTooltip";
import type { NewsSymbolScore } from "./newsTypes";
import { WatchList } from "./WatchRowContent";

const score: NewsSymbolScore = {
  symbol: "AAPL", score: 3, count: 2, latestAt: "2026-09-21T00:00:00Z", evidence: [
    { id: "opaque:e/1", title: "Evidence", titleKo: "근거 기사", sentiment: "positive", strength: 2, weight: 0.8, contribution: 2.4, createdAt: "2026-09-21T00:00:00Z" },
  ], remainingEvidenceCount: 1, remainingContribution: 0.6, remainingWeight: 0.2, snapshotId: "snap-1", asOf: "2026-09-21T00:00:00Z", totalWeight: 1,
};

describe("NewsScorePopover 접근성", () => {
  afterEach(() => vi.useRealTimers());

  it("키보드 초점과 터치 선택으로 열리고 Escape로 배지 초점을 복원한다", () => {
    render(<NewsScorePopover score={score} label="호재 +3.0" className="news-badge positive" onSelectEvidence={() => undefined} />);
    const trigger = screen.getByRole("button", { name: "호재 +3.0" });
    fireEvent.focus(trigger);
    expect(screen.getByRole("dialog", { name: "AAPL 뉴스 점수 근거" })).toBeTruthy();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(trigger);
    fireEvent.pointerDown(trigger, { pointerType: "touch", pointerId: 1 });
    fireEvent.focus(trigger);
    fireEvent.click(trigger);
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  it("동일 시점 기사 선택 시 해당 근거와 배지를 전달한다", () => {
    const onSelect = vi.fn();
    render(<NewsScorePopover score={score} label="호재 +3.0" className="news-badge positive" onSelectEvidence={onSelect} />);
    const trigger = screen.getByRole("button", { name: "호재 +3.0" });
    fireEvent.click(trigger);
    fireEvent.click(screen.getByRole("button", { name: /근거 기사/ }));
    expect(onSelect).toHaveBeenCalledWith(score.evidence![0], trigger);
  });

  it("마우스를 올려 열고 근거 창으로 포인터를 옮겨도 닫히지 않는다", () => {
    vi.useFakeTimers();
    render(<NewsScorePopover score={score} label="호재 +3.0" className="news-badge positive" onSelectEvidence={() => undefined} />);
    const trigger = screen.getByRole("button", { name: "호재 +3.0" });
    fireEvent.mouseEnter(trigger.parentElement!);
    const dialog = screen.getByRole("dialog", { name: "AAPL 뉴스 점수 근거" });
    fireEvent.mouseLeave(trigger.parentElement!);
    fireEvent.mouseEnter(dialog);
    act(() => vi.advanceTimersByTime(150));
    expect(screen.getByRole("dialog", { name: "AAPL 뉴스 점수 근거" })).toBeTruthy();
    fireEvent.mouseLeave(dialog);
    act(() => vi.advanceTimersByTime(150));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("관심종목 점수 근거 조작이 종목 선택이나 드래그 저장을 시작하지 않는다", () => {
    const onSelect = vi.fn();
    const saveOrder = vi.fn(async () => undefined);
    render(<WatchList items={[{ symbol: "AAPL", name: "Apple", change: null, badges: [<NewsScorePopover key="news" score={score} label="호재 +3.0" className="news-badge positive" onSelectEvidence={() => undefined} />] }]} selected="AAPL" onSelect={onSelect} onDelete={() => undefined} saveOrder={saveOrder} onError={() => undefined} />);
    const trigger = screen.getByRole("button", { name: "호재 +3.0" });
    fireEvent.pointerDown(trigger, { pointerType: "touch", pointerId: 1 });
    fireEvent.pointerUp(trigger, { pointerType: "touch", pointerId: 1 });
    fireEvent.click(trigger);
    expect(onSelect).not.toHaveBeenCalled();
    expect(saveOrder).not.toHaveBeenCalled();
  });
});
