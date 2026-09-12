// 이슈 #132 — 매매 보조지표 표시 헬퍼.
// 회전율은 서버가 계산한 값(당일 누적 거래량 / 상장주식수)을 그대로 표시만 한다.
// 상장주식수 메타가 없으면 0이 아니라 "—"로 둔다 — 결측을 값처럼 읽히게 하지 않는다.

/** 회전율(%) 표시. null·비유한값은 결측 기호다. */
export const turnoverText = (v: number | null | undefined): string =>
  v == null || !Number.isFinite(v) ? "—" : `${v.toFixed(2)}%`;
