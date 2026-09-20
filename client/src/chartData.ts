export type ChartBar = {
  time: string;
  open?: number;
  high?: number;
  low?: number;
  close: number;
  volume?: number;
  ema?: number;
  vwap?: number;
};

export const normalizeChartBars = (bars: ChartBar[], limit = 60): ChartBar[] => {
  const byTime = new Map<number, ChartBar>();
  for (const bar of bars) {
    const timestamp = Date.parse(bar.time);
    if (!Number.isFinite(timestamp) || ![bar.open, bar.high, bar.low, bar.close].every((v) => Number.isFinite(v))) continue;
    byTime.set(timestamp, { ...bar, time: new Date(timestamp).toISOString() });
  }
  return [...byTime.entries()]
    .sort(([a], [b]) => a - b)
    .slice(-Math.max(0, limit))
    .map(([, bar]) => bar);
};
