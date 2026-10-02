export type EntryObservationRow = {
  symbol: string;
  status: string;
  evaluatedAt: string;
  analysisAsOf: string | null;
  quoteAt: string | null;
  dataDelaySeconds: number | null;
  candidateCount: number;
  approvedCount: number;
  rejectedCount: number;
  finalDisposition: string;
  firstGateReason: string | null;
  duplicateReasons: string[];
  rejectionReasons: string[];
};

export type EntryObservabilityReport = {
  generatedAt: string;
  candidateCount: number;
  approvedCount: number;
  rejectedCount: number;
  symbols: EntryObservationRow[];
};

export type EntryObservationReasonCount = { code: string; count: number };

export type EntryObservationCandidateRow = {
  symbol: string;
  eventId: string;
  kind: string;
  state: string;
  triggerBarStart: string | null;
  firstObservedAt: string;
  lastObservedAt: string;
  rejectionCodes: string[];
  policyHash: string;
};

export type EntryObservationHistoryWindow = {
  label: string;
  from: string;
  to: string;
  files: number;
  observations: number;
  symbols: number;
  candidates: number;
  ready: number;
  entered: number;
  rejected: number;
  topReasons: EntryObservationReasonCount[];
  corruptLines: number;
};

/** 관측 jsonl 기반 누적. 서버가 파일에서 집계하므로 화면 전환·새로고침·재기동과 무관하게 같은 값이다. */
export type EntryObservationHistoryReport = {
  generatedAt: string;
  policyHash: string;
  today: EntryObservationHistoryWindow;
  currentPolicy: EntryObservationHistoryWindow;
  recentCandidates: EntryObservationCandidateRow[];
  warning: string | null;
};

const labels: Record<string, string> = {
  NO_CANDIDATE: "후보 없음",
  REJECTED: "최종 거절",
  READY: "진입 대기",
  ENTERED: "진입 완료",
  WAIT: "대기",
  INVALIDATED: "무효화",
  EXPIRED: "만료",
  STALE_LATEST_BAR: "완료 봉 지연",
  STALE_QUOTE: "시세 지연",
  MISSING_QUOTE: "시세 없음",
  QUOTE_IN_FUTURE: "시세 시각 오류",
  OUTSIDE_REGULAR_SESSION: "정규장 외",
  AFTER_ENTRY_CUTOFF: "진입 마감 이후",
  DUPLICATE_TRIGGER_GUARD: "중복 트리거",
  TRIGGER_EPISODE_CONSUMED: "이미 소비한 구간",
  NEW_TRIGGER_SUPPRESSED: "신규 트리거 억제",
  BREAKOUT_ZONE_COOLDOWN: "돌파 구간 쿨다운",
};

export const entryReasonLabel = (value: string | null) =>
  value == null ? "근거 없음" : labels[value] ?? value;

export const entryDispositionLabel = (value: string) => labels[value] ?? value;

export const dataDelayLabel = (seconds: number | null) =>
  seconds == null ? "시각 없음" : seconds < 60 ? `${Math.round(seconds)}초` : `${(seconds / 60).toFixed(1)}분`;
