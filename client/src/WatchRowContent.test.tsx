import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WatchList, WatchRowContent } from "./WatchRowContent";
import { LONG_PRESS_MS } from "./watchReorder";
import rawCss from "./styles.css?raw";

const css = rawCss.replace(/\r\n/g, "\n");

describe("WatchRowContent", () => {
  it("1행에 티커와 등락률을, 2행에 배지를 둔다", () => {
    const { container } = render(
      <button className="watch-select">
        <WatchRowContent
          symbol="LONGTICKER123"
          name="Very Long Company Name Holdings Incorporated"
          change={{ label: "+12.34%", tone: "up" }}
          badges={[
            <span key="news" className="news-badge neutral">중립 +0.0</span>,
            <span key="confluence" className="confluence-mini-badge neutral">+0.10</span>,
          ]}
        />
      </button>,
    );

    const identity = container.querySelector(".watch-identity");
    const metrics = container.querySelector(".watch-metrics");

    expect(identity?.contains(screen.getByText("LONGTICKER123"))).toBe(true);
    expect(identity?.contains(screen.getByText("+12.34%"))).toBe(true);
    expect(metrics?.contains(screen.getByText("중립 +0.0"))).toBe(true);
    expect(metrics?.contains(screen.getByText("+0.10"))).toBe(true);
  });

  it("기업명을 텍스트로 그리지 않고 title에만 남긴다", () => {
    const { container } = render(
      <WatchRowContent symbol="NVDA" name="NVIDIA Corporation" />,
    );

    expect(container.textContent).toBe("NVDA");
    expect(container.querySelector(".watch-identity")?.getAttribute("title")).toBe(
      "NVIDIA Corporation",
    );
  });

  it("배지가 없으면 2행 컨테이너를 만들지 않는다", () => {
    const { container } = render(
      <WatchRowContent symbol="NVDA" name="NVIDIA" badges={[]} />,
    );

    expect(container.querySelector(".watch-identity")).not.toBeNull();
    expect(container.querySelector(".watch-metrics")).toBeNull();
  });

  it("등락률 하락 톤을 down 클래스로 표시한다", () => {
    const { container } = render(
      <WatchRowContent symbol="PANW" name="Palo Alto" change={{ label: "-2.50%", tone: "down" }} />,
    );

    expect(container.querySelector(".watch-identity .down")?.textContent).toBe("-2.50%");
  });
});

const SYMBOLS = ["AAA", "BBB", "CCC"];
const ROW_HEIGHT = 54;

const listItems = () =>
  SYMBOLS.map((symbol) => ({
    symbol,
    name: `${symbol} Holdings`,
    change: { label: "+1.00%", tone: "up" as const },
    badges: [
      <span key="news" className="news-badge neutral">중립 +0.0</span>,
    ],
  }));

const rowOrder = (container: HTMLElement) =>
  Array.from(container.querySelectorAll<HTMLElement>(".watch-row")).map(
    (el: HTMLElement) => el.dataset.symbol,
  );

function stubRowRects() {
  const original = HTMLElement.prototype.getBoundingClientRect;
  HTMLElement.prototype.getBoundingClientRect = function rect(this: HTMLElement) {
    const index = SYMBOLS.indexOf(this.dataset.symbol ?? "");
    const top = index < 0 ? 0 : index * ROW_HEIGHT;
    return {
      x: 0,
      y: top,
      top,
      bottom: top + ROW_HEIGHT,
      left: 0,
      right: 200,
      width: 200,
      height: ROW_HEIGHT,
      toJSON: () => ({}),
    } as DOMRect;
  };
  return () => {
    HTMLElement.prototype.getBoundingClientRect = original;
  };
}

function renderList(saveOrder = vi.fn(async () => {}), onError = vi.fn()) {
  const onSelect = vi.fn();
  const view = render(
    <WatchList
      items={listItems()}
      selected="AAA"
      onSelect={onSelect}
      onDelete={vi.fn()}
      saveOrder={saveOrder}
      onError={onError}
    />,
  );
  return { ...view, onSelect, saveOrder, onError };
}

const row = (container: HTMLElement, symbol: string) =>
  container.querySelector<HTMLElement>(`.watch-row[data-symbol="${symbol}"]`)!;

describe("WatchList 순서 변경", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it("250ms 전에 손을 떼면 드래그가 아니라 선택 클릭이다", () => {
    vi.useFakeTimers();
    const { container, onSelect, saveOrder } = renderList();
    const target = row(container, "BBB");

    fireEvent.pointerDown(target, { pointerId: 1, clientX: 0, clientY: 80 });
    act(() => {
      vi.advanceTimersByTime(LONG_PRESS_MS - 50);
    });
    fireEvent.pointerUp(target, { pointerId: 1, clientX: 0, clientY: 80 });
    fireEvent.click(target.querySelector(".watch-select")!);

    expect(onSelect).toHaveBeenCalledWith("BBB");
    expect(saveOrder).not.toHaveBeenCalled();
  });

  it("250ms 전에 크게 움직이면 롱프레스를 취소한다", () => {
    vi.useFakeTimers();
    const { container, saveOrder } = renderList();
    const target = row(container, "AAA");

    fireEvent.pointerDown(target, { pointerId: 1, clientX: 0, clientY: 20 });
    fireEvent.pointerMove(target, { pointerId: 1, clientX: 0, clientY: 60 });
    act(() => {
      vi.advanceTimersByTime(LONG_PRESS_MS * 2);
    });

    expect(container.querySelector(".watch-row.dragging")).toBeNull();
    expect(saveOrder).not.toHaveBeenCalled();
  });

  it("250ms 유지 후 이동·release로 순서를 바꾸고 저장한다", async () => {
    const restore = stubRowRects();
    vi.useFakeTimers();
    const { container, onSelect, saveOrder } = renderList();
    const target = row(container, "AAA");

    fireEvent.pointerDown(target, { pointerId: 1, clientX: 0, clientY: 20 });
    act(() => {
      vi.advanceTimersByTime(LONG_PRESS_MS);
    });
    expect(container.querySelector<HTMLElement>(".watch-row.dragging")?.dataset.symbol).toBe("AAA");

    fireEvent.pointerMove(target, { pointerId: 1, clientX: 0, clientY: 150 });
    expect(container.querySelector(".watch-drop-marker")).not.toBeNull();

    await act(async () => {
      fireEvent.pointerUp(target, { pointerId: 1, clientX: 0, clientY: 150 });
    });
    fireEvent.click(target.querySelector(".watch-select")!);

    expect(saveOrder).toHaveBeenCalledWith(["BBB", "CCC", "AAA"]);
    expect(rowOrder(container)).toEqual(["BBB", "CCC", "AAA"]);
    expect(onSelect).not.toHaveBeenCalled();
    restore();
  });

  it("저장이 실패하면 이전 순서로 되돌리고 오류를 알린다", async () => {
    const restore = stubRowRects();
    vi.useFakeTimers();
    const saveOrder = vi.fn(async () => {
      throw new Error("순서를 저장하지 못했습니다");
    });
    const { container, onError } = renderList(saveOrder);
    const target = row(container, "AAA");

    fireEvent.pointerDown(target, { pointerId: 1, clientX: 0, clientY: 20 });
    act(() => {
      vi.advanceTimersByTime(LONG_PRESS_MS);
    });
    fireEvent.pointerMove(target, { pointerId: 1, clientX: 0, clientY: 150 });
    await act(async () => {
      fireEvent.pointerUp(target, { pointerId: 1, clientX: 0, clientY: 150 });
    });

    expect(saveOrder).toHaveBeenCalledWith(["BBB", "CCC", "AAA"]);
    expect(rowOrder(container)).toEqual(SYMBOLS);
    expect(onError).toHaveBeenCalledWith("순서를 저장하지 못했습니다");
    restore();
  });

  it("Alt+아래 화살표로 한 칸 내려 저장한다", async () => {
    const { container, saveOrder } = renderList();
    const select = row(container, "AAA").querySelector(".watch-select")!;

    await act(async () => {
      fireEvent.keyDown(select, { key: "ArrowDown", altKey: true });
    });

    expect(saveOrder).toHaveBeenCalledWith(["BBB", "AAA", "CCC"]);
    expect(rowOrder(container)).toEqual(["BBB", "AAA", "CCC"]);
  });

  it("Alt 없는 화살표는 순서를 바꾸지 않는다", async () => {
    const { container, saveOrder } = renderList();
    const select = row(container, "AAA").querySelector(".watch-select")!;

    await act(async () => {
      fireEvent.keyDown(select, { key: "ArrowDown", altKey: false });
    });

    expect(saveOrder).not.toHaveBeenCalled();
    expect(rowOrder(container)).toEqual(SYMBOLS);
  });

  it("관심종목이 없으면 빈 안내만 그린다", () => {
    const { container } = render(
      <WatchList
        items={[]}
        selected=""
        onSelect={vi.fn()}
        onDelete={vi.fn()}
        saveOrder={vi.fn(async () => {})}
        onError={vi.fn()}
        empty={<div className="empty-side">비어 있음</div>}
      />,
    );

    expect(container.querySelector(".watch-row")).toBeNull();
    expect(container.querySelector(".empty-side")?.textContent).toBe("비어 있음");
  });
});

describe("관심종목 CSS 계약", () => {
  const block = (selector: string) =>
    css.slice(css.indexOf(`${selector} {`) + selector.length + 2, css.indexOf("}", css.indexOf(`${selector} {`)));

  it(".watch-metrics는 max-width로 폭을 제한하지 않는다", () => {
    expect(css).toContain(".watch-metrics {");
    expect(block(".watch-metrics")).not.toContain("max-width");
  });

  it(".watch-metrics는 겹침 대신 잘림을 택한다", () => {
    const rules = block(".watch-metrics");
    expect(rules).toContain("flex-wrap: nowrap");
    expect(rules).toContain("overflow: hidden");
    expect(rules).toContain("min-width: 0");
  });

  it("행 높이와 글자 크기를 설계값으로 유지한다", () => {
    expect(block(".watch-row")).toContain("height: 54px");
    expect(block(".watch-identity b")).toContain("font-size: 13px");
    expect(block(".watch-list .up,\n.watch-list .down")).toContain("font-size: 12px");
    expect(block(".watch-list .news-badge,\n.watch-list .confluence-mini-badge")).toContain("font-size: 11px");
  });
});
