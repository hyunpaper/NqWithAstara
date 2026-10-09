// v5 구조 엔진 D4 — 화면이 사용하는 API 계약과 표기 규칙.
//
// 계약 출처: STRUCTURE-ENGINE-DESIGN.md §11(DTO) / §12(API) / §13(화면 요구사항).
// 서버(D3)의 `StructureQueryResponse` / `StructureAnalysisView`가 원본이며 JSON은 camelCase다.
// 여기서는 **추측한 값을 만들지 않는다**: 서버가 보내지 않는 필드는 optional + null 안전으로 두고,
// 없는 근거를 화면에서 그리지 않는다(§13 "구조가 없으면 선을 임의로 그리지 않는다").
//
// enum류(status/state/role/kind/code)는 문자열 union으로 좁히지 않고 `string`으로 받는다.
// D3가 값을 추가해도 화면이 깨지지 않게 하고, 모르는 값은 "원문 그대로 + 미등록" 으로 표기한다.

import type { ConfluenceSummary } from "./confluenceTypes";

export type StructureTrend = {
  state?: string | null;
  signedTrend?: number | null;
  priceDirection?: number | null;
  structureDirection?: number | null;
  efficiency?: number | null;
  atr1m?: number | null;
  ema9?: number | null;
  ema21?: number | null;
  vwap?: number | null;
  vwapSd?: number | null;
  structureEvidenceMissing?: boolean | null;
  barCount?: number | null;
  analysisCutoff?: string | null;
  usedFamilies?: string[] | null;
  missingComponents?: string[] | null;
  warnings?: string[] | null;
};

export type StructureZone = {
  id: string;
  boundsRevision?: number | null;
  snapshotRevision?: number | null;
  lower: number;
  upper: number;
  role?: string | null;
  originalRole?: string | null;
  firstConfirmedAt?: string | null;
  lastConfirmedAt?: string | null;
  sourceKinds?: string[] | null;
  sourceCount?: number | null;
  independentFamilies?: number | null;
  strength?: number | null;
  touchEvidence?: number | null;
  reactionEvidence?: number | null;
  recency?: number | null;
  confluence?: number | null;
  breachPenalty?: number | null;
  completedEpisodes?: number | null;
  successEpisodes?: number | null;
  failedEpisodes?: number | null;
  pendingEpisodes?: number | null;
  missingEvidence?: string[] | null;
  eligible?: boolean | null;
  rejectReasons?: string[] | null;
  approximationFlags?: string[] | null;
  profileOnly?: boolean | null;
  retired?: boolean | null;
};

export type StructurePlan = {
  planId: string;
  kind?: string | null;
  entryReference: number;
  invalidationAnchor: number;
  stop: number;
  target: number;
  invalidationZoneId?: string | null;
  invalidationLower?: number | null;
  invalidationUpper?: number | null;
  targetZoneId?: string | null;
  targetLower?: number | null;
  targetUpper?: number | null;
  buffer?: number | null;
  bufferBasis?: string | null;
  frontRunBuffer?: number | null;
  netReward?: number | null;
  netRisk?: number | null;
  netR?: number | null;
  riskPercent?: number | null;
  feePerShare?: number | null;
  extraCostPerShare?: number | null;
  validSpread?: number | null;
  missingLiquidity?: boolean | null;
  eligibilityCostModelVersion?: string | null;
  realizedFillCostModelVersion?: string | null;
  createdAt?: string | null;
  expiresAt?: string | null;
  engineVersion?: string | null;
  policyHash?: string | null;
  reasonCodes?: string[] | null;
  explanation?: string | null;
};

export type StructureQualityComponent = {
  name: string;
  raw?: number | null;
  value?: number | null;
  required?: boolean | null;
};

export type StructureCandidate = {
  eventId: string;
  kind?: string | null;
  zoneId?: string | null;
  triggerBarStart?: string | null;
  triggerConfirmedAt?: string | null;
  structureCutoff?: string | null;
  expiresAt?: string | null;
  state?: string | null;
  entryQuality?: number | null;
  entryReference?: number | null;
  invalidationAnchor?: number | null;
  stop?: number | null;
  target?: number | null;
  netR?: number | null;
  plan?: StructurePlan | null;
  components?: StructureQualityComponent[] | null;
  rejectionCodes?: string[] | null;
  notes?: string[] | null;
  counterTrend?: boolean | null;
  retestConfirmed?: boolean | null;
};

export type StructureSourceQuality = {
  source: string;
  status?: string | null;
  count?: number | null;
  expectedCount?: number | null;
  first?: string | null;
  last?: string | null;
  gaps?: string[] | null;
  conflicts?: string[] | null;
  coverageRatio?: number | null;
  warnings?: string[] | null;
};

export type StructureQuality = {
  sources?: StructureSourceQuality[] | null;
  warnings?: string[] | null;
  blockersForTrend?: string[] | null;
  blockersForZone?: string[] | null;
  blockersForCandidate?: string[] | null;
  blockersForReady?: string[] | null;
};

// TODO(D3 대조): 설계 §13은 "확정 피벗" 마커를 요구하지만 현재 공개 DTO(StructureAnalysisView)에는
// 피벗 좌표 배열이 없다. 필드가 추가되면 아래 optional 타입을 그대로 쓰고, 없으면 화면은
// "확정 피벗 좌표 미노출"이라고 적고 구간 확정 시각만 표시한다. 임의 좌표를 만들지 않는다.
export type StructurePivot = {
  sourceId?: string | null;
  kind?: string | null; // "HIGH" | "LOW"
  timeframe?: string | null; // "ONEMINUTE" | "FIVEMINUTE"
  price: number;
  occurredAt?: string | null;
  confirmedAt?: string | null;
};

export type StructureAnalysis = {
  symbol: string;
  mode?: string | null;
  status?: string | null;
  entryOwner?: string | null;
  engineVersion?: string | null;
  recordVersion?: string | null;
  policyHash?: string | null;
  sessionStart?: string | null;
  sessionEnd?: string | null;
  analysisAsOf?: string | null;
  lastCompletedBarStart?: string | null;
  quoteAt?: string | null;
  quotePrice?: number | null;
  evaluatedAt?: string | null;
  generation?: number | null;
  candidateSummary?: string | null;
  preferredCandidateId?: string | null;
  trend?: StructureTrend | null;
  zones?: StructureZone[] | null;
  candidates?: StructureCandidate[] | null;
  quality?: StructureQuality | null;
  warnings?: string[] | null;
  notes?: string[] | null;
  /** 서버가 아직 제공하지 않는다. 제공되면 확정 피벗 마커를 그린다. */
  pivots?: StructurePivot[] | null;
};

export type StructureResponse = {
  symbol: string;
  mode?: string | null;
  status?: string | null;
  entryOwner?: string | null;
  engineVersion?: string | null;
  policyHash?: string | null;
  message?: string | null;
  analysis?: StructureAnalysis | null;
  updatedAt?: string | null;
};

/** `/api/state`의 additive `structureSummary` 한 행(§12). */
export type StructureSummaryRow = {
  symbol: string;
  status?: string | null;
  trendState?: string | null;
  signedTrend?: number | null;
  candidateState?: string | null;
  preferredCandidateId?: string | null;
  entryQuality?: number | null;
  /** 이슈 #26: 대표 후보의 종류(PULLBACK/BREAKOUT/REBOUND). 대표 후보가 없으면 null이다. */
  preferredKind?: string | null;
  analysisAsOf?: string | null;
  quoteAt?: string | null;
  warnings?: string[] | null;
  /** 이슈 #181: ConfluenceService 캐시를 읽기만 한 additive 요약. 캐시 없음/warmup이면 null이다. */
  confluence?: ConfluenceSummary | null;
  /** #407: 대표 후보가 없어도 최근 후보(상태 무관)의 품질을 보여 주기 위한 additive 요약. 후보가 없으면 null. */
  latestCandidate?: StructureLatestCandidate | null;
};

export type StructureLatestCandidate = {
  eventId?: string | null;
  kind?: string | null;
  state?: string | null;
  entryQuality?: number | null;
  triggerConfirmedAt?: string | null;
  rejectionCodes?: string[] | null;
};

/**
 * 이슈 #26: `/api/state`의 additive `structureEvents` 한 건. 서버(StructureAlertPublisher)가
 * active gate 안 commit 지점에서 발행한 v5 알림 이벤트이며, FE는 표시·소리만 담당한다(발행 판단 없음).
 * seq는 서버 재시작을 넘어 단조 증가한다.
 */
export type StructureEventRow = {
  seq: number;
  type?: string | null; // V5_READY | V5_ENTERED | V5_BLOCKED
  symbol?: string | null;
  eventId?: string | null;
  kind?: string | null;
  entryQuality?: number | null;
  netR?: number | null;
  quotePrice?: number | null;
  at?: string | null;
  planId?: string | null;
  stop?: number | null;
  target?: number | null;
  reason?: string | null;
};

export type StructureSummary = {
  mode?: string | null;
  entryOwner?: string | null;
  legacyScoreEngine?: string | null;
  engineVersion?: string | null;
  policyHash?: string | null;
  notes?: string[] | null;
  symbols?: StructureSummaryRow[] | null;
};

// ── 표기 helper ──────────────────────────────────────────────────────────────
// 표시만 소수 1자리로 반올림한다(§3). 계산은 서버 원정밀도이며 화면에서 다시 계산하지 않는다.

export const arr = <T,>(value: T[] | null | undefined): T[] =>
  Array.isArray(value) ? value : [];

export const num1 = (value: number | null | undefined): string =>
  value == null || !Number.isFinite(value) ? "—" : value.toFixed(1);

export const num2 = (value: number | null | undefined): string =>
  value == null || !Number.isFinite(value) ? "—" : value.toFixed(2);

export const num3 = (value: number | null | undefined): string =>
  value == null || !Number.isFinite(value) ? "—" : value.toFixed(3);

export const price = (value: number | null | undefined): string =>
  value == null || !Number.isFinite(value) ? "—" : `$${value.toFixed(2)}`;

export const priceRange = (
  lower: number | null | undefined,
  upper: number | null | undefined,
): string =>
  lower == null || upper == null || !Number.isFinite(lower) || !Number.isFinite(upper)
    ? "—"
    : `${lower.toFixed(2)}~${upper.toFixed(2)}`;

export const clock = (value: string | null | undefined): string => {
  if (!value) return "—";
  const d = new Date(value);
  return Number.isNaN(d.getTime())
    ? "—"
    : d.toLocaleTimeString("ko-KR", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
};

export const ageSeconds = (value: string | null | undefined): number | null => {
  if (!value) return null;
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return null;
  return Math.max(0, Math.round((Date.now() - d.getTime()) / 1000));
};

// ── 상태·분류 라벨 ───────────────────────────────────────────────────────────

/** 조회 응답 status(§12). 각 상태를 색이 아니라 문장으로 구분한다(§13). */
export const statusLabel = (status: string | null | undefined): string => {
  switch (status) {
    case "available":
      return "분석 표시 중";
    case "warmup":
      return "워밍업 — 완료 봉 수집 중";
    case "marketClosed":
      return "정규장 외 — 갱신하지 않음";
    case "stopped":
      return "모니터링 중지됨";
    case "disabled":
      return "구조 엔진 off";
    case "unavailable":
      return "분석 불가";
    default:
      return status ? `상태 ${status} (미등록)` : "상태 미상";
  }
};

export const trendStateLabel = (state: string | null | undefined): string => {
  switch ((state ?? "").toUpperCase()) {
    case "UP":
      return "상승";
    case "DOWN":
      return "하락";
    case "RANGE":
      return "횡보";
    case "TRANSITION":
      return "전환";
    case "UNKNOWN":
      return "판단 불가";
    default:
      return state ? `${state} (미등록)` : "판단 불가";
  }
};

/** 후보 상태. READY/대기/부적합을 분리 표기한다(§13). */
export const candidateStateLabel = (state: string | null | undefined): string => {
  switch ((state ?? "").toUpperCase()) {
    case "READY":
      return "READY";
    case "WAIT":
      return "대기";
    case "REJECTED":
      return "부적합";
    case "INVALIDATED":
      return "무효화";
    case "EXPIRED":
      return "만료";
    case "ENTERED":
      return "진입 처리됨";
    default:
      return state ? `${state} (미등록)` : "—";
  }
};

/** 정렬·시각 구분용 그룹. 색만으로 의미를 대신하지 않고 라벨과 함께 쓴다. */
export const candidateGroup = (
  state: string | null | undefined,
): "ready" | "wait" | "reject" | "none" => {
  switch ((state ?? "").toUpperCase()) {
    case "READY":
      return "ready";
    case "WAIT":
      return "wait";
    case "REJECTED":
    case "INVALIDATED":
    case "EXPIRED":
      return "reject";
    case "ENTERED":
      return "wait";
    default:
      return "none";
  }
};

export const setupKindLabel = (kind: string | null | undefined): string => {
  switch ((kind ?? "").toUpperCase()) {
    case "PULLBACK":
      return "눌림(PULLBACK)";
    case "BREAKOUT":
      return "돌파(BREAKOUT)";
    case "REBOUND":
      return "반등(REBOUND)";
    default:
      return kind ? `${kind} (미등록)` : "—";
  }
};

// ── 이슈 #26: 라이브 목록 v5 열 전용 표기 ────────────────────────────────────
// SignedTrend/EntryQuality를 참고 점수 색상 스타일·`/100` 포맷·매수/매도 문구에 절대 연결하지 않는다(§2).
// %·승률·확률·성공 단어를 쓰지 않으며, 결측을 0으로 위장하지 않는다(스냅샷 테스트로 고정).

/** SignedTrend 전용 렌더 문자열: 부호 화살표 + 부호 있는 값(소수 1자리) + 추세 상태 라벨. null이면 "추세 미산정". */
export const signedTrendText = (
  state: string | null | undefined,
  signedTrend: number | null | undefined,
): string => {
  if (signedTrend == null || !Number.isFinite(signedTrend)) return "추세 미산정";
  const arrow = signedTrend > 0 ? "▲" : signedTrend < 0 ? "▼" : "—";
  const sign = signedTrend > 0 ? "+" : signedTrend < 0 ? "−" : "";
  return `${arrow} ${sign}${Math.abs(signedTrend).toFixed(1)} ${trendStateLabel(state)}`;
};

/**
 * 이슈 #181: 사이드바 미니 배지 점수 우선순위. 선택 종목의 K3 폴링 최신값(pin)이 있으면 그걸 쓰고,
 * 없으면(다른 종목 행이거나 아직 폴링 전) `structureSummary` 캐시 요약값으로 채운다.
 */
export const sidebarConfluenceScore = (
  pin: { symbol: string; score: number | null } | null | undefined,
  symbol: string,
  row: StructureSummaryRow | null | undefined,
): number | null => {
  if (pin && pin.symbol === symbol && pin.score != null) return pin.score;
  return row?.confluence?.score ?? null;
};

/** EntryQuality 표기(알림 문구용): 대표 후보가 없거나 값이 결측이면 "미평가" 고정 — null을 0으로 만들지 않는다(§2-4). */
export const entryQualityText = (
  preferredCandidateId: string | null | undefined,
  entryQuality: number | null | undefined,
): string =>
  !preferredCandidateId || entryQuality == null || !Number.isFinite(entryQuality)
    ? "미평가"
    : entryQuality.toFixed(1);

const finite = (value: number | null | undefined): value is number =>
  value != null && Number.isFinite(value);

export type EntryQualityDisplay = {
  /** 셀 본문. 숫자(소수 1자리) 또는 "후보 없음". */
  value: string;
  /** 숫자 옆 보조 문구(최근 후보 상태). 대표 후보이거나 후보가 없으면 null. */
  note: string | null;
  /** 어떤 후보의 값인지·왜 값이 없는지 설명하는 툴팁. */
  title: string;
  kind: string | null;
};

const ENTRY_QUALITY_SCALE = "후보 간 비교용 순위 지표 (0~100)";

/**
 * #407 EntryQuality 표기. 대표(READY) 후보 → 그 값, 없으면 최근 후보(부적합·만료 포함)의 값을 상태와 함께,
 * 후보 자체가 없으면 "후보 없음"과 사유. null을 0으로 만들지 않는다(§2-4).
 */
export const entryQualityDisplay = (
  row: StructureSummaryRow | null | undefined,
): EntryQualityDisplay => {
  if (row?.preferredCandidateId && finite(row.entryQuality))
    return {
      value: row.entryQuality.toFixed(1),
      note: null,
      title: `대표 후보 ${setupKindLabel(row.preferredKind)} · 진입 품질 ${row.entryQuality.toFixed(1)}\n${ENTRY_QUALITY_SCALE}`,
      kind: row.preferredKind ?? null,
    };
  const latest = row?.latestCandidate;
  if (latest && finite(latest.entryQuality)) {
    const state = candidateStateLabel(latest.state);
    const reasons = codeTexts(latest.rejectionCodes);
    return {
      value: latest.entryQuality.toFixed(1),
      note: state,
      title: [
        `최근 후보 ${setupKindLabel(latest.kind)} · ${state} · 진입 품질 ${latest.entryQuality.toFixed(1)}`,
        "대표 후보가 아니므로 진입 대상이 아닙니다",
        ...(reasons.length > 0 ? ["사유:", ...reasons.map((r) => `• ${r}`)] : []),
        ENTRY_QUALITY_SCALE,
      ].join("\n"),
      kind: latest.kind ?? null,
    };
  }
  const blockers = codeTexts(row?.warnings);
  return {
    value: "후보 없음",
    note: null,
    title: [
      "이번 세션에 생성된 구조 후보가 없습니다",
      row?.status && row.status !== "available" ? statusLabel(row.status) : null,
      ...(blockers.length > 0 ? ["현재 경고:", ...blockers.map((r) => `• ${r}`)] : []),
      ENTRY_QUALITY_SCALE,
    ]
      .filter((x): x is string => !!x)
      .join("\n"),
    kind: null,
  };
};

/** #407 경고 칩 툴팁: 숫자가 무엇을 세는지(구조 분석 입력·전제 경고 건수)와 각 항목. */
export const warningsTitle = (warnings: string[] | null | undefined): string => {
  const texts = codeTexts(warnings);
  return [
    `경고 ${texts.length}건 — 구조 분석의 입력·전제 경고 수입니다 (진입 판정 결과가 아님)`,
    ...texts.map((t) => `• ${t}`),
  ].join("\n");
};

/** D3 계약 문서는 `FLIPPED_*`, 실제 직렬화는 enum 이름 그대로인 `FLIPPEDSUPPORT`다. 둘 다 받는다. */
const normalizeRole = (role: string | null | undefined): string =>
  (role ?? "").toUpperCase().replace(/_/g, "");

export const zoneRoleLabel = (role: string | null | undefined): string => {
  switch (normalizeRole(role)) {
    case "SUPPORT":
      return "지지";
    case "RESISTANCE":
      return "저항";
    case "FLIPPEDSUPPORT":
      return "지지로 전환";
    case "FLIPPEDRESISTANCE":
      return "저항으로 전환";
    case "BROKEN":
      return "붕괴";
    case "UNRESOLVED":
      return "역할 미확정";
    default:
      return role ? `${role} (미등록)` : "역할 미상";
  }
};

export const zoneRoleGroup = (
  role: string | null | undefined,
): "support" | "resistance" | "neutral" => {
  switch (normalizeRole(role)) {
    case "SUPPORT":
    case "FLIPPEDSUPPORT":
      return "support";
    case "RESISTANCE":
    case "FLIPPEDRESISTANCE":
      return "resistance";
    default:
      return "neutral";
  }
};

export const sourceKindLabel = (kind: string | null | undefined): string => {
  const raw = (kind ?? "").toLowerCase();
  if (raw.includes("profile"))
    return "추정 거래량 프로파일(봉 기반 근사 · 실제 체결 분포 아님)";
  if (raw.includes("5m") || raw.includes("fiveminute")) return "5분 확정 피벗";
  if (raw.includes("1m") || raw.includes("oneminute")) return "1분 확정 피벗";
  if (raw.includes("pivot")) return "확정 피벗";
  if (raw.includes("orb")) return "개장 15분 범위(ORB15)";
  if (raw.includes("prev") || raw.includes("daily")) return "직전 일봉 컨텍스트";
  return kind ?? "원천 미상";
};

/** `invalidationQuality`(실제 상수)와 `invalidation`(계약 문서 예시) 양쪽을 같은 라벨로 받는다. */
export const qualityComponentLabel = (name: string | null | undefined): string => {
  const key = (name ?? "").replace(/Quality$/, "");
  switch (key) {
    case "invalidation":
      return "무효화 구조 품질";
    case "target":
      return "목표 구조 품질";
    case "room":
      return "목표까지 남은 공간";
    case "extension":
      return "진입가의 구조 대비 이격";
    case "triggerVolume":
      return "트리거 봉 거래량";
    case "alignment":
      return "추세 정합";
    case "reclaim":
      return "지지 회복 강도";
    default:
      return name ?? "—";
  }
};

/** DataQuality의 원천 이름(§16B): bars1m | bars5m | daily | quote | liquidity | volumeProfile | indicators | pivots5m. */
export const sourceNameLabel = (source: string | null | undefined): string => {
  switch (source) {
    case "bars1m":
      return "완료 1분봉";
    case "bars5m":
      return "완료 5분봉";
    case "daily":
      return "완료 일봉";
    case "quote":
      return "실시간 호가(체결가)";
    case "liquidity":
      return "호가 스프레드";
    case "volumeProfile":
      return "추정 거래량 프로파일(봉 기반 근사)";
    case "indicators":
      return "세션 지표(EMA·VWAP·ATR)";
    case "pivots5m":
      return "5분 확정 피벗";
    default:
      return source ?? "원천 미상";
  }
};

/** DataQuality source status(§16B): available | approximate | stale | missing. */
export const sourceStatusLabel = (status: string | null | undefined): string => {
  switch ((status ?? "").toLowerCase()) {
    case "available":
      return "사용 가능";
    case "approximate":
      return "근사값";
    case "stale":
      return "오래됨";
    case "missing":
      return "없음";
    default:
      return status ? `${status} (미등록)` : "상태 미상";
  }
};

// ── 코드 → 한국어 문장 ───────────────────────────────────────────────────────
// §13: "색깔만으로 사유를 대신하지 않는다". 모르는 코드는 감추지 말고 원문 그대로 노출한다(§19-9).

const CODE_TEXT: Record<string, string> = {
  // 계획·자격
  NO_TARGET_STRUCTURE: "목표 구조 없음 — 진입가 위에 자격을 갖춘 저항 구간이 없습니다",
  NO_INVALIDATION_STRUCTURE: "무효화 구조 없음 — 손절 기준이 될 지지 구간이 없습니다",
  NO_TARGET_ROOM: "다음 저항까지 남은 공간이 없습니다",
  ENTRY_INSIDE_RESISTANCE: "진입가가 자격 있는 저항 구간 안입니다 (위쪽 먼 저항으로 대체하지 않습니다)",
  COST_EXCEEDS_ROOM: "비용(수수료·스프레드)을 빼면 목표까지 남는 폭이 없습니다",
  INSUFFICIENT_REWARD_TO_RISK: "비용 반영 손익비가 기준에 못 미칩니다",
  RISK_TOO_WIDE: "손절 폭이 허용 위험을 넘습니다 (손절을 좁혀 통과시키지 않습니다)",
  STOP_NOT_BELOW_ENTRY: "계산된 손절이 진입가보다 낮지 않습니다",
  STOP_NOT_POSITIVE: "계산된 손절이 0 이하입니다",
  // 이슈 #43: netR은 비용 대비 비율이라 손절폭이 비용보다 좁으면 위험을 재지 못한다.
  STOP_INSIDE_COST: "손절 폭이 왕복 수수료보다 좁습니다",
  // 이슈 #43: 구조 무효화 기준이 아니라 체결 잡음에 걸리는 선이라 거절한다.
  STOP_INSIDE_NOISE: "손절 폭이 1분 ATR 절반보다 좁습니다 (체결 잡음 구간)",
  INVALID_ENTRY_REFERENCE: "진입 참고가가 유효하지 않습니다",
  ENTRY_REFERENCE_FROM_TRIGGER_CLOSE:
    "실시간 호가가 없어 트리거 봉 종가를 진입 참고가로 사용했습니다 (READY 아님)",
  TRIGGER_LOW_BELOW_INVALIDATION_ANCHOR: "트리거 봉 저가가 무효화 기준선 아래입니다",
  UNSUPPORTED_PRICE_TICK: "이 종목의 호가 단위를 확인할 수 없습니다",
  PRICE_BELOW_MINIMUM_SUPPORTED: "지원 최소 가격 미만입니다",
  ATR_NOISE: "손절 여유폭 근거: ATR 잡음 구간",
  SPREAD: "손절 여유폭 근거: 호가 스프레드",
  TICK_FLOOR: "손절 여유폭 근거: 최소 호가 단위 하한",
  BUFFER_FROM_TICK_ONLY: "ATR·스프레드가 없어 최소 호가 단위만으로 여유폭을 잡았습니다",

  // 추세·품질
  TREND_UNAVAILABLE: "추세를 판정할 근거가 부족합니다",
  MISSING_5M_STRUCTURE: "5분 확정 피벗이 부족합니다 — 피벗 확인 대기",
  // 이슈 #29: SetupDetector가 반등(REBOUND) 후보에 남기는 관측 note. 사전 미등록이라
  // 원문 fallback으로 노출되던 것을 등록한다 (서버 상수: NoteReadyWithout5mStructure).
  V5_READY_WITHOUT_5M_STRUCTURE:
    "5분 구조 확인 전 반등 진입 — 구조 결측 상태 표식",
  COUNTER_TREND_SETUP: "추세와 반대 방향의 후보입니다",
  PULLBACK_REQUIRES_UP_OR_TRANSITION: "눌림 후보는 상승·전환 추세에서만 성립합니다",
  // 이슈 #42: signedTrend<0에서 PULLBACK/BREAKOUT이 롱으로 승격되는 것을 막는 거절 사유.
  TREND_DIRECTION_OPPOSES_LONG: "추세 방향이 롱 진입과 반대입니다 (역방향 진입은 거절합니다)",
  NO_VOLUME_BASELINE: "거래량 기준선이 없어 트리거 거래량 품질을 계산할 수 없습니다",
  MISSING_REQUIRED_QUALITY_COMPONENT: "필수 품질 요소가 결측입니다 (품질 점수 없음)",
  ZERO_REQUIRED_QUALITY_COMPONENT: "필수 품질 요소가 0입니다",
  EFFICIENCY_UNAVAILABLE: "추세 효율(efficiency)을 계산할 수 없습니다",
  VWAP_UNAVAILABLE: "VWAP을 계산할 수 없습니다",
  ATR_UNAVAILABLE: "ATR을 계산할 수 없습니다",
  ATR_MISSING_AT_TOUCH: "접촉 시점 ATR이 없어 반응 크기를 정규화하지 못했습니다",
  EXCURSION_ATR_UNAVAILABLE: "반응 폭을 ATR로 정규화할 수 없습니다",

  // 구간
  STRENGTH_BELOW_MINIMUM: "구간 강도가 최소 기준 미만입니다",
  INSUFFICIENT_INDEPENDENT_EVIDENCE: "서로 독립적인 증거 계열이 부족합니다",
  PROFILE_ONLY:
    "추정 거래량 프로파일만 근거인 구간입니다 (봉 기반 근사이며 실제 체결 분포·수급이 아닙니다)",
  ZONE_BROKEN: "구간이 붕괴했습니다",
  ZONE_RETIRED: "구간이 폐기되었습니다",
  ROLE_UNRESOLVED: "구간 역할이 아직 확정되지 않았습니다",
  NO_DIRECTIONAL_ROLE: "방향성 역할(지지/저항)이 정해지지 않은 구간입니다",
  SUPPORT_CLOSE_BELOW_LOWER: "완료 종가가 지지 하단 아래로 마감했습니다",
  RESISTANCE_CLOSE_ABOVE_UPPER: "완료 종가가 저항 상단 위로 마감했습니다",
  FLIP_ATTEMPT_CLOSED_ABOVE: "역할 전환 재검증: 상단 위에서 마감",
  FLIP_ATTEMPT_CLOSED_BELOW: "역할 전환 재검증: 하단 아래에서 마감",
  FLIP_ATTEMPT_LEFT_ZONE: "역할 전환 재검증: 구간을 벗어남",
  FLIPPED_SUPPORT_BROKEN: "전환된 지지가 다시 붕괴했습니다",
  FLIPPED_RESISTANCE_BROKEN: "전환된 저항이 다시 붕괴했습니다",
  RESOLVED_ABOVE_UPPER: "접촉이 상단 위에서 종료되었습니다",
  RESOLVED_BELOW_LOWER: "접촉이 하단 아래에서 종료되었습니다",
  RETEST_IN_PROGRESS: "되돌림 재확인이 진행 중입니다",
  RETEST_HELD_ABOVE_UPPER: "되돌림이 상단 위에서 지지되었습니다",
  RETEST_HELD_BELOW_LOWER: "되돌림이 하단 아래에서 저항받았습니다",
  BREAKOUT_RETEST_NOT_CONFIRMED: "돌파 후 되돌림 확인이 아직 되지 않았습니다",
  ZONE_LINEAGE_MERGED: "구간이 병합되었습니다",
  ZONE_LINEAGE_SPLIT: "구간이 분리되었습니다",
  SOURCE_NOT_CONFIRMED_AT_CUTOFF: "기준 시각까지 확정되지 않은 원천은 제외했습니다",
  ZERO_VOLUME_PROFILE: "거래량이 0이라 프로파일을 만들 수 없습니다",
  PROFILE_NO_BARS: "프로파일을 만들 봉이 없습니다",
  PROFILE_BIN_LIMIT_UNRESOLVED: "프로파일 구간 수 제한을 해소하지 못했습니다",

  // 실시간 유지 조건
  LIVE_PRICE_BELOW_SUPPORT_LOWER: "실시간 가격이 지지 하단 아래입니다",
  LIVE_PRICE_NOT_ABOVE_BREAKOUT_LEVEL: "실시간 가격이 돌파 기준 위에 있지 않습니다",
  LIVE_PRICE_AT_OR_BELOW_STRUCTURAL_STOP: "실시간 가격이 구조적 손절 이하입니다",
  LIVE_PRICE_BROKE_INVALIDATION: "실시간 가격이 무효화 기준을 깼습니다",

  // 호가·비용
  MISSING_QUOTE: "실시간 호가가 없습니다",
  STALE_QUOTE: "호가가 오래되었습니다 (30초 만료)",
  QUOTE_IN_FUTURE: "호가 시각이 미래입니다 (신뢰하지 않음)",
  // 이슈 #29: spread=0 가정은 보수성 보장이 없으므로 "보수적 가정"으로 단정하지 않고 사실대로 적는다.
  MISSING_LIQUIDITY_COST: "호가 스프레드를 확인할 수 없어 호가 비용이 반영되지 않았습니다",
  SPREAD_MISSING: "스프레드 정보 없음",
  SPREAD_STALE: "스프레드가 오래되었습니다",
  SPREAD_CROSSED: "매수·매도 호가가 역전되어 사용하지 않았습니다",
  SPREAD_NON_POSITIVE_PRICE: "호가 가격이 0 이하라 사용하지 않았습니다",
  SPREAD_NON_POSITIVE_SIZE: "호가 잔량이 0 이하라 사용하지 않았습니다",
  SPREAD_SIZE_UNKNOWN: "호가 잔량을 알 수 없어 스프레드를 비용으로 쓰지 않았습니다",
  SPREAD_FUTURE_TIMESTAMP: "호가 시각이 미래입니다",
  SPREAD_OUTSIDE_SESSION: "정규장 밖 호가입니다",

  // 데이터·세션
  INSUFFICIENT_1M_BARS: "완료된 1분봉이 부족합니다 (워밍업)",
  NO_COMPLETED_BAR: "완료된 봉이 없습니다",
  NO_COMPLETED_TRIGGER_BAR: "완료된 트리거 봉이 없습니다",
  NO_CONTIGUOUS_PREVIOUS_BAR: "직전 봉이 연속되지 않습니다",
  STALE_LATEST_BAR: "가장 최근 완료 봉이 오래되었습니다",
  BAR_GAP: "봉 결손 구간이 있습니다",
  BAR_CONFLICT: "같은 시각에 서로 다른 봉이 들어왔습니다",
  INVALID_BAR_VALUES: "봉 값이 유효하지 않아 제외했습니다",
  INVALID_BAR_ORDERING: "봉 순서가 어긋나 제외했습니다",
  BAR_CROSSES_SESSION_END: "세션 종료를 넘는 봉은 제외했습니다",
  FUTURE_BAR_INPUT_REJECTED: "미래 시각 봉 입력을 거부했습니다",
  OUTSIDE_REGULAR_SESSION: "정규장 밖 데이터입니다",
  NO_REGULAR_SESSION: "정규장 세션 정보가 없습니다",
  MISSING_DAILY_CONTEXT: "일봉 컨텍스트가 없습니다",
  DAILY_INPUT_TRIMMED: "일봉 입력을 정책 범위로 잘랐습니다",
  INVALID_DAILY_BAR_DROPPED: "유효하지 않은 일봉을 제외했습니다",
  CURRENT_DAILY_BAR_REMOVED: "진행 중인 당일 일봉을 제외했습니다 (미래 데이터 금지)",
  CURRENT_DAILY_BAR_REJECTED: "진행 중인 당일 일봉을 사용하지 않았습니다",

  // 수명주기·게이트
  CANDIDATE_TTL_EXPIRED: "후보 유효시간이 지났습니다",
  DUPLICATE_TRIGGER_GUARD: "같은 트리거가 이미 처리되었습니다",
  TOMBSTONED_EVENT_NOT_REVIVED: "종결된 이벤트를 되살리지 않습니다",
  NEW_TRIGGER_SUPPRESSED: "신규 트리거를 억제했습니다",
  WATERMARK_SEEDED_NO_NEW_TRIGGER: "재시작 직후라 이번 봉에서는 신규 트리거를 만들지 않습니다",
  BAR_ALREADY_EVALUATED: "이미 평가한 봉입니다",
  MISSED_BARS_REPLAY_ONLY: "빠진 봉은 재생 분석에만 쓰고 신규 트리거로 삼지 않습니다",
  AFTER_ENTRY_CUTOFF: "장 마감 전 신규 진입 차단 시간대입니다",
  ACTIVE_ENTRY_WIRING_PENDING:
    "active 모드라도 신규 진입 배선은 아직 연결되지 않았습니다 (D6 범위)",
  // 이슈 #47: 같은 zone lineage에서 직전 돌파 발동으로부터 30분 이내면 새 트리거라도 승격하지 않는다.
  BREAKOUT_ZONE_COOLDOWN: "같은 저항 구간의 돌파 재발동을 30분 동안 억제합니다",

  // v5 진입 관측 (D6 active 배선)
  V5_ENTRY_COMMITTED: "v5 구조 계획으로 진입을 생성했습니다",
  V5_ENTRY_BLOCKED_BY_OPEN_TRADE: "이 종목에 OPEN 거래가 있어 신규 진입을 보류했습니다",
  V5_ENTRY_PLAN_INVALID: "동결 계획의 가격 순서가 성립하지 않아 진입을 거절했습니다",
  V5_ENTRY_PORT_UNAVAILABLE: "진입 포트가 배선되지 않아 진입을 보류했습니다 (설정 문제)",

  // 관측 저장 한도 (이슈 #44) — 서버 상수는 PascalCase(ObservationStorageLimited)다. 다른 코드와
  // 대소문자 형식이 다르지만 codeText()는 원문 그대로 대조하므로 그대로 키로 쓴다.
  ObservationStorageLimited: "관측 저장 한도로 주기 요약 일부가 축약되었습니다",
  ObservationCoreStorageLimited: "관측 저장 한도로 핵심 관측까지 누락되어 전체 검증이 불가합니다",
};

// ── 결측 컴포넌트 이름 → 한국어 설명 ────────────────────────────────────────
// missingComponents는 경고/이벤트 "코드"(SCREAMING_SNAKE)가 아니라 계산 구성요소
// "이름"(camelCase)이다. 의미는 "이 요소를 계산할 근거(확정 봉·피벗·유효 ATR 등)가
// 아직 부족해 계산에서 생략했다"이며, 계산 오류나 0점이 아니다(§16A: 결측을 0으로
// 대체하지 않는다. 값 0은 유효한 값이고 결측이 아니다).
// 출처 전수 대조: server/Domain/Structure/TrendEvaluator.cs (추세 7종),
// server/Domain/Structure/ZoneEvaluator.cs (구간 강도 4종).

const MISSING_COMPONENT_TEXT: Record<string, string> = {
  // 추세(TrendEvaluator) — trend.missingComponents
  structureDirection:
    "구조 방향 — 구조 방향을 계산할 확정 5분 피벗 구조가 아직 부족합니다 (피벗 확인 대기)",
  atr1m: "1분 ATR — 유효한 ATR(>0)이 아직 없어 ATR로 정규화하는 요소를 계산하지 못했습니다",
  vwap: "VWAP — 세션 거래량이 아직 없어 VWAP을 계산하지 못했습니다",
  efficiency: "추세 효율 — 경로 효율을 계산할 완료 봉이 아직 부족합니다",
  emaDirection: "EMA 정렬 방향 — EMA(9·21) 또는 유효 ATR이 아직 부족합니다",
  slopeDirection: "EMA 기울기 방향 — 기울기 비교 구간의 완료 봉 또는 유효 ATR이 아직 부족합니다",
  vwapDirection: "VWAP 대비 방향 — VWAP 또는 유효 ATR이 아직 없어 계산하지 못했습니다",
  // 구간 강도(ZoneEvaluator) — zone.missingEvidence (기하평균에서 생략된 요소)
  touchEvidence: "접촉 증거 — 완료된 접촉 반응이 아직 없어 계산하지 못했습니다",
  reactionEvidence: "반응 증거 — 반응 크기를 정규화할 완료 반응·ATR이 아직 없습니다",
  recency: "최근성 — 최근성을 계산할 확정 원천이 아직 없습니다",
  confluence: "증거 중첩 — 이 구간을 지지하는 원천 계열이 아직 없습니다",
};

/**
 * 결측 컴포넌트 이름 하나를 "계산 근거 부족" 설명으로. 코드 사전(codeText)과 분리된
 * 별도 경로다(§19-9: 모르는 이름은 감추지 않고 원문 그대로 노출).
 */
export const missingComponentText = (name: string): string => {
  const raw = (name ?? "").trim();
  if (!raw) return "";
  return MISSING_COMPONENT_TEXT[raw] ?? `${raw} — 설명이 등록되지 않은 결측 요소입니다 (원문 표시)`;
};

export const missingComponentTexts = (names: string[] | null | undefined): string[] =>
  arr(names).map(missingComponentText).filter((x) => x.length > 0);

/** 코드 하나를 문장으로. 접미 카운트(`...x3`)와 `CODE:detail` 형태를 함께 처리한다. */
export const codeText = (code: string): string => {
  const raw = (code ?? "").trim();
  if (!raw) return "";
  if (CODE_TEXT[raw]) return CODE_TEXT[raw];

  const repeated = /^(.*?)x(\d+)$/.exec(raw);
  if (repeated && CODE_TEXT[repeated[1]]) return `${CODE_TEXT[repeated[1]]} (${repeated[2]}회)`;

  const head = raw.split(":")[0];
  if (head !== raw && CODE_TEXT[head])
    return `${CODE_TEXT[head]} (상세: ${raw.slice(head.length + 1)})`;

  return `${raw} — 설명이 등록되지 않은 코드입니다 (원문 표시)`;
};

export const codeTexts = (codes: string[] | null | undefined): string[] =>
  arr(codes).map(codeText).filter((x) => x.length > 0);

// ── 기본 화면 / 진단 상세 분리 (이슈 #29) ──────────────────────────────────
// §19-9("모르는 코드를 감추지 않는다")는 **데이터를 버리지 말라**는 규칙이지 원시 코드를
// 기본 화면에 그대로 찍으라는 규칙이 아니다. 기본 화면은 사전에 등록된 문장만 보여주고,
// 미등록 원시 코드(WidthFromTickOnly·EstimatedVolumeProfile·ProfileOnlyTemporaryId 등)는
// 접힌 진단 상세에 원문 그대로 남긴다. codeText()의 fallback 자체는 그대로 유지한다.

/** 코드가 사전에 등록되어 있는지. 접미 카운트(`...xN`)와 `CODE:detail` 형태도 등록으로 본다. */
export const isKnownCode = (code: string): boolean => {
  const raw = (code ?? "").trim();
  if (!raw) return false;
  if (CODE_TEXT[raw]) return true;
  const repeated = /^(.*?)x(\d+)$/.exec(raw);
  if (repeated && CODE_TEXT[repeated[1]]) return true;
  const head = raw.split(":")[0];
  return head !== raw && CODE_TEXT[head] != null;
};

/** 기본 화면용 — 등록된 코드만 한국어 문장으로. 미등록 코드는 여기서 나오지 않는다. */
export const knownCodeTexts = (codes: string[] | null | undefined): string[] =>
  arr(codes).filter(isKnownCode).map(codeText);

/** 진단 상세용 — 사전에 없는 코드의 원문. 서버 데이터를 버리지 않기 위한 보존 경로다. */
export const unknownCodes = (codes: string[] | null | undefined): string[] =>
  arr(codes)
    .map((c) => (c ?? "").trim())
    .filter((c) => c.length > 0 && !isKnownCode(c));
