export type WatchRowRect = { symbol: string; top: number; bottom: number };

/** 이슈 #224: 꾹 누름 판정 시간(ms)과 드래그 취소 이동 허용치(px). */
export const LONG_PRESS_MS = 250;
export const DRAG_CANCEL_PX = 6;

export function exceedsPressSlop(dx: number, dy: number): boolean {
  return Math.sqrt(dx * dx + dy * dy) > DRAG_CANCEL_PX;
}

/** 포인터 y가 지나온 행 중앙선 개수 = 삽입 위치(0..길이). */
export function insertionIndex(rects: readonly WatchRowRect[], pointerY: number): number {
  let index = 0;
  for (const rect of rects) if (pointerY > (rect.top + rect.bottom) / 2) index += 1;
  return index;
}

/** 삽입 위치(원본 좌표계)를 제거 후 배열의 최종 인덱스로 변환한다. */
export function dropIndex(from: number, insertion: number): number {
  return insertion > from ? insertion - 1 : insertion;
}

export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  const next = [...items];
  if (from < 0 || from >= next.length) return next;
  const target = Math.min(Math.max(to, 0), next.length - 1);
  if (target === from) return next;
  const [moved] = next.splice(from, 1);
  next.splice(target, 0, moved);
  return next;
}

export function sameOrder<T>(a: readonly T[], b: readonly T[]): boolean {
  return a.length === b.length && a.every((x, i) => x === b[i]);
}

export function sameSet(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && sameOrder([...a].sort(), [...b].sort());
}

export function applyDrop<T>(
  items: readonly T[],
  from: number,
  insertion: number,
): { items: T[]; changed: boolean } {
  const next = moveItem(items, from, dropIndex(from, insertion));
  return { items: next, changed: !sameOrder(next, items) };
}

/** 한 칸 이동 목표 인덱스 — 경계를 벗어나면 null(버튼 비활성). */
export function stepTargetIndex(index: number, length: number, delta: number): number | null {
  const target = index + delta;
  return target < 0 || target >= length ? null : target;
}

export function canStep(index: number, length: number, delta: number): boolean {
  return stepTargetIndex(index, length, delta) !== null;
}

/** 접근성 대안: Alt+↑/↓만 한 칸 이동으로 해석하고 그 외에는 null. */
export function keyboardTargetIndex(
  index: number,
  length: number,
  event: { key: string; altKey: boolean },
): number | null {
  if (!event.altKey) return null;
  const delta = event.key === "ArrowUp" ? -1 : event.key === "ArrowDown" ? 1 : 0;
  if (delta === 0) return null;
  const target = index + delta;
  return target < 0 || target >= length ? null : target;
}

export type GrabAction =
  | { type: "grab" }
  | { type: "move"; delta: -1 | 1 }
  | { type: "drop" }
  | { type: "cancel" };

/** 핸들 키보드 조작: Space로 잡기 → ↑/↓ 이동 → Space/Enter 놓기, Esc 취소. */
export function grabKeyAction(key: string, grabbed: boolean): GrabAction | null {
  const space = key === " " || key === "Spacebar";
  if (!grabbed) return space ? { type: "grab" } : null;
  if (space || key === "Enter") return { type: "drop" };
  if (key === "ArrowUp") return { type: "move", delta: -1 };
  if (key === "ArrowDown") return { type: "move", delta: 1 };
  if (key === "Escape") return { type: "cancel" };
  return null;
}

export const WATCH_ORDER_URL = "/api/watchlist/order";

/** 순서 저장 요청 본문 계약 — 서버는 { symbols: [...] }만 받는다. */
export function watchOrderRequest(symbols: readonly string[]): RequestInit {
  return {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ symbols }),
  };
}
