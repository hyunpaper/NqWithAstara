import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { WatchRowContent } from "./WatchRowContent";

describe("WatchRowContent", () => {
  it("1행에 티커와 등락률을, 2행에 배지를 둔다", () => {
    const { container } = render(
      <button className="watch-select">
        <WatchRowContent
          symbol="LONGTICKER123"
          name="Very Long Company Name Holdings Incorporated"
          change={{ label: "+12.34%", tone: "up" }}
          badges={[
            <span key="news" className="news-badge neutral">중립 +0.0</span>,
            <span key="confluence" className="confluence-mini-badge neutral">+0.10</span>,
          ]}
        />
      </button>,
    );

    const identity = container.querySelector(".watch-identity");
    const metrics = container.querySelector(".watch-metrics");

    expect(identity?.contains(screen.getByText("LONGTICKER123"))).toBe(true);
    expect(identity?.contains(screen.getByText("+12.34%"))).toBe(true);
    expect(metrics?.contains(screen.getByText("중립 +0.0"))).toBe(true);
    expect(metrics?.contains(screen.getByText("+0.10"))).toBe(true);
  });

  it("기업명을 텍스트로 그리지 않고 title에만 남긴다", () => {
    const { container } = render(
      <WatchRowContent symbol="NVDA" name="NVIDIA Corporation" />,
    );

    expect(container.textContent).toBe("NVDA");
    expect(container.querySelector(".watch-identity")?.getAttribute("title")).toBe(
      "NVIDIA Corporation",
    );
  });

  it("배지가 없으면 2행 컨테이너를 만들지 않는다", () => {
    const { container } = render(
      <WatchRowContent symbol="NVDA" name="NVIDIA" badges={[]} />,
    );

    expect(container.querySelector(".watch-identity")).not.toBeNull();
    expect(container.querySelector(".watch-metrics")).toBeNull();
  });

  it("등락률 하락 톤을 down 클래스로 표시한다", () => {
    const { container } = render(
      <WatchRowContent symbol="PANW" name="Palo Alto" change={{ label: "-2.50%", tone: "down" }} />,
    );

    expect(container.querySelector(".watch-identity .down")?.textContent).toBe("-2.50%");
  });
});
