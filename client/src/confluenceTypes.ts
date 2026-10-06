// 이슈 #168 — 컨플루언스 응답 타입/정규화. 서버 계약(#167, ConfluenceService.cs)을
// 그대로 신뢰하지 않고 결측·알 수 없는 필드를 안전하게 무시한다(newsTypes.ts와 같은 패턴).

export type ConfluenceTechnique = {
  name: string;
  score: number | null;
  confidence: number | null;
  weight: number | null;
  warmup: boolean;
  contributing: boolean;
  correlationGroup: string | null;
  evidence: Record<string, number | null>;
};

export type ConfluenceResponse = {
  symbol: string;
  status: "ready" | "warmup";
  policyHash: string | null;
  weightsVersion: string | null;
  barEnd: string | null;
  score: number | null;
  warmupCount: number;
  techniques: ConfluenceTechnique[];
  updatedAt: string | null;
};

/** 이슈 #379: 총점에 가장 크게 기여한 요소 한 개(상위/하위 요약용). */
export type ConfluenceContributor = {
  name: string;
  score: number | null;
  contribution: number | null;
  evidence: Record<string, number | null>;
};

/**
 * `/api/structure/{symbol}`·`/api/state`의 `structureSummary` 행에 additive로 실리는 요약.
 * barEnd는 이슈 #181에서 `structureSummary` 쪽에 추가됐다 — 캐시가 없으면 전체가 null이다.
 * top/bottom은 이슈 #379에서 추가된 기여 상위(+)·하위(−) 요소이며 없으면 빈 배열이다.
 */
export type ConfluenceSummary = {
  score: number | null;
  warmupCount: number;
  weightsVersion: string | null;
  barEnd: string | null;
  top: ConfluenceContributor[];
  bottom: ConfluenceContributor[];
};

const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const str = (v: unknown): string | null => (typeof v === "string" && v.length > 0 ? v : null);
const bool = (v: unknown): boolean => v === true;

const normalizeEvidence = (raw: unknown): Record<string, number | null> => {
  if (typeof raw !== "object" || raw === null) return {};
  const out: Record<string, number | null> = {};
  for (const [key, value] of Object.entries(raw as Record<string, unknown>)) out[key] = num(value);
  return out;
};

/** name 없는 기법은 목록에서 제외한다 — 라벨·키 식별이 불가능하다. */
export const normalizeTechnique = (raw: unknown): ConfluenceTechnique | null => {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const name = str(r.name);
  if (!name) return null;
  return {
    name,
    score: num(r.score),
    confidence: num(r.confidence),
    weight: num(r.weight),
    warmup: bool(r.warmup),
    contributing: bool(r.contributing),
    correlationGroup: str(r.correlationGroup),
    evidence: normalizeEvidence(r.evidence),
  };
};

export const normalizeConfluenceResponse = (raw: unknown): ConfluenceResponse => {
  const r = (typeof raw === "object" && raw !== null ? raw : {}) as Record<string, unknown>;
  const techniques = Array.isArray(r.techniques)
    ? r.techniques.map(normalizeTechnique).filter((x): x is ConfluenceTechnique => x !== null)
    : [];
  return {
    symbol: str(r.symbol) ?? "",
    status: r.status === "ready" ? "ready" : "warmup",
    policyHash: str(r.policyHash),
    weightsVersion: str(r.weightsVersion),
    barEnd: str(r.barEnd),
    score: num(r.score),
    warmupCount: num(r.warmupCount) ?? 0,
    techniques,
    updatedAt: str(r.updatedAt),
  };
};

const normalizeContributor = (raw: unknown): ConfluenceContributor | null => {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const name = str(r.name);
  if (!name) return null;
  return {
    name,
    score: num(r.score),
    contribution: num(r.contribution),
    evidence: normalizeEvidence(r.evidence),
  };
};

const normalizeContributors = (raw: unknown): ConfluenceContributor[] =>
  Array.isArray(raw)
    ? raw.map(normalizeContributor).filter((x): x is ConfluenceContributor => x !== null)
    : [];

export const normalizeConfluenceSummary = (raw: unknown): ConfluenceSummary | null => {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  return {
    score: num(r.score),
    warmupCount: num(r.warmupCount) ?? 0,
    weightsVersion: str(r.weightsVersion),
    barEnd: str(r.barEnd),
    top: normalizeContributors(r.top),
    bottom: normalizeContributors(r.bottom),
  };
};
