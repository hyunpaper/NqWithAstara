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
  economicCalendar: {
    marketDate: "2026-09-22", timeZone: "America/New_York", status: "partial", source: "검증 제공자", asOf: "2026-09-22T15:00:00Z", reason: null,
    events: [
      { id: "cpi", title: "소비자물가지수", scheduledAt: "2026-09-22T14:30:00Z", status: "published", actual: "2.8", forecast: "2.7", previous: "2.6", unit: "%", impactDirection: "risk_off", source: "공식 통계", reason: null },
      { id: "fed", title: "연준 의장 연설", scheduledAt: "2026-09-22T18:00:00Z", status: "unpublished", actual: null, forecast: null, previous: null, unit: null, impactDirection: "unknown", source: "공식 일정", reason: "아직 발표되지 않았습니다." },
    ],
  },
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
    expect(dialog.textContent).toContain("발표 2.8%");
    expect(dialog.textContent).toContain("예상 2.7 · 이전 2.6 · 영향 risk_off");
    expect(dialog.textContent).toContain("미발표");
    expect(dialog.textContent).toContain("무료 시세이며 실시간 틱을 보장하지 않습니다.");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
});
