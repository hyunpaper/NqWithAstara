import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ConfluenceBadgePopover } from "./ConfluenceTooltip";
import type { ConfluenceContributor, ConfluenceSummary } from "./confluenceTypes";

const contributor = (over: Partial<ConfluenceContributor> = {}): ConfluenceContributor => ({
  name: "RSI",
  score: 0.5,
  contribution: 0.3,
  evidence: {},
  ...over,
});

const summary = (over: Partial<ConfluenceSummary> = {}): ConfluenceSummary => ({
  score: 0.4,
  warmupCount: 0,
  weightsVersion: "uniform.1",
  barEnd: null,
  top: [],
  bottom: [],
  ...over,
});

describe("ConfluenceBadgePopover", () => {
  it("배지 숫자를 보여주고 호버 전에는 툴팁을 숨긴다", () => {
    render(<ConfluenceBadgePopover symbol="AAPL" score={0.42} summary={summary()} />);
    expect(screen.getByRole("button").textContent).toBe("+0.42");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("호버 시 상승 기여와 하락 요인을 부호별로 보여준다", () => {
    const s = summary({
      top: [contributor({ name: "VWAP_DEVIATION", score: 0.8, contribution: 0.8, evidence: { deviation: 1.2 } })],
      bottom: [contributor({ name: "RSI", score: -0.5, contribution: -0.5, evidence: { rsi: 74 } })],
    });
    render(<ConfluenceBadgePopover symbol="AAPL" score={0.3} summary={s} />);
    fireEvent.mouseEnter(screen.getByRole("button").parentElement!);
    const dialog = screen.getByRole("dialog");
    expect(dialog.textContent).toContain("상승 기여");
    expect(dialog.textContent).toContain("하락 요인");
    expect(dialog.textContent).toContain("VWAP 위 1.2σ");
    expect(dialog.textContent).toContain("RSI 74 과열");
    expect(dialog.textContent).toContain("+0.80");
    expect(dialog.textContent).toContain("-0.50");
  });

  it("기여 요소가 없으면 데이터 없음을 보여준다", () => {
    render(<ConfluenceBadgePopover symbol="AAPL" score={0.05} summary={summary()} />);
    fireEvent.mouseEnter(screen.getByRole("button").parentElement!);
    expect(screen.getByRole("dialog").textContent).toContain("기여 요소 데이터 없음");
  });

  it("evidence가 결측이면 해당 값을 데이터 없음으로 적는다", () => {
    const s = summary({ top: [contributor({ name: "RSI", score: 0.4, contribution: 0.4, evidence: {} })] });
    render(<ConfluenceBadgePopover symbol="AAPL" score={0.4} summary={s} />);
    fireEvent.mouseEnter(screen.getByRole("button").parentElement!);
    expect(screen.getByRole("dialog").textContent).toContain("데이터 없음");
  });

  it("키보드 포커스로 툴팁을 연다", () => {
    render(<ConfluenceBadgePopover symbol="AAPL" score={0.3} summary={summary({ top: [contributor()] })} />);
    fireEvent.focus(screen.getByRole("button"));
    expect(screen.queryByRole("dialog")).not.toBeNull();
  });
});
