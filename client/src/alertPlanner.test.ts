// 이슈 #26 PR-3 — v5 알림 소비 계획 검증(승인 설계안 §4·§6 FE 테스트 12·13, 승인 단서 2).
// 관점: 같은 seq 재수신·재마운트(새로고침)에서 알림 0회, Notification tag로 OS 중복 흡수,
// 문구에 v4 점수·점·%·승률·확률이 없다. active에서만 v4 푸시가 꺼진다(배지 표시는 별개).
import { describe, expect, it } from "vitest";
import { planV5Notifications, v4PushEnabled } from "./alertPlanner";
import type { StructureEventRow } from "./structureTypes";

const ready = (seq: number, over: Partial<StructureEventRow> = {}): StructureEventRow => ({
  seq,
  type: "V5_READY",
  symbol: "AAPL",
  eventId: `AAPL|event-${seq}`,
  kind: "PULLBACK",
  entryQuality: 61.24,
  quotePrice: 100,
  ...over,
});

describe("seq seed — 재시작(새로고침) 중복 회귀 (승인 단서 2)", () => {
  it("최초 스냅샷(lastSeq=null)은 과거 이벤트를 울리지 않고 최대 seq만 저장한다", () => {
    const plan = planV5Notifications([ready(1), ready(2)], null);
    expect(plan.notifications).toEqual([]);
    expect(plan.nextSeq).toBe(2);
  });

  it("새로고침(재마운트): lastSeq가 null로 리셋돼도 같은 이벤트 배열로 알림 0회", () => {
    const events = [ready(1), ready(2, { type: "V5_ENTERED", stop: 99.12, target: 101.78 })];
    const before = planV5Notifications(events, null); // 첫 마운트: seed
    expect(before.notifications).toHaveLength(0);
    // 새로고침 → ref가 초기화된 새 마운트. 서버는 같은 이벤트(seq 유지)를 다시 내려준다.
    const remounted = planV5Notifications(events, null);
    expect(remounted.notifications).toHaveLength(0);
    expect(remounted.nextSeq).toBe(2);
  });

  it("seed 후 도착한 새 seq만 순서대로 알린다", () => {
    const seeded = planV5Notifications([ready(1)], null);
    const plan = planV5Notifications([ready(1), ready(3), ready(2)], seeded.nextSeq);
    expect(plan.notifications.map((n) => n.seq)).toEqual([2, 3]);
    expect(plan.nextSeq).toBe(3);
  });

  it("같은 seq를 다시 받으면(폴링 반복) 알림 0회 — seq는 뒤로 가지 않는다", () => {
    const plan = planV5Notifications([ready(1), ready(2)], 2);
    expect(plan.notifications).toEqual([]);
    expect(plan.nextSeq).toBe(2);
  });

  it("이벤트가 비어 있으면 seed는 0에서 시작해 이후 첫 이벤트를 울린다", () => {
    const seeded = planV5Notifications([], null);
    expect(seeded.nextSeq).toBe(0);
    expect(planV5Notifications([ready(1)], seeded.nextSeq).notifications).toHaveLength(1);
  });
});

describe("알림 문구·tag (§4)", () => {
  it("READY: 제목 `SYM V5 READY · KIND`, 본문 `진입 품질 X · $가격`", () => {
    const [n] = planV5Notifications([ready(2)], 1).notifications;
    expect(n.title).toBe("AAPL V5 READY · PULLBACK");
    expect(n.body).toBe("진입 품질 61.2 · $100.00");
    expect(n.tag).toBe("astra-v5-AAPL|event-2:V5_READY");
  });

  it("ENTERED: 동결 계획의 손절·목표를 그대로 보여준다", () => {
    const [n] = planV5Notifications(
      [ready(2, { type: "V5_ENTERED", stop: 99.12, target: 101.78 })],
      1,
    ).notifications;
    expect(n.title).toBe("AAPL V5 진입 완료 · PULLBACK");
    expect(n.body).toBe("손절 $99.12 · 목표 $101.78");
  });

  it("BLOCKED: 사유 코드를 한국어 사전으로 풀어서 보여준다", () => {
    const [n] = planV5Notifications(
      [ready(2, { type: "V5_BLOCKED", reason: "V5_ENTRY_BLOCKED_BY_OPEN_TRADE" })],
      1,
    ).notifications;
    expect(n.title).toBe("AAPL V5 진입 보류 · PULLBACK");
    expect(n.body).toContain("OPEN 거래가 있어 신규 진입을 보류");
  });

  it("진입 품질 결측이면 본문도 '미평가'다 — 0으로 위장하지 않는다", () => {
    const [n] = planV5Notifications([ready(2, { entryQuality: null })], 1).notifications;
    expect(n.body).toContain("미평가");
    expect(n.body).not.toMatch(/품질 0(\.0)?/);
  });

  it("READY 직후 ENTERED가 같은 배치에 와도 둘 다 알린다 (5분 스로틀 없음) — tag는 서로 다르다", () => {
    const events = [
      ready(2),
      { ...ready(2), seq: 3, type: "V5_ENTERED", stop: 99.12, target: 101.78 },
    ];
    const plan = planV5Notifications(events, 1);
    expect(plan.notifications).toHaveLength(2);
    expect(new Set(plan.notifications.map((n) => n.tag)).size).toBe(2);
  });

  it("모르는 이벤트 종류·필드 결손(symbol/eventId 없음)은 울리지 않는다", () => {
    const plan = planV5Notifications(
      [
        ready(2, { type: "V5_SOMETHING_NEW" }),
        ready(3, { symbol: null }),
        ready(4, { eventId: null }),
      ],
      1,
    );
    expect(plan.notifications).toEqual([]);
    expect(plan.nextSeq).toBe(4); // 소비는 됐다 — 다음 폴링에서 다시 울리려 들지 않는다.
  });

  it("금지 표현: 제목·본문에 %·승률·확률·성공·점·/100·매수 문구가 없다 (§6-13)", () => {
    const events = [
      ready(2),
      ready(3, { type: "V5_ENTERED", stop: 99.12, target: 101.78 }),
      ready(4, { type: "V5_BLOCKED", reason: "V5_ENTRY_PLAN_INVALID" }),
      ready(5, { entryQuality: null }),
    ];
    for (const n of planV5Notifications(events, 1).notifications)
      for (const word of ["%", "승률", "확률", "성공", "점", "/100", "매수", "매도"]) {
        expect(n.title).not.toContain(word);
        expect(n.body).not.toContain(word);
      }
  });
});

describe("v4 푸시 게이트 (§4 off/shadow 호환, 승인 단서 1)", () => {
  it("active에서만 v4 SETUP/BREAKOUT 푸시·소리가 꺼진다 — off/shadow/구버전은 기존 v4 알림 유지", () => {
    expect(v4PushEnabled("active")).toBe(false);
    expect(v4PushEnabled("shadow")).toBe(true);
    expect(v4PushEnabled("off")).toBe(true);
    expect(v4PushEnabled(null)).toBe(true);
    expect(v4PushEnabled(undefined)).toBe(true);
  });
});
