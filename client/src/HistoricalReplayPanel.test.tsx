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
