import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { WatchRowContent } from "./App";

describe("WatchRowContent", () => {
  it("긴 티커와 판단 배지를 서로 다른 레이아웃 영역에 둔다", () => {
    const { container } = render(
      <button className="watch-select">
        <WatchRowContent symbol="LONGTICKER123" name="Very Long Company Name Holdings Incorporated">
          <span className="news-badge neutral">중립 +0.0</span>
          <span className="confluence-mini-badge neutral">+0.10</span>
          <span className="up">+12.34%</span>
        </WatchRowContent>
      </button>,
    );

    const identity = container.querySelector(".watch-identity");
    const metrics = container.querySelector(".watch-metrics");

    expect(identity).not.toBeNull();
    expect(metrics).not.toBeNull();
    expect(identity?.contains(screen.getByText("LONGTICKER123"))).toBe(true);
    expect(identity?.contains(screen.getByText("Very Long Company Name Holdings Incorporated"))).toBe(true);
    expect(metrics?.contains(screen.getByText("중립 +0.0"))).toBe(true);
    expect(metrics?.contains(screen.getByText("+0.10"))).toBe(true);
    expect(metrics?.contains(screen.getByText("+12.34%"))).toBe(true);
  });

  it("배지가 없어도 티커 영역만 렌더한다", () => {
    const { container } = render(<WatchRowContent symbol="NVDA" name="NVIDIA" />);

    expect(container.querySelector(".watch-identity")).not.toBeNull();
    expect(container.querySelector(".watch-metrics")).toBeNull();
    expect(screen.getByText("NVDA").textContent).toBe("NVDA");
  });

  it("좁은 폭 행에서도 긴 티커와 배지 묶음의 레이아웃 계약을 유지한다", () => {
    const { container } = render(
      <div className="watch-row" style={{ width: 180 }}>
        <button className="watch-select">
          <WatchRowContent symbol="SUPERLONGTICKER" name="Responsive Width Regression">
            <span className="news-badge negative">악재 -2.5</span>
          </WatchRowContent>
        </button>
      </div>,
    );

    const select = container.querySelector(".watch-select");
    const identity = container.querySelector(".watch-identity");
    const metrics = container.querySelector(".watch-metrics");

    expect(select?.contains(identity)).toBe(true);
    expect(select?.contains(metrics)).toBe(true);
    expect(identity?.contains(screen.getByText("SUPERLONGTICKER"))).toBe(true);
    expect(metrics?.contains(screen.getByText("악재 -2.5"))).toBe(true);
  });
});
