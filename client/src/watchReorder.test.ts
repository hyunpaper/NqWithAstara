import { describe, expect, it } from "vitest";
import {
  DRAG_CANCEL_PX,
  LONG_PRESS_MS,
  WATCH_ORDER_URL,
  applyDrop,
  dropIndex,
  exceedsPressSlop,
  insertionIndex,
  keyboardTargetIndex,
  moveItem,
  sameOrder,
  sameSet,
  stepTargetIndex,
  canStep,
  grabKeyAction,
  watchOrderRequest,
} from "./watchReorder";

const rects = [
  { symbol: "AAA", top: 0, bottom: 50 },
  { symbol: "BBB", top: 50, bottom: 100 },
  { symbol: "CCC", top: 100, bottom: 150 },
];

describe("watchReorder", () => {
  it("롱프레스·취소 임계값을 설계값으로 고정한다", () => {
    expect(LONG_PRESS_MS).toBe(250);
    expect(DRAG_CANCEL_PX).toBe(6);
  });

  it("취소 임계값을 넘는 이동만 롱프레스를 깬다", () => {
    expect(exceedsPressSlop(0, 0)).toBe(false);
    expect(exceedsPressSlop(3, 4)).toBe(false);
    expect(exceedsPressSlop(0, 7)).toBe(true);
    expect(exceedsPressSlop(-10, 0)).toBe(true);
  });

  it("포인터 y가 지나온 행 중앙선 개수를 삽입 위치로 돌려준다", () => {
    expect(insertionIndex(rects, 0)).toBe(0);
    expect(insertionIndex(rects, 24)).toBe(0);
    expect(insertionIndex(rects, 26)).toBe(1);
    expect(insertionIndex(rects, 80)).toBe(2);
    expect(insertionIndex(rects, 400)).toBe(3);
    expect(insertionIndex([], 400)).toBe(0);
  });

  it("삽입 위치를 제거 후 최종 인덱스로 바꾼다", () => {
    expect(dropIndex(0, 0)).toBe(0);
    expect(dropIndex(0, 3)).toBe(2);
    expect(dropIndex(2, 1)).toBe(1);
    expect(dropIndex(2, 2)).toBe(2);
  });

  it("배열 이동은 원본을 바꾸지 않고 경계를 잘라낸다", () => {
    const items = ["A", "B", "C"];
    expect(moveItem(items, 0, 2)).toEqual(["B", "C", "A"]);
    expect(moveItem(items, 2, 0)).toEqual(["C", "A", "B"]);
    expect(moveItem(items, 1, 1)).toEqual(["A", "B", "C"]);
    expect(moveItem(items, 0, 9)).toEqual(["B", "C", "A"]);
    expect(moveItem(items, 0, -3)).toEqual(["A", "B", "C"]);
    expect(moveItem(items, 5, 0)).toEqual(["A", "B", "C"]);
    expect(moveItem(items, -1, 0)).toEqual(["A", "B", "C"]);
    expect(items).toEqual(["A", "B", "C"]);
  });

  it("순서·집합 비교를 구분한다", () => {
    expect(sameOrder(["A", "B"], ["A", "B"])).toBe(true);
    expect(sameOrder(["A", "B"], ["B", "A"])).toBe(false);
    expect(sameOrder(["A"], ["A", "B"])).toBe(false);
    expect(sameSet(["A", "B"], ["B", "A"])).toBe(true);
    expect(sameSet(["A", "B"], ["A", "C"])).toBe(false);
    expect(sameSet(["A"], ["A", "B"])).toBe(false);
  });

  it("드롭 결과와 변경 여부를 함께 돌려준다", () => {
    expect(applyDrop(["A", "B", "C"], 0, 3)).toEqual({ items: ["B", "C", "A"], changed: true });
    expect(applyDrop(["A", "B", "C"], 1, 1)).toEqual({ items: ["A", "B", "C"], changed: false });
    expect(applyDrop(["A", "B", "C"], 1, 2)).toEqual({ items: ["A", "B", "C"], changed: false });
  });

  it("Alt와 함께 누른 위아래 화살표만 이동으로 해석한다", () => {
    expect(keyboardTargetIndex(1, 3, { key: "ArrowUp", altKey: true })).toBe(0);
    expect(keyboardTargetIndex(1, 3, { key: "ArrowDown", altKey: true })).toBe(2);
    expect(keyboardTargetIndex(0, 3, { key: "ArrowUp", altKey: true })).toBeNull();
    expect(keyboardTargetIndex(2, 3, { key: "ArrowDown", altKey: true })).toBeNull();
    expect(keyboardTargetIndex(1, 3, { key: "ArrowDown", altKey: false })).toBeNull();
    expect(keyboardTargetIndex(1, 3, { key: "Enter", altKey: true })).toBeNull();
  });

  it("한 칸 이동 목표는 경계에서 null이다", () => {
    expect(stepTargetIndex(1, 3, -1)).toBe(0);
    expect(stepTargetIndex(1, 3, 1)).toBe(2);
    expect(stepTargetIndex(0, 3, -1)).toBeNull();
    expect(stepTargetIndex(2, 3, 1)).toBeNull();
    expect(canStep(0, 3, 1)).toBe(true);
    expect(canStep(0, 3, -1)).toBe(false);
    expect(canStep(2, 3, 1)).toBe(false);
  });

  it("핸들 키보드 조작을 잡기 상태에 따라 해석한다", () => {
    expect(grabKeyAction(" ", false)).toEqual({ type: "grab" });
    expect(grabKeyAction("Spacebar", false)).toEqual({ type: "grab" });
    expect(grabKeyAction("ArrowDown", false)).toBeNull();
    expect(grabKeyAction(" ", true)).toEqual({ type: "drop" });
    expect(grabKeyAction("Enter", true)).toEqual({ type: "drop" });
    expect(grabKeyAction("ArrowUp", true)).toEqual({ type: "move", delta: -1 });
    expect(grabKeyAction("ArrowDown", true)).toEqual({ type: "move", delta: 1 });
    expect(grabKeyAction("Escape", true)).toEqual({ type: "cancel" });
    expect(grabKeyAction("Tab", true)).toBeNull();
  });

  it("순서 저장 요청 계약을 고정한다", () => {
    expect(WATCH_ORDER_URL).toBe("/api/watchlist/order");
    const init = watchOrderRequest(["NBIS", "PANW"]);
    expect(init.method).toBe("PUT");
    expect(init.headers).toEqual({ "Content-Type": "application/json" });
    expect(JSON.parse(String(init.body))).toEqual({ symbols: ["NBIS", "PANW"] });
  });
});
