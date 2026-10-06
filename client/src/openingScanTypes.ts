export type OpeningGrade = "STRONG" | "VOLUME_ONLY" | "PRICE_ONLY" | "WEAK";
export type OpeningPhase = "idle" | "pending" | "scanning" | "summary" | "disabled";
export type OpeningVolumeStatus = "full" | "partial" | "insufficient" | "pending";

export type OpeningFirst5 = { barsSeen: number; upBars: number; newHighs: number };
export type OpeningPremarket = { high: number; volume: number; abovePremarketHigh: boolean };

export type OpeningScanRow = {
  symbol: string;
  name: string;
  grade: OpeningGrade;
  score: number;
  volumeStatus: OpeningVolumeStatus;
  rvolNow: number | null;
  rvol5: number | null;
  sampleCount: number;
  changeFromOpenPercent: number | null;
  changeFromPrevClosePercent: number | null;
  gapPercent: number | null;
  prevCloseSource: string | null;
  aboveVwap: boolean | null;
  first5: OpeningFirst5 | null;
  brokeOpeningRange: boolean | null;
  premarket: OpeningPremarket | null;
  quoteStatus: string;
  reasons: string[];
  observedAt: string;
  elapsedMinutes: number;
};

export type OpeningFollowupRow = {
  symbol: string;
  name: string;
  gradeAt5: OpeningGrade;
  returnPercent30: number | null;
  mfePercent30: number | null;
  maePercent30: number | null;
  rvol5: number | null;
};

export type OpeningCloseRow = {
  symbol: string;
  name: string;
  gradeAt5: OpeningGrade;
  returnPercentClose: number | null;
};

export type OpeningScanSummary = {
  at5: OpeningScanRow[];
  at30: OpeningScanRow[];
  followup30: OpeningFollowupRow[];
  close: OpeningCloseRow[];
};

export type OpeningScanResponse = {
  sessionDate: string | null;
  phase: OpeningPhase;
  windowStart: string | null;
  windowEnd: string | null;
  asOf: string;
  elapsedMinutes: number;
  policyVersion: string;
  lookbackSessions: number;
  minimumSessions: number;
  refreshSeconds: number;
  rows: OpeningScanRow[];
  summary: OpeningScanSummary | null;
  warnings: string[];
};

const record = (value: unknown): Record<string, unknown> | null =>
  typeof value === "object" && value !== null ? value as Record<string, unknown> : null;
const text = (value: unknown): string | null => typeof value === "string" ? value : null;
const finite = (value: unknown): number | null => typeof value === "number" && Number.isFinite(value) ? value : null;
const bool = (value: unknown): boolean | null => typeof value === "boolean" ? value : null;

const grade = (value: unknown): OpeningGrade =>
  value === "STRONG" || value === "VOLUME_ONLY" || value === "PRICE_ONLY" ? value : "WEAK";

const volumeStatus = (value: unknown): OpeningVolumeStatus =>
  value === "full" || value === "partial" || value === "insufficient" ? value : "pending";

const first5 = (value: unknown): OpeningFirst5 | null => {
  const r = record(value);
  if (!r) return null;
  return { barsSeen: finite(r.barsSeen) ?? 0, upBars: finite(r.upBars) ?? 0, newHighs: finite(r.newHighs) ?? 0 };
};

const premarket = (value: unknown): OpeningPremarket | null => {
  const r = record(value);
  if (!r) return null;
  return { high: finite(r.high) ?? 0, volume: finite(r.volume) ?? 0, abovePremarketHigh: r.abovePremarketHigh === true };
};

const row = (raw: unknown): OpeningScanRow | null => {
  const r = record(raw);
  const symbol = text(r?.symbol);
  if (!r || !symbol) return null;
  return {
    symbol,
    name: text(r.name) ?? symbol,
    grade: grade(r.grade),
    score: finite(r.score) ?? 0,
    volumeStatus: volumeStatus(r.volumeStatus),
    rvolNow: finite(r.rvolNow),
    rvol5: finite(r.rvol5),
    sampleCount: finite(r.sampleCount) ?? 0,
    changeFromOpenPercent: finite(r.changeFromOpenPercent),
    changeFromPrevClosePercent: finite(r.changeFromPrevClosePercent),
    gapPercent: finite(r.gapPercent),
    prevCloseSource: text(r.prevCloseSource),
    aboveVwap: bool(r.aboveVwap),
    first5: first5(r.first5),
    brokeOpeningRange: bool(r.brokeOpeningRange),
    premarket: premarket(r.premarket),
    quoteStatus: text(r.quoteStatus) ?? "unknown",
    reasons: Array.isArray(r.reasons) ? r.reasons.flatMap((x) => text(x) ?? []) : [],
    observedAt: text(r.observedAt) ?? "",
    elapsedMinutes: finite(r.elapsedMinutes) ?? 0,
  };
};

const followup = (raw: unknown): OpeningFollowupRow | null => {
  const r = record(raw);
  const symbol = text(r?.symbol);
  if (!r || !symbol) return null;
  return {
    symbol,
    name: text(r.name) ?? symbol,
    gradeAt5: grade(r.gradeAt5),
    returnPercent30: finite(r.returnPercent30),
    mfePercent30: finite(r.mfePercent30),
    maePercent30: finite(r.maePercent30),
    rvol5: finite(r.rvol5),
  };
};

const closeRow = (raw: unknown): OpeningCloseRow | null => {
  const r = record(raw);
  const symbol = text(r?.symbol);
  if (!r || !symbol) return null;
  return {
    symbol,
    name: text(r.name) ?? symbol,
    gradeAt5: grade(r.gradeAt5),
    returnPercentClose: finite(r.returnPercentClose),
  };
};

const rows = (value: unknown) => Array.isArray(value) ? value.flatMap((x) => row(x) ?? []) : [];

export const normalizeOpeningScan = (value: unknown): OpeningScanResponse | null => {
  const root = record(value);
  if (!root) return null;
  const phase = text(root.phase);
  const rawSummary = record(root.summary);
  const summary: OpeningScanSummary | null = rawSummary
    ? {
        at5: rows(rawSummary.at5),
        at30: rows(rawSummary.at30),
        followup30: Array.isArray(rawSummary.followup30) ? rawSummary.followup30.flatMap((x) => followup(x) ?? []) : [],
        close: Array.isArray(rawSummary.close) ? rawSummary.close.flatMap((x) => closeRow(x) ?? []) : [],
      }
    : null;
  return {
    sessionDate: text(root.sessionDate),
    phase: phase === "pending" || phase === "scanning" || phase === "summary" || phase === "disabled" ? phase : "idle",
    windowStart: text(root.windowStart),
    windowEnd: text(root.windowEnd),
    asOf: text(root.asOf) ?? "",
    elapsedMinutes: finite(root.elapsedMinutes) ?? 0,
    policyVersion: text(root.policyVersion) ?? "opening-scan.1",
    lookbackSessions: finite(root.lookbackSessions) ?? 20,
    minimumSessions: finite(root.minimumSessions) ?? 5,
    refreshSeconds: finite(root.refreshSeconds) ?? 60,
    rows: rows(root.rows),
    summary,
    warnings: Array.isArray(root.warnings) ? root.warnings.flatMap((x) => text(x) ?? []) : [],
  };
};

const rvolText = (value: number | null): string | null => value == null ? null : `${value.toFixed(1)}×`;
const moveText = (value: number | null): string | null =>
  value == null ? null : `${value >= 0 ? "▲" : "▼"}${Math.abs(value).toFixed(1)}%`;

/// 관심종목 행 배지. WEAK는 null(배지 없음).
export const openingScanBadge = (row: OpeningScanRow): { label: string; title: string } | null => {
  const title = row.reasons.join("\n");
  const rvol = rvolText(row.rvolNow);
  const move = moveText(row.changeFromOpenPercent);
  if (row.grade === "STRONG") return { label: `개장 ${rvol ?? ""} ${move ?? ""}`.replace(/\s+/g, " ").trim(), title };
  if (row.grade === "VOLUME_ONLY" && rvol) return { label: `개장 ${rvol}`, title };
  if (row.grade === "PRICE_ONLY" && move) return { label: `개장 ${move}`, title };
  return null;
};

/// 헤더 팝오버 트리거 라벨. idle/disabled는 null(렌더 안 함).
export const openingScanTriggerLabel = (resp: OpeningScanResponse): { label: string; detail: string | null } | null => {
  if (resp.phase === "scanning") {
    const strong = resp.rows.filter((r) => r.grade !== "WEAK").length;
    return { label: `개장 초반 강세 ${strong}`, detail: `${resp.elapsedMinutes}분` };
  }
  if (resp.phase === "pending") return { label: "개장 봉 대기", detail: null };
  if (resp.phase === "summary") return { label: "개장 초반 기록", detail: null };
  return null;
};
