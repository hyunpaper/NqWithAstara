export type ReplayStatus = "queued" | "running" | "completed" | "failed";

export type ReplaySymbolResult = {
  symbol: string;
  signals: number;
  virtualEntries: number | null;
  wins: number | null;
  losses: number | null;
  pnlPercent: number | null;
  averageHoldingMinutes: number | null;
  exits: { stop: number; target: number; eod: number } | null;
  tradeReplayStatus: "available" | "unavailable";
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
  aggregate: Omit<ReplaySymbolResult, "symbol" | "tradeReplayStatus" | "unavailableReason"> | null;
  symbols: ReplaySymbolResult[];
  resultKind: "historical-virtual";
  notice: string;
};

export function replayValue(value: number | null | undefined, suffix = ""): string {
  return value == null ? "unavailable" : `${value.toLocaleString()}${suffix}`;
}

export function replayStatusLabel(status: ReplayStatus): string {
  return { queued: "대기", running: "실행 중", completed: "완료", failed: "실패" }[status];
}
