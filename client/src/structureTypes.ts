// v5 구조 엔진 D4 — 화면이 사용하는 API 계약과 표기 규칙.
//
// 계약 출처: STRUCTURE-ENGINE-DESIGN.md §11(DTO) / §12(API) / §13(화면 요구사항).
// 서버(D3)의 `StructureQueryResponse` / `StructureAnalysisView`가 원본이며 JSON은 camelCase다.
// 여기서는 **추측한 값을 만들지 않는다**: 서버가 보내지 않는 필드는 optional + null 안전으로 두고,
// 없는 근거를 화면에서 그리지 않는다(§13 "구조가 없으면 선을 임의로 그리지 않는다").
//
// enum류(status/state/role/kind/code)는 문자열 union으로 좁히지 않고 `string`으로 받는다.
// D3가 값을 추가해도 화면이 깨지지 않게 하고, 모르는 값은 "원문 그대로 + 미등록" 으로 표기한다.

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
  analysisAsOf?: string | null;
  quoteAt?: string | null;
  warnings?: string[] | null;
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
  COUNTER_TREND_SETUP: "추세와 반대 방향의 후보입니다",
  PULLBACK_REQUIRES_UP_OR_TRANSITION: "눌림 후보는 상승·전환 추세에서만 성립합니다",
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
  MISSING_LIQUIDITY_COST: "호가 스프레드를 확인할 수 없어 비용을 보수적으로 가정했습니다",
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
};

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
