import { Fragment, useEffect, useRef, useState } from "react";
import type { PointerEvent as ReactPointerEvent, ReactNode } from "react";
import { ChevronDown, ChevronUp, GripVertical, Trash2 } from "lucide-react";
import {
  LONG_PRESS_MS,
  applyDrop,
  canStep,
  exceedsPressSlop,
  insertionIndex,
  keyboardTargetIndex,
  moveItem,
  sameOrder,
  stepTargetIndex,
  sameSet,
  type WatchRowRect,
} from "./watchReorder";

export type WatchChange = { label: string; tone: "up" | "down" };

export type WatchListItem = {
  symbol: string;
  name: string;
  change: WatchChange | null;
  badges: ReactNode[];
};

/// <summary>관심종목 행 2줄 레이아웃 — 1행 티커·등락률, 2행 배지 (§UI, #224)</summary>
export function WatchRowContent({
  symbol,
  name,
  change,
  badges,
}: {
  symbol: string;
  name: string;
  change?: WatchChange | null;
  badges?: ReactNode[];
}) {
  return (
    <>
      <div className="watch-identity" title={name}>
        <b>{symbol}</b>
        {change && <span className={change.tone}>{change.label}</span>}
      </div>
      {badges && badges.length > 0 && <div className="watch-metrics">{badges}</div>}
    </>
  );
}

type PressState = {
  pointerId: number;
  symbol: string;
  from: number;
  x: number;
  y: number;
  timer: number;
  active: boolean;
  insertion: number;
  rects: WatchRowRect[];
};

/// <summary>그립 핸들 즉시 드래그·꾹 눌러 드래그·키보드로 순서를 바꾸는 관심종목 목록 (§UI, #229)</summary>
export function WatchList({
  items,
  selected,
  onSelect,
  onDelete,
  saveOrder,
  onError,
  empty,
}: {
  items: WatchListItem[];
  selected: string;
  onSelect: (symbol: string) => void;
  onDelete: (symbol: string) => void;
  saveOrder: (symbols: string[]) => Promise<void>;
  onError: (message: string) => void;
  empty?: ReactNode;
}) {
  const [pending, setPending] = useState<string[] | null>(null);
  const [drag, setDrag] = useState<{ symbol: string; insertion: number } | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const press = useRef<PressState | null>(null);
  const suppressClick = useRef(false);

  const serverSymbols = items.map((x) => x.symbol);
  const serverKey = serverSymbols.join(",");
  const usePending = pending !== null && sameSet(pending, serverSymbols);
  const ordered = usePending
    ? pending.map((symbol) => items.find((x) => x.symbol === symbol)!)
    : items;
  const symbols = ordered.map((x) => x.symbol);

  useEffect(() => {
    setPending((current) => (current && !sameSet(current, serverSymbols) ? null : current));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverKey]);

  const measure = (): WatchRowRect[] =>
    Array.from(listRef.current?.querySelectorAll<HTMLElement>(".watch-row") ?? []).map((el) => {
      const rect = el.getBoundingClientRect();
      return { symbol: el.dataset.symbol ?? "", top: rect.top, bottom: rect.bottom };
    });

  const clearPress = () => {
    if (press.current) window.clearTimeout(press.current.timer);
    press.current = null;
  };

  const commit = async (next: string[]) => {
    if (sameOrder(next, symbols)) return;
    setPending(next);
    try {
      await saveOrder(next);
    } catch (e) {
      setPending(null);
      onError(e instanceof Error ? e.message : "관심종목 순서를 저장하지 못했습니다");
    }
  };

  const step = (index: number, delta: number) => {
    const target = stepTargetIndex(index, symbols.length, delta);
    if (target == null) return;
    void commit(moveItem(symbols, index, target));
  };

  const beginPress = (
    e: ReactPointerEvent<HTMLElement>,
    symbol: string,
    index: number,
    immediate = false,
  ) => {
    if (e.pointerType === "mouse" && e.button !== 0) return;
    suppressClick.current = false;
    clearPress();
    const element = e.currentTarget;
    const pointerId = e.pointerId;
    const state: PressState = {
      pointerId,
      symbol,
      from: index,
      x: e.clientX,
      y: e.clientY,
      timer: 0,
      active: false,
      insertion: index,
      rects: [],
    };
    const activate = () => {
      state.rects = measure();
      state.active = true;
      try {
        element.setPointerCapture?.(pointerId);
      } catch {
        /* 포인터 캡처를 지원하지 않는 환경 */
      }
      setDrag({ symbol, insertion: index });
    };
    if (immediate) activate();
    else state.timer = window.setTimeout(activate, LONG_PRESS_MS);
    press.current = state;
  };

  const movePress = (e: ReactPointerEvent<HTMLElement>) => {
    const state = press.current;
    if (!state || state.pointerId !== e.pointerId) return;
    if (!state.active) {
      if (exceedsPressSlop(e.clientX - state.x, e.clientY - state.y)) clearPress();
      return;
    }
    e.preventDefault();
    state.insertion = insertionIndex(state.rects, e.clientY);
    setDrag({ symbol: state.symbol, insertion: state.insertion });
  };

  const endPress = (e: ReactPointerEvent<HTMLElement>) => {
    const state = press.current;
    if (!state || state.pointerId !== e.pointerId) return;
    clearPress();
    if (!state.active) return;
    suppressClick.current = true;
    setDrag(null);
    const drop = applyDrop(symbols, state.from, state.insertion);
    if (drop.changed) void commit(drop.items);
  };

  const cancelPress = () => {
    clearPress();
    setDrag(null);
  };

  if (!items.length) return <div className="watch-list">{empty}</div>;

  return (
    <div className="watch-list" ref={listRef}>
      {ordered.map((item, index) => (
        <Fragment key={item.symbol}>
          {drag && drag.insertion === index && (
            <div className="watch-drop-marker" aria-hidden="true" />
          )}
          <div
            className={`watch-row ${selected === item.symbol ? "active" : ""} ${drag?.symbol === item.symbol ? "dragging" : ""}`}
            data-symbol={item.symbol}
            onPointerDown={(e) => beginPress(e, item.symbol, index)}
            onPointerMove={movePress}
            onPointerUp={endPress}
            onPointerCancel={cancelPress}
          >
            <button
              className="watch-grip"
              aria-label={`${item.symbol} 순서 변경 핸들`}
              onPointerDown={(e) => {
                e.stopPropagation();
                beginPress(e, item.symbol, index, true);
              }}
              onPointerMove={movePress}
              onPointerUp={endPress}
              onPointerCancel={cancelPress}
              onClick={(e) => e.preventDefault()}
            >
              <GripVertical size={14} />
            </button>
            <button
              className="watch-select"
              aria-label={`${item.symbol} ${item.name}`}
              onClick={() => {
                if (suppressClick.current) {
                  suppressClick.current = false;
                  return;
                }
                onSelect(item.symbol);
              }}
              onKeyDown={(e) => {
                const target = keyboardTargetIndex(index, symbols.length, {
                  key: e.key,
                  altKey: e.altKey,
                });
                if (target == null) return;
                e.preventDefault();
                void commit(moveItem(symbols, index, target));
              }}
            >
              <WatchRowContent
                symbol={item.symbol}
                name={item.name}
                change={item.change}
                badges={item.badges}
              />
            </button>
            <button
              className="watch-move"
              aria-label={`${item.symbol} 위로 이동`}
              disabled={!canStep(index, symbols.length, -1)}
              onClick={() => step(index, -1)}
            >
              <ChevronUp size={14} />
            </button>
            <button
              className="watch-move"
              aria-label={`${item.symbol} 아래로 이동`}
              disabled={!canStep(index, symbols.length, 1)}
              onClick={() => step(index, 1)}
            >
              <ChevronDown size={14} />
            </button>
            <button
              className="delete"
              aria-label={`${item.symbol} 관심종목 삭제`}
              onClick={() => onDelete(item.symbol)}
            >
              <Trash2 size={14} />
            </button>
          </div>
        </Fragment>
      ))}
      {drag && drag.insertion === ordered.length && (
        <div className="watch-drop-marker" aria-hidden="true" />
      )}
    </div>
  );
}
