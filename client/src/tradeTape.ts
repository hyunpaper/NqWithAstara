// 이슈 #133 — 체결강도 원천 · 블록 체결 표시 헬퍼(순수 함수).

/** 체결강도 원천 — 서버 `flowSource` 값. 모르는 값은 표본 없음으로 본다. */
export type FlowSource = "ws" | "rest" | "none";

/** 체결강도가 실시간 틱인지 REST 체결 내역 보정인지 알려 준다. */
export const flowSourceLabel = (source: string | null | undefined): string =>
  source === "ws"
    ? "실시간 틱"
    : source === "rest"
      ? "체결 내역 보정"
      : "표본 없음";

/** 블록 체결 건수. 방향을 알 수 없다는 사실을 값에 함께 적는다. */
export const blockTradeLabel = (count: number | null | undefined): string =>
  count == null ? "—" : `${count}건 (방향 없음)`;
