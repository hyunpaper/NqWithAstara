// 이슈 #26 — 라이브 목록(App)과 구조 뷰(StructurePanel)가 공유하는 정렬 비교 함수(승인 설계안 §3).
//
// 규칙:
// - active 기본: v5 상태 우선순위 → EntryQuality ↓ → |SignedTrend| ↓ → symbol (추세 강도는 동률 해소 축일 뿐,
//   높은 상승 추세가 좋은 매수가를 뜻하지 않는다 — §3).
// - 결정성: 동률은 symbol 오름차순, 결측(null/NaN)은 항상 마지막. v5 결측을 `?? 0`으로 0점 위장하지 않는다.
// - v4 정렬(shadow/off 기본)은 기존 의미 그대로 score 내림차순이며 v5 값을 섞지 않는다.

import type { StructureSummaryRow } from "./structureTypes";

/** 라이브 목록 정렬 키. v5(상태·품질) / trend(추세 강도) / v4(참고 점수). */
export type LiveSortKey = "v5" | "trend" | "v4";

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

/** active 기본 정렬: v5 상태 → EntryQuality ↓ → |SignedTrend| ↓ → symbol (§3). */
export const compareByV5State = (a: StructureSummaryRow, b: StructureSummaryRow): number =>
  v5StatePriority(a.candidateState) - v5StatePriority(b.candidateState) ||
  descNullsLast(a.entryQuality, b.entryQuality) ||
  descNullsLast(magnitude(a.signedTrend), magnitude(b.signedTrend)) ||
  a.symbol.localeCompare(b.symbol);

/** 추세 강도 정렬: |SignedTrend| ↓ → symbol. 방향이 아니라 크기다(§3 선택 정렬 ①). */
export const compareByTrendStrength = (a: StructureSummaryRow, b: StructureSummaryRow): number =>
  descNullsLast(magnitude(a.signedTrend), magnitude(b.signedTrend)) ||
  a.symbol.localeCompare(b.symbol);

/** v4 참고 점수 정렬 — 기존 라이브 목록 의미 그대로(score ?? 0 내림차순) + symbol 동률 해소. */
export type V4Scored = { symbol: string; score?: number | null };
export const compareByV4Score = (a: V4Scored, b: V4Scored): number =>
  (b.score ?? 0) - (a.score ?? 0) || a.symbol.localeCompare(b.symbol);

/** 모드별 선택 가능한 정렬 키(§3 표). off·summary 부재는 v4 고정이다. */
export const sortKeysForMode = (mode: string | null | undefined): LiveSortKey[] => {
  switch (mode) {
    case "active":
      return ["v5", "trend", "v4"];
    case "shadow":
      return ["v4", "v5", "trend"];
    default:
      return ["v4"];
  }
};

/** 저장된 선택이 현재 모드에서 유효하지 않으면 모드 기본값으로 떨어진다. */
export const resolveSortKey = (
  mode: string | null | undefined,
  stored: string | null | undefined,
): LiveSortKey => {
  const allowed = sortKeysForMode(mode);
  return allowed.includes(stored as LiveSortKey) ? (stored as LiveSortKey) : allowed[0];
};
