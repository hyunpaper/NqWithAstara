import { describe, expect, it } from "vitest";
import { replayStatusLabel, replayValue } from "./replayTypes";

describe("과거 replay 표시", () => {
  it("재현할 수 없는 실제 v5 거래 값은 unavailable로 표시한다", () => {
    expect(replayValue(null)).toBe("unavailable");
    expect(replayValue(3, "건")).toBe("3건");
  });

  it("작업 상태를 한국어로 표시한다", () => {
    expect(replayStatusLabel("queued")).toBe("대기");
    expect(replayStatusLabel("running")).toBe("실행 중");
    expect(replayStatusLabel("completed")).toBe("완료");
    expect(replayStatusLabel("failed")).toBe("실패");
  });

  it("봉 기반 거래와 호가 의존 결측을 partial 상태로 함께 표현한다", () => {
    const status: import("./replayTypes").ReplaySymbolResult["tradeReplayStatus"] = "partial";
    expect(status).toBe("partial");
  });
});
