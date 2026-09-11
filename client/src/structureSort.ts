// 이슈 #26 — 라이브 목록(App)과 구조 뷰(StructurePanel)가 공유하는 정렬 비교 함수(승인 설계안 §3).
// 이슈 #88 — v4 정렬(참고 점수)을 화면에서 전면 제거. active/shadow는 v5 계열 정렬만 제공하고,
// off(v5 분석 없음)는 정렬 선택 자체가 없다 — 종목 알파벳순으로 고정 표시한다(App.tsx가 처리).
//
// 규칙:
// - active/shadow 기본: v5 상태 우선순위 → EntryQuality ↓ → |SignedTrend| ↓ → symbol (추세 강도는 동률 해소 축일 뿐,
//   높은 상승 추세가 좋은 매수가를 뜻하지 않는다 — §3).
// - 결정성: 동률은 symbol 오름차순, 결측(null/NaN)은 항상 마지막. v5 결측을 `?? 0`으로 0점 위장하지 않는다.

import type { StructureSummaryRow } from "./structureTypes";

/** 라이브 목록 정렬 키. v5(상태·품질) / trend(추세 강도) / direction(추세 방향) / quality(진입 품질) / symbol(종목명). */
export type LiveSortKey = "v5" | "trend" | "direction" | "quality" | "symbol";

/** v5 상태 우선순위: READY·진입됨=0 → 대기=1 → 거절·무효화·만료=2 → 상태 없음=3 (§3). */
export const v5StatePriority = (state: string | null | undefined): number => {
  switch ((state ?? "").toUpperCase()) {
    case "READY":
    case "ENTERED":
      return 0;
    case "WAIT":
      return 1;
    case "REJECTED":
    case "INVALIDATED":
    case "EXPIRED":
      return 2;
    default:
      return 3;
  }
};

/** 내림차순 숫자 비교 — 결측(null/NaN)은 항상 마지막. 0으로 대체하지 않는다(§2-4). */
export const descNullsLast = (
  a: number | null | undefined,
  b: number | null | undefined,
): number => {
  const aOk = a != null && Number.isFinite(a);
  const bOk = b != null && Number.isFinite(b);
  if (aOk && bOk) return (b as number) - (a as number);
  if (aOk) return -1;
  if (bOk) return 1;
  return 0;
};

const magnitude = (value: number | null | undefined): number | null =>
  value == null || !Number.isFinite(value) ? null : Math.abs(value);

/** active/shadow 기본 정렬: v5 상태 → EntryQuality ↓ → |SignedTrend| ↓ → symbol (§3). */
export const compareByV5State = (a: StructureSummaryRow, b: StructureSummaryRow): number =>
  v5StatePriority(a.candidateState) - v5StatePriority(b.candidateState) ||
  descNullsLast(a.entryQuality, b.entryQuality) ||
  descNullsLast(magnitude(a.signedTrend), magnitude(b.signedTrend)) ||
  a.symbol.localeCompare(b.symbol);

/** 추세 강도 정렬: |SignedTrend| ↓ → symbol. 방향이 아니라 크기다(§3 선택 정렬 ①). */
export const compareByTrendStrength = (a: StructureSummaryRow, b: StructureSummaryRow): number =>
  descNullsLast(magnitude(a.signedTrend), magnitude(b.signedTrend)) ||
  a.symbol.localeCompare(b.symbol);

/**
 * 추세 방향 정렬: SignedTrend 부호 있는 값 ↓ → symbol. 추세 강도(절대값)와 달리 하락(음수)이
 * 뒤로 밀려나므로 상승 우세 종목이 위, 하락 우세 종목이 아래로 실제로 순서가 갈린다.
 */
export const compareByTrendDirection = (a: StructureSummaryRow, b: StructureSummaryRow): number =>
  descNullsLast(a.signedTrend, b.signedTrend) || a.symbol.localeCompare(b.symbol);

/** 진입 품질 정렬: EntryQuality ↓ → symbol. 후보가 없으면(결측) 항상 마지막이다. */
export const compareByEntryQuality = (a: StructureSummaryRow, b: StructureSummaryRow): number =>
  descNullsLast(a.entryQuality, b.entryQuality) || a.symbol.localeCompare(b.symbol);

/** 종목명 정렬: symbol 오름차순. 항상 결정적이며 v5 분석이 없는 상태(off)의 기준 정렬이다. */
export const compareBySymbol = (a: { symbol: string }, b: { symbol: string }): number =>
  a.symbol.localeCompare(b.symbol);

/** 모드별 선택 가능한 정렬 키(§3 표 + 이슈 #88). off·summary 부재는 선택지가 없다 — 종목 알파벳순 고정. */
export const sortKeysForMode = (mode: string | null | undefined): LiveSortKey[] => {
  switch (mode) {
    case "active":
    case "shadow":
      return ["v5", "trend", "direction", "quality", "symbol"];
    default:
      return [];
  }
};

/**
 * 저장된 선택이 현재 모드에서 유효하지 않으면 모드 기본값으로 떨어진다.
 * 이슈 #88: localStorage에 남아있을 수 있는 옛 "v4" 값도 여기로 걸러져 안전하게 기본값으로 떨어진다.
 * 선택지가 없는 모드(off)에서는 "v5"를 반환하지만, 화면은 이 값을 쓰지 않고 종목 알파벳순으로 고정한다.
 */
export const resolveSortKey = (
  mode: string | null | undefined,
  stored: string | null | undefined,
): LiveSortKey => {
  const allowed = sortKeysForMode(mode);
  return allowed.includes(stored as LiveSortKey) ? (stored as LiveSortKey) : (allowed[0] ?? "v5");
};
