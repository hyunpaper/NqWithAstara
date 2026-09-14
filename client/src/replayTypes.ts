export type ReplayStatus = "queued" | "running" | "canceling" | "canceled" | "completed" | "no-data" | "failed";

export type ReplaySymbolResult = {
  symbol: string;
  signals: number | null;
  virtualEntries: number | null;
  wins: number | null;
  losses: number | null;
  pnlPercent: number | null;
  averageHoldingMinutes: number | null;
  exits: { stop: number; target: number; eod: number } | null;
  tradeReplayStatus: "available" | "partial" | "unavailable";
  unavailableReason: string | null;
};

export type HistoricalReplayRun = {
  id: string;
  from: string;
  to: string;
  watchlist: string[];
  benchmark: string;
  source: string;
  policyHash: string;
  weightsVersion: string;
  status: ReplayStatus;
  createdAt: string;
  completedAt: string | null;
  failureReason: string | null;
  dataStatus: "unknown" | "available" | "partial" | "no-data";
  dataReason: string | null;
  sourceQuality: Array<{
    symbol: string; rawBars: number; actualTradingDays: number; reachedRequestedStart: boolean;
    oldestBar: string | null; dataStatus: "available" | "partial" | "no-data"; reason: string | null;
  }>;
  aggregate: Omit<ReplaySymbolResult, "symbol" | "tradeReplayStatus" | "unavailableReason"> | null;
  symbols: ReplaySymbolResult[];
  resultKind: "historical-virtual";
  notice: string;
};

export function replayValue(value: number | null | undefined, suffix = ""): string {
  return value == null ? "unavailable" : `${value.toLocaleString()}${suffix}`;
}

export function replayStatusLabel(status: ReplayStatus): string {
  return { queued: "대기", running: "실행 중", canceling: "중지 중", canceled: "중지됨", completed: "완료", "no-data": "데이터 없음", failed: "실패" }[status];
}
