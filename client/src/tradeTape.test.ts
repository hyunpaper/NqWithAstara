import { describe, expect, it } from "vitest";
import { blockTradeLabel, flowSourceLabel } from "./tradeTape";

describe("flowSourceLabel", () => {
  it("웹소켓 틱과 REST 보정을 구분한다", () => {
    expect(flowSourceLabel("ws")).toBe("실시간 틱");
    expect(flowSourceLabel("rest")).toBe("체결 내역 보정");
  });

  it("표본이 없거나 모르는 값은 표본 없음이다", () => {
    expect(flowSourceLabel("none")).toBe("표본 없음");
    expect(flowSourceLabel(undefined)).toBe("표본 없음");
    expect(flowSourceLabel(null)).toBe("표본 없음");
    expect(flowSourceLabel("legacy")).toBe("표본 없음");
  });
});

describe("blockTradeLabel", () => {
  it("건수에 방향 없음을 함께 적는다", () => {
    expect(blockTradeLabel(0)).toBe("0건 (방향 없음)");
    expect(blockTradeLabel(3)).toBe("3건 (방향 없음)");
  });

  it("미수집은 대시로 표시한다", () => {
    expect(blockTradeLabel(null)).toBe("—");
    expect(blockTradeLabel(undefined)).toBe("—");
  });
});
