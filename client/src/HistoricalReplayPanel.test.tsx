import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import HistoricalReplayPanel from "./HistoricalReplayPanel";

const running = {
  id: "run-1", from: "2026-09-08", to: "2026-09-08", watchlist: ["TSLA"], benchmark: "QQQ",
  source: "mock", policyHash: "policy", weightsVersion: "weights", status: "running",
  createdAt: "2026-09-13T00:00:00Z", completedAt: null, failureReason: null, aggregate: null,
  symbols: [], resultKind: "historical-virtual", notice: "가상 결과",
};

afterEach(() => vi.restoreAllMocks());

describe("과거 replay 중지", () => {
  it("데이터 없음 상태와 unavailable 수치를 표시한다", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(new Response(JSON.stringify({
      ...running, status: "no-data", failureReason: "요청 기간에 수집된 원시 봉이 없습니다.",
      dataStatus: "no-data", dataReason: "요청 기간에 수집된 원시 봉이 없습니다.",
      sourceQuality: [{ symbol: "TSLA", rawBars: 0, actualTradingDays: 0, reachedRequestedStart: false,
        oldestBar: null, dataStatus: "no-data", reason: "빈 응답" }],
      symbols: [{ symbol: "TSLA", signals: null, virtualEntries: null, wins: null, losses: null,
        pnlPercent: null, averageHoldingMinutes: null, exits: null, tradeReplayStatus: "unavailable",
        unavailableReason: "빈 응답" }],
    }), { status: 200 }));

    render(<HistoricalReplayPanel />);

    expect(await screen.findByText("데이터 없음")).toBeTruthy();
    expect(screen.getByText(/원시 봉 0/)).toBeTruthy();
    expect(screen.getAllByText("unavailable").length).toBeGreaterThan(0);
  });

  it("실행 중인 작업을 중지하고 중지 중 상태를 표시한다", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(new Response(JSON.stringify(running), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...running, status: "canceling" }), { status: 200 }));
    render(<HistoricalReplayPanel />);
    const stop = await screen.findByRole("button", { name: "중지" });

    fireEvent.click(stop);

    await waitFor(() => expect(screen.getAllByText("중지 중").length).toBeGreaterThan(0));
    expect(fetchMock).toHaveBeenCalledWith("/api/replays/run-1/cancel", { method: "POST" });
  });
});
