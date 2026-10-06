import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import OpeningScanPopover from "./OpeningScanPopover";
import type { OpeningScanResponse, OpeningScanRow } from "./openingScanTypes";

const win = (lookback: number, ratio: number | null = null, baselineVolume: number | null = null, sampleCount = 0) =>
  ({ lookbackSessions: lookback, ratio, baselineVolume, sampleCount });

const row = (over: Partial<OpeningScanRow>): OpeningScanRow => ({
  symbol: "NVDA", name: "엔비디아", grade: "WEAK", score: 0, volumeStatus: "pending",
  rvolNow: null, rvol3: win(3), rvol5: win(5), rvol20: win(20), sampleCount: 0, changeFromOpenPercent: null, changeFromPrevClosePercent: null,
  gapPercent: null, prevCloseSource: null, aboveVwap: null, first5: null, brokeOpeningRange: null,
  premarket: null, quoteStatus: "fresh", reasons: [], observedAt: "", elapsedMinutes: 7, ...over,
});

const base = (over: Partial<OpeningScanResponse>): OpeningScanResponse => ({
  sessionDate: "2026-10-07", phase: "scanning", windowStart: null, windowEnd: null, asOf: "2026-10-07T13:37:00Z",
  elapsedMinutes: 7, policyVersion: "opening-scan.1", lookbackSessions: 20, minimumSessions: 5, refreshSeconds: 60,
  rows: [], summary: null, warnings: [], ...over,
});

describe("OpeningScanPopover", () => {
  it("scanning 표를 열고 근거 툴팁과 약함 접힘을 보여준다", () => {
    const scan = base({
      phase: "scanning",
      rows: [
        row({ symbol: "NVDA", grade: "STRONG", rvolNow: 2.8, rvol3: win(3, 3.1, 401000, 3), rvol5: win(5, 2.8, 439000, 5), rvol20: win(20, 2.5, 470000, 20), sampleCount: 20, volumeStatus: "full", changeFromOpenPercent: 0.9, aboveVwap: true, reasons: ["거래량 강함", "시가 대비 상승"] }),
        row({ symbol: "AMD", grade: "WEAK" }),
      ],
    });
    render(<OpeningScanPopover scan={scan} />);
    fireEvent.click(screen.getByRole("button", { name: "개장 초반 강세 1 · 7분" }));

    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByText("NVDA")).toBeTruthy();
    expect(screen.getByText("약함 1종목")).toBeTruthy();
    expect(screen.getByText("3.1× / 2.8× / 2.5×")).toBeTruthy();
    const nvdaRow = screen.getByText("NVDA").closest("tr")!;
    expect(nvdaRow.getAttribute("title")).toContain("거래량 강함");
  });

  it("표본 부족은 창마다 표본 수를 보여준다", () => {
    const scan = base({
      phase: "scanning",
      rows: [row({ symbol: "NVDA", grade: "PRICE_ONLY", volumeStatus: "insufficient", rvol3: win(3, null, null, 2), rvol5: win(5, null, null, 2), rvol20: win(20, null, null, 2), sampleCount: 2, changeFromOpenPercent: 1.1 })],
    });
    render(<OpeningScanPopover scan={scan} />);
    fireEvent.click(screen.getByRole("button", { name: /개장 초반 강세/ }));
    expect(screen.getByText("2/3 / 2/5 / 2/20")).toBeTruthy();
  });

  it("summary 모드는 기록 컬럼을 보여준다", () => {
    const scan = base({
      phase: "summary",
      rows: [],
      summary: {
        at5: [row({ symbol: "NVDA", grade: "STRONG", rvolNow: 2.8, volumeStatus: "full", changeFromOpenPercent: 0.9 })],
        at30: [],
        followup30: [{ symbol: "NVDA", name: "엔비디아", gradeAt5: "STRONG", returnPercent30: 0.8, mfePercent30: 1.4, maePercent30: -0.3, rvol5: 2.8 }],
        close: [{ symbol: "NVDA", name: "엔비디아", gradeAt5: "STRONG", returnPercentClose: 1.5 }],
      },
    });
    render(<OpeningScanPopover scan={scan} />);
    fireEvent.click(screen.getByRole("button", { name: "개장 초반 기록" }));
    expect(screen.getByText("5분 시점 상위")).toBeTruthy();
    expect(screen.getByText("+30분 수익률")).toBeTruthy();
    expect(screen.getByText("마감 수익률")).toBeTruthy();
  });

  it("idle이면 아무것도 렌더하지 않는다", () => {
    const { container } = render(<OpeningScanPopover scan={base({ phase: "idle" })} />);
    expect(container.firstChild).toBeNull();
  });
});
