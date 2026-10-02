import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import EntryObservabilityPanel from "./EntryObservabilityPanel";
import type {
  EntryObservabilityReport,
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

const history: EntryObservationHistoryReport = {
  generatedAt: "2026-10-02T14:30:00Z",
  policyHash: "abe5d3a6fa8d40b1",
  today: window(),
  currentPolicy: window({ label: "현재 정책", from: "2026-09-26", files: 5, candidates: 9, rejected: 6 }),
  recentCandidates: [
    {
      symbol: "NBIS",
      eventId: "ev-rejected",
      kind: "BREAKOUT",
      state: "REJECTED",
      triggerBarStart: "2026-10-02T14:10:00Z",
      firstObservedAt: "2026-10-02T14:10:15Z",
      lastObservedAt: "2026-10-02T14:12:15Z",
      rejectionCodes: ["V5_ENTRY_NET_R", "STALE_QUOTE"],
      policyHash: "abe5d3a6fa8d40b1",
    },
  ],
  warning: null,
};

const snapshotWithoutRejections: EntryObservabilityReport = {
  generatedAt: "2026-10-02T14:30:00Z",
  candidateCount: 0,
  approvedCount: 0,
  rejectedCount: 0,
  symbols: [
    {
      symbol: "NBIS",
      status: "available",
      evaluatedAt: "2026-10-02T14:30:00Z",
      analysisAsOf: null,
      quoteAt: null,
      dataDelaySeconds: null,
      candidateCount: 0,
      approvedCount: 0,
      rejectedCount: 0,
      finalDisposition: "NO_CANDIDATE",
      firstGateReason: null,
      duplicateReasons: [],
      rejectionReasons: [],
    },
  ],
};

describe("EntryObservabilityPanel", () => {
  it("스냅샷 제목에 재기동 이후 범위를 명시하고 누적 영역을 따로 그린다", () => {
    render(<EntryObservabilityPanel report={snapshotWithoutRejections} history={history} />);
    expect(screen.getByText("진입 관측 — 현재 스냅샷(재기동 이후)")).toBeTruthy();
    expect(screen.getByText("누적 (관측 파일 기준)")).toBeTruthy();
    expect(screen.getByText(/오늘 누적/)).toBeTruthy();
    expect(screen.getByText(/현재 정책 누적 \(2026-09-26 ~ 2026-10-02\)/)).toBeTruthy();
    expect(screen.getAllByText(/시세 지연 4/).length).toBe(2);
  });

  it("스냅샷에 거절이 없어도 누적 거절 이력은 서버 집계대로 보인다", () => {
    render(<EntryObservabilityPanel report={snapshotWithoutRejections} history={history} />);
    expect(screen.getByText("최종 거절")).toBeTruthy();
    expect(screen.getByText("V5_ENTRY_NET_R · 시세 지연")).toBeTruthy();
  });

  it("화면 전환(언마운트 후 재마운트)과 스냅샷 교체 후에도 같은 거절 이력이 유지된다", () => {
    const first = render(<EntryObservabilityPanel report={snapshotWithoutRejections} history={history} />);
    expect(screen.getByText("최종 거절")).toBeTruthy();
    first.unmount();
    expect(screen.queryByText("최종 거절")).toBeNull();

    const emptySnapshot: EntryObservabilityReport = { ...snapshotWithoutRejections, symbols: [] };
    render(<EntryObservabilityPanel report={emptySnapshot} history={history} />);
    expect(screen.getByText("아직 평가된 종목이 없습니다.")).toBeTruthy();
    expect(screen.getByText("최종 거절")).toBeTruthy();
    expect(screen.getByText("NBIS")).toBeTruthy();
  });

  it("스냅샷이 없어도(재기동 직후) 누적만으로 패널을 그린다", () => {
    render(<EntryObservabilityPanel report={null} history={history} />);
    expect(screen.getByText("누적 (관측 파일 기준)")).toBeTruthy();
    expect(screen.getByText("최종 거절")).toBeTruthy();
  });

  it("누적 경고와 손상 줄 수를 숨기지 않는다", () => {
    render(
      <EntryObservabilityPanel
        report={null}
        history={{ ...history, warning: "관측 파일 읽기 실패: x.jsonl (IOException)", today: window({ corruptLines: 3 }) }}
      />,
    );
    expect(screen.getByText("관측 파일 읽기 실패: x.jsonl (IOException)")).toBeTruthy();
    expect(screen.getByText(/손상 줄 3/)).toBeTruthy();
  });

  it("스냅샷도 누적도 없으면 아무것도 그리지 않는다", () => {
    const { container } = render(<EntryObservabilityPanel report={null} history={null} />);
    expect(container.firstChild).toBeNull();
  });
});
