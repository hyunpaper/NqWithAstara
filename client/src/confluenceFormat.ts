// 이슈 #168 — 컨플루언스 게이지·기법별 막대 표시 규칙(순수 함수). 색·라벨 판정을
// 컴포넌트에서 분리해 단위 테스트 가능하게 한다(newsFormat.ts와 같은 패턴).
import type { ConfluenceTechnique } from "./confluenceTypes";

export type GaugeTone = "bearish" | "bullish" | "neutral";

const BEARISH_THRESHOLD = -0.3;
const BULLISH_THRESHOLD = 0.3;

/** 게이지·배지 색 판정: ≤−0.3 적, ≥+0.3 녹, 그 외 회. null은 중립(데이터 없음)과 같은 색으로 둔다. */
export const gaugeTone = (score: number | null | undefined): GaugeTone => {
  if (score == null || !Number.isFinite(score)) return "neutral";
  if (score <= BEARISH_THRESHOLD) return "bearish";
  if (score >= BULLISH_THRESHOLD) return "bullish";
  return "neutral";
};

/** 기법 라벨 한국어 사전(§C4). 미등록 기법명은 원문 그대로 폴백한다. */
const TECHNIQUE_LABELS: Record<string, string> = {
  MACD: "MACD",
  RSI: "RSI",
  BB_PERCENT_B: "볼린저 %B",
  ADX_DMI: "ADX/DMI",
  VWAP_DEVIATION: "VWAP 이격",
  RVOL: "상대거래량",
  ATR_CHANNEL: "ATR 채널",
  ORB15: "개장 레인지",
  RS_QQQ: "상대강도(QQQ)",
  OBI: "호가 불균형",
};

export const techniqueLabel = (name: string | null | undefined): string =>
  (name && TECHNIQUE_LABELS[name]) || name || "—";

/** 막대 폭(%) — score(−1..+1)를 좌우 폭으로. null/warmup은 0으로 그린다(호출 측에서 점선 처리). */
export const barWidthPercent = (score: number | null | undefined): number => {
  if (score == null || !Number.isFinite(score)) return 0;
  return Math.min(1, Math.abs(score)) * 100;
};

/** 막대 불투명도 — confidence(0..1)를 그대로 쓰되 결측이면 완전 불투명(정보 없음을 흐리게 숨기지 않는다). */
export const barOpacity = (confidence: number | null | undefined): number => {
  if (confidence == null || !Number.isFinite(confidence)) return 1;
  return Math.min(1, Math.max(0, confidence));
};

/** warmup 여부 최종 판정: 서버 status가 warmup이거나, 해당 기법 자체가 warmup 플래그다. */
export const isTechniqueWarmup = (
  technique: Pick<ConfluenceTechnique, "warmup">,
  statusWarmup: boolean,
): boolean => statusWarmup || technique.warmup;

/** 전체 기법이 warmup이면(=기여 기법 0) "워밍업 중" 문구를 보인다. */
export const allWarmup = (
  status: "ready" | "warmup",
  techniques: ConfluenceTechnique[],
): boolean => status === "warmup" || techniques.length === 0 || techniques.every((t) => t.warmup);

/** 사이드바 미니 배지 숫자: 소수 2자리. null은 표시하지 않는다(호출 측에서 배지 자체를 숨긴다). */
export const scoreText2 = (score: number | null | undefined): string =>
  score == null || !Number.isFinite(score) ? "—" : `${score >= 0 ? "+" : ""}${score.toFixed(2)}`;
