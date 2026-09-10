// 이슈 #27 — 시뮬레이션 대시보드 전용 타입/헬퍼.
// /api/sim 응답의 additive 필드(structure 코호트 섹션 + 거래별 동결 컨텍스트)를 다룬다.
// 원칙(§10/§11): 표시는 진입 당시 동결(FrozenStructureContext) 값만 쓰고, 현재 재계산 값으로
// 과거 거래를 채우지 않는다. 없는 값은 0이 아니라 "미수집"으로 표기한다.
// structureTypes.ts(실시간 구조 뷰 소유)와 분리된 파일이다 — 저장 계약(SimTrade.Structure) 기준.

/** 코호트 성과. winRate의 분모는 validClosed(손익 유효 청산)이며 표본이 없으면 null이다. */
export type CohortStats = {
  total: number;
  open: number;
  closed: number;
  validClosed: number;
  wins: number;
  winRate: number | null;
  /** 실현 손익 평균(%) — 왕복 수수료 0.2% 차감 후. 계획 netR과 다른 축이다. */
  avgPnl: number | null;
  missingPnl: number;
  estimatedExits: number;
  /** 동결 계획의 netR 평균(R 단위) — 계획값이지 실현 손익이 아니다. */
  avgPlannedNetR: number | null;
  plannedNetRSamples: number;
};

/** collected=false는 미수집 코호트(컨텍스트 누락·EntryQuality 미수집)다. 0이나 정상 구간처럼 그리지 않는다. */
export type SimulationCohort = {
  key: string;
  label: string;
  collected: boolean;
  stats: CohortStats;
};

export type SimulationCohortGroup = {
  dimension: string;
  title: string;
  cohorts: SimulationCohort[];
};

export type StructureCohortReport = {
  v5Stats: CohortStats;
  contextMissing: number;
  groups: SimulationCohortGroup[];
};

/** SimTrade.Structure.PlanSnapshot의 동결 계획(§11 FrozenPlanSnapshot) — 화면이 쓰는 부분만 선언. */
export type FrozenPlan = {
  planId: string;
  kind: string;
  entryReference: number;
  invalidationAnchor: number;
  stop: number;
  target: number;
  invalidationZoneId: string;
  invalidationLower: number;
  invalidationUpper: number;
  targetZoneId: string;
  targetLower: number;
  targetUpper: number;
  netR: number;
  missingLiquidity: boolean;
  eligibilityCostModelVersion: string;
  realizedFillCostModelVersion: string;
  engineVersion: string;
  policyHash: string;
  reasonCodes: string[];
  explanation: string;
};

/** 거래에 저장된 진입 시점 동결 컨텍스트(§11 FrozenStructureContext). */
export type TradeStructure = {
  entryEventId: string;
  planSnapshot: FrozenPlan;
  trendAtEntry: string;
  signedTrendAtEntry: number | null;
  entryQualityAtEntry: number | null;
  analysisAsOf: string;
  quoteAt: string | null;
  structuralExitPolicyVersion: string;
};

export const trendKo = (trend: string | null | undefined): string =>
  trend === "UP"
    ? "상승"
    : trend === "DOWN"
      ? "하락"
      : trend === "RANGE"
        ? "횡보"
        : trend === "TRANSITION"
          ? "전환"
          : trend === "UNKNOWN"
            ? "판정 불가"
            : "미수집";

const num = (v: number | null | undefined, digits = 1): string =>
  v == null ? "미수집" : v.toFixed(digits);

/**
 * 매매 기록 행 툴팁. v5 거래는 동결 근거(품질·추세·계획·zone)를, v4 거래는 기존 점수 축을 보여 준다.
 * v5 필드가 없는 v4 거래를 "—"의 나열로 채우지 않기 위한 분기이며, 계획 netR에는 "계획"을 명시한다.
 */
export const tradeEntryTooltip = (t: {
  score?: number | null;
  extSigma?: number | null;
  relVolume?: number | null;
  buyShare?: number | null;
  rsi?: number | null;
  reasons?: string[] | null;
  structure?: TradeStructure | null;
}): string => {
  const s = t.structure;
  if (!s) {
    return [
      `점수 ${t.score ?? "—"} · σ ${t.extSigma ?? "—"} · 거래량 ${t.relVolume ?? "—"}× · 매수비중 ${t.buyShare ?? "—"}% · RSI ${t.rsi ?? "—"}`,
      ...(t.reasons ?? []),
    ].join("\n");
  }
  const p = s.planSnapshot;
  return [
    `v5 동결 근거 (진입 시점 값 · 재계산 없음)`,
    `EntryQuality ${num(s.entryQualityAtEntry)} · 추세 ${trendKo(s.trendAtEntry)} (${num(s.signedTrendAtEntry)}) · 셋업 ${p.kind}`,
    `계획 netR ${num(p.netR, 2)}R (실현 손익 아님)${p.missingLiquidity ? " · 비용 결측(스프레드 0 가정)" : ""}`,
    `무효화 zone ${p.invalidationZoneId} [${p.invalidationLower}~${p.invalidationUpper}] · 목표 zone ${p.targetZoneId} [${p.targetLower}~${p.targetUpper}]`,
    `엔진 ${p.engineVersion} · policy ${p.policyHash}`,
    p.explanation,
  ].join("\n");
};

/** 승률 셀: 값과 분모(표본)를 항상 함께 보여 준다. 표본 0은 0%가 아니라 "표본 없음"이다. */
export const winRateText = (stats: CohortStats): string =>
  stats.winRate == null
    ? "표본 없음"
    : `${stats.winRate}% (${stats.wins}/${stats.validClosed}건)`;

/** 계획 netR 셀: 표본 없으면 미수집. 실현 손익과 혼동되지 않게 R 단위를 붙인다. */
export const plannedNetRText = (stats: CohortStats): string =>
  stats.avgPlannedNetR == null
    ? "미수집"
    : `${stats.avgPlannedNetR}R (${stats.plannedNetRSamples}건)`;
