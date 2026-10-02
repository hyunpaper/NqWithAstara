import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import EntryObservabilityPanel from "./EntryObservabilityPanel";
import type {
  EntryObservabilityReport,
  EntryObservationCandidateRow,
  EntryObservationHistoryReport,
  EntryObservationHistoryWindow,
} from "./entryObservability";

const window = (over: Partial<EntryObservationHistoryWindow> = {}): EntryObservationHistoryWindow => ({
  label: "오늘",
  from: "2026-10-02",
  to: "2026-10-02",
  files: 1,
  observations: 12,
  symbols: 3,
  candidates: 2,
  ready: 1,
  entered: 0,
  rejected: 1,
  topReasons: [{ code: "STALE_QUOTE", count: 4 }],
  corruptLines: 0,
  ...over,
});

const rejected: EntryObservationCandidateRow = {
  symbol: "NBIS",
  eventId: "ev-rejected",
  kind: "BREAKOUT",
  state: "REJECTED",
  triggerBarStart: "2026-10-02T14:10:00Z",
  firstObservedAt: "2026-10-02T14:10:15Z",
  lastObservedAt: "2026-10-02T14:12:15Z",
  rejectionCodes: ["V5_ENTRY_NET_R", "STALE_QUOTE"],
  policyHash: "abe5d3a6fa8d40b1",
};

const entered: EntryObservationCandidateRow = {
  ...rejected,
  symbol: "IREN",
  eventId: "ev-entered",
  kind: "REBOUND",
  state: "ENTERED",
  rejectionCodes: [],
};

const historyWithoutEntry: EntryObservationHistoryReport = {
  generatedAt: "2026-10-02T14:30:00Z",
  policyHash: "abe5d3a6fa8d40b1",
  today: window(),
  currentPolicy: window({ label: "현재 정책", from: "2026-09-26", files: 5, candidates: 9, rejected: 6 }),
  recentCandidates: [rejected],
  warning: null,
};

const historyWithEntry: EntryObservationHistoryReport = {
  ...historyWithoutEntry,
  today: window({ entered: 1 }),
  recentCandidates: [rejected, entered],
};

const snapshot: EntryObservabilityReport = {
  generatedAt: "2026-10-02T14:30:00Z",
  candidateCount: 3,
  approvedCount: 1,
  rejectedCount: 2,
  symbols: [
    {
      symbol: "NBIS",
      status: "available",
      evaluatedAt: "2026-10-02T14:30:00Z",
      analysisAsOf: null,
      quoteAt: null,
      dataDelaySeconds: 120,
      candidateCount: 2,
      approvedCount: 0,
      rejectedCount: 2,
      finalDisposition: "REJECTED",
      firstGateReason: "STALE_QUOTE",
      duplicateReasons: ["DUPLICATE_TRIGGER_GUARD"],
      rejectionReasons: ["STALE_QUOTE"],
    },
  ],
};

describe("EntryObservabilityPanel", () => {
  it("누적·정책·차단 사유 영역을 그리지 않는다", () => {
    render(<EntryObservabilityPanel report={snapshot} history={historyWithEntry} />);
    expect(screen.queryByText(/누적/)).toBeNull();
    expect(screen.queryByText(/현재 정책/)).toBeNull();
    expect(screen.queryByText(/차단 사유/)).toBeNull();
    expect(screen.queryByText(/재기동 이후/)).toBeNull();
    expect(screen.queryByTestId("entry-observation-history")).toBeNull();
  });

  it("후보·거절 종목과 거절 사유는 보이지 않는다", () => {
    render(<EntryObservabilityPanel report={snapshot} history={historyWithEntry} />);
    expect(screen.queryByText("NBIS")).toBeNull();
    expect(screen.queryByText("최종 거절")).toBeNull();
    expect(screen.queryByText(/시세 지연/)).toBeNull();
    expect(screen.queryByText(/후보/)).toBeNull();
  });

  it("실제 진입한 거래만 건수와 함께 표시한다", () => {
    render(<EntryObservabilityPanel report={snapshot} history={historyWithEntry} />);
    expect(screen.getByText("진입 관측")).toBeTruthy();
    expect(screen.getByText("오늘 진입 1건")).toBeTruthy();
    expect(screen.getByText("IREN")).toBeTruthy();
    expect(screen.getByText("REBOUND")).toBeTruthy();
  });

  it("진입이 없으면 한 줄 문구만 보이고 표를 그리지 않는다", () => {
    render(<EntryObservabilityPanel report={snapshot} history={historyWithoutEntry} />);
    expect(screen.getByText("오늘 진입 없음")).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("누적 데이터가 없으면 스냅샷의 진입 완료 종목을 표시한다", () => {
    const enteredSnapshot: EntryObservabilityReport = {
      ...snapshot,
      symbols: [{ ...snapshot.symbols[0], symbol: "IREN", finalDisposition: "ENTERED" }],
    };
    render(<EntryObservabilityPanel report={enteredSnapshot} history={null} />);
    expect(screen.getByText("오늘 진입 1건")).toBeTruthy();
    expect(screen.getByText("IREN")).toBeTruthy();
  });

  it("관측 파일 경고는 숨기지 않는다", () => {
    render(
      <EntryObservabilityPanel
        report={null}
        history={{ ...historyWithoutEntry, warning: "관측 파일 읽기 실패: x.jsonl (IOException)" }}
      />,
    );
    expect(screen.getByText("관측 파일 읽기 실패: x.jsonl (IOException)")).toBeTruthy();
  });

  it("스냅샷도 누적도 없으면 아무것도 그리지 않는다", () => {
    const { container } = render(<EntryObservabilityPanel report={null} history={null} />);
    expect(container.firstChild).toBeNull();
  });
});
