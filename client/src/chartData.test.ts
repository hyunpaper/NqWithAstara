import { describe, expect, it } from "vitest";
import { normalizeChartBars } from "./chartData";

const bar = (minute: number, close = minute): { time: string; open: number; high: number; low: number; close: number } => ({
  time: `2026-09-20T00:${String(minute).padStart(2, "0")}:00Z`,
  open: close - 1,
  high: close + 1,
  low: close - 2,
  close,
});

describe("normalizeChartBars", () => {
  it("정렬·중복 제거·결측 필터·최근 구간 제한을 적용한다", () => {
    const result = normalizeChartBars([
      bar(3),
      { ...bar(2), close: Number.NaN },
      bar(1),
      { ...bar(3), close: 99 },
      bar(4),
    ], 3);
    expect(result.map((x) => [x.time, x.close])).toEqual([
      ["2026-09-20T00:01:00.000Z", 1],
      ["2026-09-20T00:03:00.000Z", 99],
      ["2026-09-20T00:04:00.000Z", 4],
    ]);
  });

  it("잘못된 시각과 OHLC 누락을 제외한다", () => {
    expect(normalizeChartBars([
      { ...bar(1), time: "잘못된 시각" },
      { ...bar(2), high: undefined },
      bar(3),
    ])).toHaveLength(1);
  });
});
