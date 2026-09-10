// 이슈 #26 — 모드별 정렬 비교 함수(승인 설계안 §3·§6 FE 테스트 8·11).
// 관점: active 기본은 v5 상태 → 진입 품질 → |추세| → symbol이고, v4 점수는 정렬에 끼어들지 않는다.
// 결측은 항상 마지막이며 0으로 위장하지 않는다. 동률은 symbol 오름차순(결정성).
import { describe, expect, it } from "vitest";
import type { StructureSummaryRow } from "./structureTypes";
import {
  compareByTrendStrength,
  compareByV4Score,
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
  it("결측(null/NaN)은 항상 마지막 — 0으로 대체하지 않는다", () => {
    // 음수 값(-10)도 결측보다 앞이다. null을 0으로 바꿨다면 -10이 뒤로 갔을 것이다.
    expect(descNullsLast(-10, null)).toBeLessThan(0);
    expect(descNullsLast(null, -10)).toBeGreaterThan(0);
    expect(descNullsLast(Number.NaN, 1)).toBeGreaterThan(0);
    expect(descNullsLast(null, null)).toBe(0);
  });
  it("값이 둘 다 있으면 내림차순", () => {
    expect(descNullsLast(70, 30)).toBeLessThan(0);
  });
});

describe("compareByV5State (active 기본 정렬)", () => {
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

  it("품질 동률이면 |SignedTrend|가 큰 쪽이 앞이고, 그마저 같으면 symbol 오름차순(결정성)", () => {
    const rows = [
      row("BBB", { candidateState: "READY", entryQuality: 50, signedTrend: 20 }),
      row("AAA", { candidateState: "READY", entryQuality: 50, signedTrend: -45 }), // 절대값 45 > 20
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

  it("v4/v5 상반 판단: v4 85점·v5 거절은 v4 40점·v5 READY보다 뒤다 (v4 점수는 비교에 없다)", () => {
    // §6 테스트 11 fixture. 비교 함수 시그니처 자체가 score를 받지 않는다 —
    // v5 정렬 결과는 v4 점수와 무관함을 상반 값으로 고정한다.
    const conflicted = [
      { row: row("HOT", { candidateState: "REJECTED", entryQuality: null }), v4Score: 85 },
      { row: row("COLD", { candidateState: "READY", entryQuality: 55 }), v4Score: 40 },
    ];
    const v5Order = [...conflicted].sort((a, b) => compareByV5State(a.row, b.row));
    expect(v5Order.map((x) => x.row.symbol)).toEqual(["COLD", "HOT"]);
    const v4Order = [...conflicted].sort((a, b) =>
      compareByV4Score(
        { symbol: a.row.symbol, score: a.v4Score },
        { symbol: b.row.symbol, score: b.v4Score },
      ),
    );
    expect(v4Order.map((x) => x.row.symbol)).toEqual(["HOT", "COLD"]);
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

describe("compareByV4Score (shadow/off 기본)", () => {
  it("기존 의미 그대로 score 내림차순(null은 0 취급) + symbol 동률 해소", () => {
    const rows = [
      { symbol: "BBB", score: 70 },
      { symbol: "AAA", score: null },
      { symbol: "CCC", score: 85 },
      { symbol: "AAB", score: 70 },
    ];
    expect([...rows].sort(compareByV4Score).map((x) => x.symbol)).toEqual([
      "CCC",
      "AAB",
      "BBB",
      "AAA",
    ]);
  });
});

describe("모드별 정렬 키 (§3 표)", () => {
  it("active 기본은 v5, shadow·off·summary 부재 기본은 v4", () => {
    expect(sortKeysForMode("active")[0]).toBe("v5");
    expect(sortKeysForMode("shadow")[0]).toBe("v4");
    expect(sortKeysForMode("off")).toEqual(["v4"]);
    expect(sortKeysForMode(null)).toEqual(["v4"]);
  });
  it("shadow는 v5 정렬을 선택할 수 있고(관측 검증용), off는 v4 고정이다", () => {
    expect(sortKeysForMode("shadow")).toContain("v5");
    expect(sortKeysForMode("shadow")).toContain("trend");
    expect(sortKeysForMode("off")).not.toContain("v5");
  });
  it("저장된 선택이 현재 모드에서 유효하지 않으면 기본으로 떨어진다", () => {
    expect(resolveSortKey("active", "v4")).toBe("v4");
    expect(resolveSortKey("active", "nonsense")).toBe("v5");
    expect(resolveSortKey("off", "v5")).toBe("v4");
    expect(resolveSortKey(null, "trend")).toBe("v4");
  });
});
