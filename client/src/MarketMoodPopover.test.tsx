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
      { id: "cpi", kind: "CPI", title: "소비자물가지수", scheduledAt: "2026-09-22T14:30:00Z", status: "published", actual: "2.8", forecast: "2.7", previous: "2.6", unit: "%", impactDirection: "risk_off", bondImpact: "negative", source: "공식 통계", reason: null },
      { id: "fed", kind: "FOMC", title: "연준 의장 연설", scheduledAt: "2026-09-22T18:00:00Z", status: "unpublished", actual: null, forecast: null, previous: null, unit: null, impactDirection: "unknown", bondImpact: "unknown", source: "공식 일정", reason: "아직 발표되지 않았습니다." },
    ],
  },
  policyRates: {
    status: "delayed", checkedAt: "2026-09-22T15:00:00Z", source: "SBHNews 표시값 (원천 미확인)", reason: "갱신이 지연되었습니다.",
    rates: [
      { key: "fed", label: "미 연준", value: 4, previous: 3.75, asOf: "2026-09-16", note: "목표범위", checkedAt: "2026-09-22T15:00:00Z", source: "SBHNews 표시값 (원천 미확인)", delayStatus: "stale", reason: "표시 갱신시각이 24시간을 넘었습니다." },
    ],
  },
  assets: [
    { key: "gold", label: "금", symbol: "GLD", assetKind: "commodity", proxy: true, isAvailable: true, direction: "up", changePercent: 1.25, asOf: "2026-09-22T14:59:00Z", source: "Toss", delayStatus: "fresh", sessionStatus: "open", reason: null, impactSign: -1, contribution: -1.25 },
    { key: "oil", label: "유가", symbol: "USO", assetKind: "commodity", proxy: true, isAvailable: false, direction: "unknown", changePercent: null, asOf: null, source: "Toss", delayStatus: "unavailable", sessionStatus: "closed", reason: "휴장이라 집계에서 제외했습니다.", impactSign: -1, contribution: null },
  ],
};

describe("MarketMoodPopover", () => {
  it("키보드로 근거를 열고 자산 상태와 제한을 설명한다", () => {
    render(<MarketMoodPopover mood={mood} />);
    const trigger = screen.getByRole("button", { name: "시장 분위기 상승 +1.25%" });
    fireEvent.focus(trigger);
    const dialog = screen.getByRole("dialog", { name: "시장 분위기 근거" });
    expect(dialog.textContent).toContain("금 GLD ETF 프록시");
    expect(dialog.textContent).toContain("상승 +1.25% → 시장 악재");
    expect(dialog.textContent).toContain("위험선호 가중 평균 +1.25%");
    expect(dialog.textContent).toContain("휴장이라 집계에서 제외했습니다.");
    expect(dialog.textContent).toContain("발표 2.8%");
    expect(dialog.textContent).toContain("예상 2.7 · 이전 2.6 · 예상 대비 실제 → 국채 영향 부정");
    expect(dialog.textContent).toContain("미발표");
    expect(dialog.textContent).toContain("주요 정책금리 · 지연");
    expect(dialog.textContent).toContain("미 연준 · 목표범위");
    expect(dialog.textContent).toContain("4% · 지연");
    expect(dialog.textContent).toContain("이전 3.75% · 기준일 2026-09-16");
    expect(dialog.textContent).toContain("원천 미확인 참고 데이터이며 시장 분위기 점수에 반영하지 않습니다.");
    expect(dialog.textContent).toContain("무료 시세이며 실시간 틱을 보장하지 않습니다.");
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });
});
