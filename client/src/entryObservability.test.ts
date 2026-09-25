import { describe, expect, it } from "vitest";
import { dataDelayLabel, entryDispositionLabel, entryReasonLabel } from "./entryObservability";

describe("진입 관측 표시", () => {
  it("거래가 없어도 후보 없음과 첫 gate를 표시한다", () => {
    expect(entryDispositionLabel("NO_CANDIDATE")).toBe("후보 없음");
    expect(entryReasonLabel("STALE_QUOTE")).toBe("시세 지연");
  });

  it("데이터 지연을 초와 분으로 구분한다", () => {
    expect(dataDelayLabel(25)).toBe("25초");
    expect(dataDelayLabel(95)).toBe("1.6분");
    expect(dataDelayLabel(null)).toBe("시각 없음");
  });
});
