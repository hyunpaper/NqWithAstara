import { describe, expect, it } from "vitest";
import type { StructureSummaryRow } from "./structureTypes";
import {
  compareByEntryQuality,
  compareBySymbol,
  compareByTrendDirection,
  compareByTrendStrength,
  compareByV5State,
  descNullsLast,
  resolveSortKey,
  sortKeysForMode,
  v5StatePriority,
} from "./structureSort";

const row = (symbol: string, over: Partial<StructureSummaryRow> = {}): StructureSummaryRow => ({
  symbol,
  ...over,
});

describe("v5StatePriority", () => {
  it("READY·진입됨=0 → 대기=1 → 거절/무효화/만료=2 → 상태 없음=3", () => {
    expect(v5StatePriority("READY")).toBe(0);
    expect(v5StatePriority("ENTERED")).toBe(0);
    expect(v5StatePriority("WAIT")).toBe(1);
    expect(v5StatePriority("REJECTED")).toBe(2);
    expect(v5StatePriority("INVALIDATED")).toBe(2);
    expect(v5StatePriority("EXPIRED")).toBe(2);
    expect(v5StatePriority(null)).toBe(3);
    expect(v5StatePriority("SOMETHING_NEW")).toBe(3);
  });
  it("대소문자를 가리지 않는다", () => {
    expect(v5StatePriority("ready")).toBe(0);
  });
});

describe("descNullsLast", () => {
  it("결측(null/NaN)은 항상 마지막 — 음수도 결측보다 앞이다(0으로 대체하지 않는다)", () => {
    expect(descNullsLast(-10, null)).toBeLessThan(0);
    expect(descNullsLast(null, -10)).toBeGreaterThan(0);
    expect(descNullsLast(Number.NaN, 1)).toBeGreaterThan(0);
    expect(descNullsLast(null, null)).toBe(0);
  });
  it("값이 둘 다 있으면 내림차순", () => {
    expect(descNullsLast(70, 30)).toBeLessThan(0);
  });
});

describe("compareByV5State (active/shadow 기본 정렬)", () => {
  it("상태 → 진입 품질 ↓ → |추세| ↓ → symbol 순서로 비교한다", () => {
    const rows = [
      row("EEE"), // 상태 없음 → 마지막
      row("DDD", { candidateState: "REJECTED", entryQuality: 90 }), // 거절 그룹은 품질이 높아도 뒤
      row("BBB", { candidateState: "READY", entryQuality: 40, signedTrend: -80 }),
      row("AAA", { candidateState: "READY", entryQuality: 61.2, signedTrend: 10 }),
      row("CCC", { candidateState: "WAIT", entryQuality: null }),
    ];
    expect([...rows].sort(compareByV5State).map((x) => x.symbol)).toEqual([
      "AAA",
      "BBB",
      "CCC",
      "DDD",
      "EEE",
    ]);
  });

  it("품질 동률이면 |SignedTrend|(절대값 45 > 20)가 큰 쪽이 앞이고, 그마저 같으면 symbol 오름차순(결정성)", () => {
    const rows = [
      row("BBB", { candidateState: "READY", entryQuality: 50, signedTrend: 20 }),
      row("AAA", { candidateState: "READY", entryQuality: 50, signedTrend: -45 }),
      row("CCC", { candidateState: "READY", entryQuality: 50, signedTrend: 20 }),
    ];
    expect([...rows].sort(compareByV5State).map((x) => x.symbol)).toEqual(["AAA", "BBB", "CCC"]);
  });

  it("진입 품질 결측(미평가)은 같은 상태 그룹 안에서 항상 마지막이다", () => {
    const rows = [
      row("AAA", { candidateState: "READY", entryQuality: null }),
      row("BBB", { candidateState: "READY", entryQuality: 1 }),
    ];
    expect([...rows].sort(compareByV5State).map((x) => x.symbol)).toEqual(["BBB", "AAA"]);
  });

  it("READY 없음: 전 종목 대기·거절이어도 상태 그룹만으로 결정적으로 정렬된다", () => {
    const rows = [
      row("CCC", { candidateState: "REJECTED" }),
      row("BBB", { candidateState: "WAIT" }),
      row("AAA", { candidateState: "WAIT" }),
    ];
    expect([...rows].sort(compareByV5State).map((x) => x.symbol)).toEqual(["AAA", "BBB", "CCC"]);
  });
});

describe("compareByTrendStrength", () => {
  it("|SignedTrend| 내림차순 — 방향이 아니라 크기다. 결측은 마지막", () => {
    const rows = [
      row("BBB", { signedTrend: 30 }),
      row("AAA", { signedTrend: -70 }),
      row("CCC", { signedTrend: null }),
    ];
    expect([...rows].sort(compareByTrendStrength).map((x) => x.symbol)).toEqual([
      "AAA",
      "BBB",
      "CCC",
    ]);
  });
});

describe("compareByTrendDirection", () => {
  it("SignedTrend 부호 있는 값 내림차순 — 상승이 위, 하락이 아래. 결측은 마지막", () => {
    const rows = [
      row("BBB", { signedTrend: 30 }),
      row("AAA", { signedTrend: -70 }),
      row("CCC", { signedTrend: null }),
    ];
    expect([...rows].sort(compareByTrendDirection).map((x) => x.symbol)).toEqual([
      "BBB",
      "AAA",
      "CCC",
    ]);
  });

  it("음수 추세에서 추세 강도(절대값)와 실제로 다른 순서를 만든다", () => {
    const rows = [
      row("AAA", { signedTrend: -80 }),
      row("BBB", { signedTrend: 20 }),
    ];
    expect([...rows].sort(compareByTrendStrength).map((x) => x.symbol)).toEqual(["AAA", "BBB"]);
    expect([...rows].sort(compareByTrendDirection).map((x) => x.symbol)).toEqual(["BBB", "AAA"]);
  });

  it("동률이면 symbol 오름차순", () => {
    const rows = [
      row("BBB", { signedTrend: 10 }),
      row("AAA", { signedTrend: 10 }),
    ];
    expect([...rows].sort(compareByTrendDirection).map((x) => x.symbol)).toEqual(["AAA", "BBB"]);
  });
});

describe("compareByEntryQuality", () => {
  it("EntryQuality 내림차순 — 결측은 마지막", () => {
    const rows = [
      row("AAA", { entryQuality: 40 }),
      row("BBB", { entryQuality: 90 }),
      row("CCC", { entryQuality: null }),
    ];
    expect([...rows].sort(compareByEntryQuality).map((x) => x.symbol)).toEqual([
      "BBB",
      "AAA",
      "CCC",
    ]);
  });

  it("candidateState와 무관하다 — 상태 정렬(compareByV5State)과 실제로 다른 순서를 만든다", () => {
    const rows = [
      row("HOT", { candidateState: "REJECTED", entryQuality: 90 }),
      row("COLD", { candidateState: "READY", entryQuality: 10 }),
    ];
    expect([...rows].sort(compareByEntryQuality).map((x) => x.symbol)).toEqual(["HOT", "COLD"]);
    expect([...rows].sort(compareByV5State).map((x) => x.symbol)).toEqual(["COLD", "HOT"]);
  });

  it("동률이면 symbol 오름차순", () => {
    const rows = [
      row("BBB", { entryQuality: 50 }),
      row("AAA", { entryQuality: 50 }),
    ];
    expect([...rows].sort(compareByEntryQuality).map((x) => x.symbol)).toEqual(["AAA", "BBB"]);
  });
});

describe("compareBySymbol", () => {
  it("symbol 오름차순 고정 — off 모드(v5 분석 없음)의 기준 정렬", () => {
    const rows = [{ symbol: "TSLA" }, { symbol: "AAPL" }, { symbol: "MSFT" }];
    expect([...rows].sort(compareBySymbol).map((x) => x.symbol)).toEqual([
      "AAPL",
      "MSFT",
      "TSLA",
    ]);
  });
});

describe("모드별 정렬 키", () => {
  it("active·shadow는 v5 계열 5종을 모두 제공하고, off·summary 부재는 선택지가 없다", () => {
    expect(sortKeysForMode("active")).toEqual(["v5", "trend", "direction", "quality", "symbol"]);
    expect(sortKeysForMode("shadow")).toEqual(["v5", "trend", "direction", "quality", "symbol"]);
    expect(sortKeysForMode("off")).toEqual([]);
    expect(sortKeysForMode(null)).toEqual([]);
  });

  it("저장된 선택이 현재 모드에서 유효하지 않으면 기본(v5)으로 떨어진다", () => {
    expect(resolveSortKey("active", "trend")).toBe("trend");
    expect(resolveSortKey("active", "nonsense")).toBe("v5");
    expect(resolveSortKey("shadow", "quality")).toBe("quality");
  });

  it("localStorage에 남은 옛 v4 값은 어떤 모드에서도 안전하게 기본값으로 떨어진다", () => {
    expect(resolveSortKey("active", "v4")).toBe("v5");
    expect(resolveSortKey("shadow", "v4")).toBe("v5");
    expect(resolveSortKey("off", "v4")).toBe("v5");
    expect(resolveSortKey(null, "v4")).toBe("v5");
  });

  it("off는 선택지가 없어 resolveSortKey가 fallback(v5)을 반환하지만, 화면은 이 값을 쓰지 않고 종목 알파벳순을 쓴다", () => {
    expect(resolveSortKey("off", "trend")).toBe("v5");
    expect(resolveSortKey(null, "v5")).toBe("v5");
  });
});
