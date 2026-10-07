// 이슈 #168 — 컨플루언스 게이지·기법별 막대 표시 규칙(순수 함수). 색·라벨 판정을
// 컴포넌트에서 분리해 단위 테스트 가능하게 한다(newsFormat.ts와 같은 패턴).
import type { ConfluenceContributor, ConfluenceTechnique } from "./confluenceTypes";

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
  CANDLE: "캔들 확인",
  MTA_ALIGN: "다중 시간대 정렬",
  SQUEEZE: "변동성 스퀴즈",
  VOL_BREAKOUT: "변동성 돌파",
  RVOL_DAILY: "상대거래량(일)",
  LR_DELTA: "체결 델타",
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

/** 이슈 #379: 기여 요소 한 줄 표기 — 지표 이름·현재 값·한 줄 해석·기여 점수. */
export type ContributorDisplay = {
  label: string;
  detail: string;
  contribution: string;
};

const MISSING = "데이터 없음";

const fmt = (v: number | null | undefined, digits = 1): string | null =>
  v == null || !Number.isFinite(v) ? null : v.toFixed(digits);

/** 기법별 현재 값 + 한 줄 해석. evidence가 결측이면 "데이터 없음". */
const readContributor = (c: ConfluenceContributor): string => {
  const e = c.evidence;
  const up = (c.score ?? 0) >= 0;
  switch (c.name) {
    case "RSI": {
      const v = fmt(e.rsi, 0);
      if (v == null) return MISSING;
      const state = e.rsi != null && e.rsi >= 70 ? "과열" : e.rsi != null && e.rsi <= 30 ? "과매도" : "중립권";
      return `RSI ${v} ${state}`;
    }
    case "MACD": {
      const v = fmt(e.histogram, 2);
      return v == null ? MISSING : `MACD 히스토그램 ${v} ${up ? "상승 모멘텀" : "하락 모멘텀"}`;
    }
    case "BB_PERCENT_B": {
      const v = fmt(e.percentB, 2);
      if (v == null) return MISSING;
      const state = e.percentB != null && e.percentB > 1 ? "상단 돌파" : e.percentB != null && e.percentB < 0 ? "하단 이탈" : "밴드 안";
      return `%B ${v} ${state}`;
    }
    case "ADX_DMI": {
      const v = fmt(e.adx, 0);
      return v == null ? MISSING : `ADX ${v} ${up ? "+DI 우위" : "−DI 우위"}`;
    }
    case "VWAP_DEVIATION": {
      const v = fmt(e.deviation, 1);
      return v == null ? MISSING : `VWAP ${up ? "위" : "아래"} ${Math.abs(e.deviation ?? 0).toFixed(1)}σ`;
    }
    case "RVOL":
    case "RVOL_DAILY": {
      const v = fmt(e.relativeVolume ?? e.relativeVolumeDaily, 1);
      return v == null ? MISSING : `상대거래량 ${v}배 ${up ? "상승 거래 우위" : "하락 거래 우위"}`;
    }
    case "RS_QQQ": {
      const v = fmt(e.differencePercent, 1);
      return v == null ? MISSING : `QQQ 대비 ${v}% ${up ? "강세" : "약세"}`;
    }
    case "OBI": {
      return `호가 ${up ? "매수" : "매도"} 우위`;
    }
    case "ORB15": {
      return `개장 레인지 ${up ? "상향 돌파" : "하향 이탈"}`;
    }
    case "CANDLE": {
      return `${up ? "강세" : "약세"} 캔들`;
    }
    case "MTA_ALIGN": {
      const a = fmt(e.aligned, 0);
      const t = fmt(e.timeframes, 0);
      const count = a != null && t != null ? `${a}/${t} ` : "";
      return `다중 시간대 ${count}${up ? "상방 정렬" : "하방 정렬"}`;
    }
    case "SQUEEZE": {
      return e.released === 1 ? "스퀴즈 해소" : e.squeeze === 1 ? "스퀴즈 중" : up ? "변동성 상방" : "변동성 하방";
    }
    case "VOL_BREAKOUT": {
      return `변동성 ${up ? "상향 돌파" : "하향 이탈"}`;
    }
    case "LR_DELTA": {
      return `체결 ${up ? "매수 우위" : "매도 우위"}`;
    }
    case "ATR_CHANNEL": {
      return `ATR 채널 ${up ? "상단" : "하단"}`;
    }
    default:
      return up ? "상승 쪽" : "하락 쪽";
  }
};

/** 기여 요소 한 개를 툴팁 한 줄로 변환한다(지표 이름·현재 값/해석·기여 점수). */
export const describeContributor = (c: ConfluenceContributor): ContributorDisplay => ({
  label: techniqueLabel(c.name),
  detail: readContributor(c),
  contribution: scoreText2(c.contribution),
});
