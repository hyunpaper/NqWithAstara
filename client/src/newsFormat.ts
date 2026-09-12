// 이슈 #152 — 뉴스 감성 배지 표시 규칙(순수 함수). 색·라벨 판정과 렌더 여부를
// 컴포넌트에서 분리해 단위 테스트 가능하게 한다.
import type { NewsSentimentLabel } from "./newsTypes";

export type Badge = { label: string; className: string };

const POSITIVE_THRESHOLD = 2;
const NEGATIVE_THRESHOLD = -2;

/** 점수 기반 배지(사이드바 종목 행·헤더 시장 분위기). ±2 미만은 중립, null은 배지 없음. */
export const scoreBadge = (score: number | null | undefined): Badge | null => {
  if (score == null || Number.isNaN(score)) return null;
  if (score >= POSITIVE_THRESHOLD) return { label: `호재 +${score.toFixed(1)}`, className: "news-badge positive" };
  if (score <= NEGATIVE_THRESHOLD) return { label: `악재 ${score.toFixed(1)}`, className: "news-badge negative" };
  return { label: `중립 ${score >= 0 ? "+" : ""}${score.toFixed(1)}`, className: "news-badge neutral" };
};

/** 점수 배지 title(§#157). "N건 · 최근 M분" — count·latestAt이 없으면 건수/경과분을 생략한다. */
export const scoreBadgeTitle = (
  count: number | null | undefined,
  latestAt: string | null | undefined,
  nowMs: number = Date.now(),
): string => {
  if (count == null || count <= 0) return "뉴스 감성";
  const at = latestAt ? new Date(latestAt).getTime() : NaN;
  if (Number.isNaN(at)) return `${count}건`;
  const minutes = Math.max(0, Math.floor((nowMs - at) / MINUTE));
  return `${count}건 · 최근 ${minutes}분`;
};

/** 기사 개별 판정 배지(뉴스 패널). unclassified는 배지를 그리지 않는다. */
export const sentimentBadge = (sentiment: NewsSentimentLabel): Badge | null => {
  switch (sentiment) {
    case "positive":
      return { label: "호재", className: "news-badge positive" };
    case "negative":
      return { label: "악재", className: "news-badge negative" };
    case "neutral":
      return { label: "중립", className: "news-badge neutral" };
    default:
      return null;
  }
};

export const inputKindLabel = (kind: string | null | undefined): string =>
  kind === "summary" ? "요약 기반" : kind === "body" ? "본문 기반" : kind === "headline" ? "제목 기반" : "";

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** KST 상대 시각("3분 전" 등). 절대 시각은 협정시 차이가 없는 상대 diff라 로컬 TZ와 무관하다. */
export const relativeTimeKo = (iso: string | null | undefined, nowMs: number = Date.now()): string => {
  if (!iso) return "시각 없음";
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return "시각 없음";
  const diff = nowMs - t;
  if (diff < 0) return "방금 전";
  if (diff < MINUTE) return "방금 전";
  if (diff < HOUR) return `${Math.floor(diff / MINUTE)}분 전`;
  if (diff < DAY) return `${Math.floor(diff / HOUR)}시간 전`;
  return `${Math.floor(diff / DAY)}일 전`;
};

/** KST 절대 시각(타이틀·툴팁용). */
export const absoluteTimeKst = (iso: string | null | undefined): string => {
  if (!iso) return "시각 없음";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "시각 없음";
  return `${d.toLocaleString("ko-KR", { timeZone: "Asia/Seoul", hour12: false })} KST`;
};
