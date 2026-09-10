// 이슈 #26 PR-3 — v5 알림 소비 계획(승인 설계안 §4 "FE는 표시·소리만 담당").
//
// 발행 판단은 서버(StructureAlertPublisher)에 있고, 여기서는 `/api/state`의 structureEvents를
// seq 기준으로만 소비한다. 중복 방지 3중 중 FE 몫 2중이 이 모듈이다:
// - seq seed: 최초 스냅샷(lastSeq=null)은 알림 없이 최대 seq만 저장한다 → 새로고침/재마운트가
//   과거 이벤트를 다시 울리지 않는다(승인 단서 2 재시작 회귀).
// - Notification tag: `astra-v5-${eventId}` — OS 수준 중복 흡수.
// v5 이벤트에는 심볼당 5분 스로틀을 적용하지 않는다(EventId가 유일하고, READY 직후 ENTERED를
// 삼키면 안 된다). 문구에 v4 점수·"N점"·%·승률·확률을 쓰지 않는다(§4 알림 문구).
import { StructureEventRow, codeText, entryQualityText } from "./structureTypes";

export type NotificationSpec = {
  symbol: string;
  title: string;
  body: string;
  /** OS 수준 중복 흡수용 Notification tag. 이벤트·종류 단위로 유일하다. */
  tag: string;
  seq: number;
};

export type V5AlertPlan = {
  notifications: NotificationSpec[];
  /** 다음 비교 기준 seq. 알림 on/off와 무관하게 항상 전진시켜 저장해야 한다. */
  nextSeq: number;
};

const dollars = (value: number | null | undefined): string =>
  value == null || !Number.isFinite(value) ? "—" : `$${value.toFixed(2)}`;

/** §4 알림 문구: 제목 `AAPL V5 READY · PULLBACK`, 본문 `진입 품질 61.2 · $100.00`. */
const spec = (event: StructureEventRow): NotificationSpec | null => {
  const symbol = event.symbol ?? "";
  if (!symbol || !event.eventId) return null;
  const kind = event.kind ? ` · ${event.kind}` : "";
  const quality = entryQualityText(event.eventId, event.entryQuality);
  const tagKey = `astra-v5-${event.eventId}:${event.type ?? ""}`;
  switch (event.type) {
    case "V5_READY":
      return {
        symbol,
        title: `${symbol} V5 READY${kind}`,
        body: `진입 품질 ${quality} · ${dollars(event.quotePrice)}`,
        tag: tagKey,
        seq: event.seq,
      };
    case "V5_ENTERED":
      return {
        symbol,
        title: `${symbol} V5 진입 완료${kind}`,
        body: `손절 ${dollars(event.stop)} · 목표 ${dollars(event.target)}`,
        tag: tagKey,
        seq: event.seq,
      };
    case "V5_BLOCKED":
      return {
        symbol,
        title: `${symbol} V5 진입 보류${kind}`,
        body: event.reason ? codeText(event.reason) : "진입이 차단되었습니다",
        tag: tagKey,
        seq: event.seq,
      };
    default:
      // 모르는 이벤트 종류는 울리지 않는다(서버가 종류를 추가해도 화면이 오작동하지 않게).
      return null;
  }
};

/**
 * structureEvents를 seq 기준으로 소비한다.
 * - lastSeq === null(최초 스냅샷·새로고침 직후)이면 알림 없이 seed만 한다.
 * - 그 외에는 seq > lastSeq인 이벤트만 seq 오름차순으로 알린다.
 */
export const planV5Notifications = (
  events: StructureEventRow[] | null | undefined,
  lastSeq: number | null,
): V5AlertPlan => {
  const valid = (events ?? []).filter((e) => e != null && Number.isFinite(e.seq));
  const maxSeq = valid.reduce((max, e) => Math.max(max, e.seq), lastSeq ?? 0);
  if (lastSeq === null) return { notifications: [], nextSeq: maxSeq };
  const notifications = valid
    .filter((e) => e.seq > lastSeq)
    .sort((a, b) => a.seq - b.seq)
    .map(spec)
    .filter((x): x is NotificationSpec => x !== null);
  return { notifications, nextSeq: maxSeq };
};

/**
 * v4 SETUP/BREAKOUT 푸시·소리 허용 여부(§4 off/shadow 호환).
 * active에서는 v5 이벤트만 울린다 — v4 배지·점수 표시는 "참고" 라벨로 유지된다(승인 단서 1).
 */
export const v4PushEnabled = (mode: string | null | undefined): boolean => mode !== "active";
