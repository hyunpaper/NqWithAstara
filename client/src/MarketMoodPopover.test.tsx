import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import MarketMoodPopover from "./MarketMoodPopover";
import type { MarketMoodResponse } from "./marketMoodTypes";

const mood: MarketMoodResponse = {
  asOf: "2026-09-22T15:00:00Z",
  status: "partial",
  score: 1.25,
  direction: "up",
  availableCount: 1,
  totalCount: 2,
  aggregation: "equal_weight_available_only",
  source: "Toss Open API · 미국 주식·ETF",
  refreshSeconds: 60,
  limitations: ["무료 시세이며 실시간 틱을 보장하지 않습니다.", "ETF 프록시입니다."],
  evidence: ["gold"],
  assets: [
    { key: "gold", label: "금", symbol: "GLD", assetKind: "commodity", proxy: true, isAvailable: true, direction: "up", changePercent: 1.25, asOf: "2026-09-22T14:59:00Z", source: "Toss", delayStatus: "fresh", sessionStatus: "open", reason: null },
    { key: "oil", label: "유가", symbol: "USO", assetKind: "commodity", proxy: true, isAvailable: false, direction: "unknown", changePercent: null, asOf: null, source: "Toss", delayStatus: "unavailable", sessionStatus: "closed", reason: "휴장이라 집계에서 제외했습니다." },
  ],
};

describe("MarketMoodPopover", () => {
  it("키보드로 근거를 열고 자산 상태와 제한을 설명한다", () => {
    render(<MarketMoodPopover mood={mood} />);
    const trigger = screen.getByRole("button", { name: "시장 분위기 상승 +1.25%" });
    fireEvent.focus(trigger);
    const dialog = screen.getByRole("dialog", { name: "시장 분위기 근거" });
    expect(dialog.textContent).toContain("금 GLD ETF 프록시");
    expect(dialog.textContent).toContain("상승 +1.25%");
    expect(dialog.textContent).toContain("휴장이라 집계에서 제외했습니다.");
    expect(dialog.textContent).toContain("무료 시세이며 실시간 틱을 보장하지 않습니다.");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
});
