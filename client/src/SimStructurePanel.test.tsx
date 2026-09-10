// 이슈 #27 — v5 구조 코호트 섹션 화면 검증(RTL).
// 관점: 표본 없음·미수집을 0%나 "검증 완료"처럼 그리지 않고, 승률에는 분모를 붙이고,
// 계획 netR(계획값)과 실현 손익을 혼동시키지 않는다. fixture 축은 서버 테스트와 동일하게
// v4-only(=v5 없음)/v5-only/혼합/빈 데이터/OPEN-only/컨텍스트 누락을 다룬다.
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import SimStructurePanel from "./SimStructurePanel";
import type {
  CohortStats,
  SimulationCohort,
  StructureCohortReport,
} from "./dashboardTypes";

const stats = (over: Partial<CohortStats> = {}): CohortStats => ({
  total: 0,
  open: 0,
  closed: 0,
  validClosed: 0,
  wins: 0,
  winRate: null,
  avgPnl: null,
  missingPnl: 0,
  estimatedExits: 0,
  avgPlannedNetR: null,
  plannedNetRSamples: 0,
  ...over,
});

const cohort = (
  key: string,
  label: string,
  over: Partial<CohortStats> = {},
  collected = true,
): SimulationCohort => ({ key, label, collected, stats: stats(over) });

const emptyReport: StructureCohortReport = {
  v5Stats: stats(),
  contextMissing: 0,
  groups: [],
};

const v5Report: StructureCohortReport = {
  v5Stats: stats({
    total: 14,
    open: 2,
    closed: 12,
    validClosed: 11,
    wins: 7,
    winRate: 63.6,
    avgPnl: 0.42,
    missingPnl: 1,
    estimatedExits: 1,
    avgPlannedNetR: 1.62,
    plannedNetRSamples: 14,
  }),
  contextMissing: 0,
  groups: [
    {
      dimension: "dataQuality",
      title: "데이터 품질 (비용 산정)",
      cohorts: [
        cohort("COST_OK", "비용 산정 정상", {
          total: 11,
          closed: 10,
          validClosed: 10,
          wins: 6,
          winRate: 60,
          avgPnl: 0.5,
          avgPlannedNetR: 1.7,
          plannedNetRSamples: 11,
        }),
        cohort("MISSING_LIQUIDITY_COST", "비용 결측 — 호가 없음 · 스프레드 0 가정", {
          total: 3,
          closed: 2,
          validClosed: 1,
          wins: 1,
          winRate: 100,
          missingPnl: 1,
          avgPnl: 1.1,
          avgPlannedNetR: 1.3,
          plannedNetRSamples: 3,
        }),
      ],
    },
    {
      dimension: "entryQuality",
      title: "진입 품질 구간 (EntryQuality)",
      cohorts: [
        cohort("Q50_75", "50 ~ 75", {
          total: 12,
          closed: 11,
          validClosed: 11,
          wins: 7,
          winRate: 63.6,
          avgPnl: 0.42,
          avgPlannedNetR: 1.6,
          plannedNetRSamples: 12,
        }),
        cohort(
          "QUALITY_UNCOLLECTED",
          "EntryQuality 미수집",
          { total: 2, open: 2, avgPlannedNetR: 1.8, plannedNetRSamples: 2 },
          false,
        ),
      ],
    },
  ],
};

describe("SimStructurePanel", () => {
  it("v5 거래가 없으면(빈 데이터·v4-only) 0% 대신 '없음' 안내만 그린다", () => {
    render(<SimStructurePanel report={emptyReport} />);
    expect(
      screen.getByText(/v5 구조 거래가 아직 없습니다/),
    ).toBeTruthy();
    expect(screen.queryByText(/0%/)).toBeNull();
    expect(screen.queryByText(/검증 완료/)).toBeNull();
  });

  it("구버전 서버(structure 필드 없음)에서도 깨지지 않는다", () => {
    render(<SimStructurePanel report={undefined} />);
    expect(screen.getByText(/v5 구조 거래가 아직 없습니다/)).toBeTruthy();
  });

  it("v5-only: 승률은 항상 분모(표본)와 함께, 계획 netR은 계획값으로 구분해 그린다", () => {
    render(<SimStructurePanel report={v5Report} />);
    expect(screen.getAllByText(/63\.6% \(7\/11건\)/).length).toBeGreaterThan(0);
    expect(screen.getByText(/1\.62R \(14건\)/)).toBeTruthy();
    expect(
      screen.getAllByText(/실현 손익이 아닙니다/).length,
    ).toBeGreaterThan(0);
    // 손익 결측 건은 분모에서 빠졌음이 명시된다.
    expect(screen.getByText(/손익 결측 1건 제외/)).toBeTruthy();
    // 비용 결측 코호트가 정상 비용 코호트와 분리된 행으로 존재한다.
    expect(screen.getByText(/비용 결측 — 호가 없음/)).toBeTruthy();
    expect(screen.getByText("비용 산정 정상")).toBeTruthy();
  });

  it("EntryQuality 미수집 코호트는 구간이 아니라 '미수집'으로 표시된다", () => {
    render(<SimStructurePanel report={v5Report} />);
    expect(screen.getByText("EntryQuality 미수집")).toBeTruthy();
    expect(screen.getAllByText("미수집").length).toBeGreaterThan(0);
  });

  it("손익 유효 청산이 10건 미만이면 잠정 관찰값 경고를 그리고, 충분하면 그리지 않는다", () => {
    const small: StructureCohortReport = {
      ...v5Report,
      v5Stats: stats({
        total: 4,
        closed: 3,
        validClosed: 3,
        wins: 2,
        winRate: 66.7,
        avgPnl: 0.3,
        open: 1,
        avgPlannedNetR: 1.5,
        plannedNetRSamples: 4,
      }),
    };
    const { unmount } = render(<SimStructurePanel report={small} />);
    expect(screen.getByText(/소표본입니다/)).toBeTruthy();
    unmount();
    render(<SimStructurePanel report={v5Report} />);
    expect(screen.queryByText(/소표본입니다/)).toBeNull();
  });

  it("OPEN-only: 청산 0건이면 승률·평균 손익은 0이 아니라 '표본 없음'이다", () => {
    const openOnly: StructureCohortReport = {
      v5Stats: stats({ total: 3, open: 3, avgPlannedNetR: 1.4, plannedNetRSamples: 3 }),
      contextMissing: 0,
      groups: [
        {
          dimension: "trend",
          title: "진입 시점 추세",
          cohorts: [
            cohort("UP", "상승", {
              total: 3,
              open: 3,
              avgPlannedNetR: 1.4,
              plannedNetRSamples: 3,
            }),
          ],
        },
      ],
    };
    render(<SimStructurePanel report={openOnly} />);
    expect(screen.getAllByText("표본 없음").length).toBeGreaterThan(2);
    expect(screen.queryByText(/0%/)).toBeNull();
  });

  it("컨텍스트 누락 건수는 추정하지 않고 별도 경고 + 미수집 행으로 구분한다", () => {
    const withMissing: StructureCohortReport = {
      v5Stats: stats({
        total: 2,
        closed: 1,
        validClosed: 1,
        wins: 1,
        winRate: 100,
        avgPnl: 1.2,
        open: 1,
        avgPlannedNetR: 1.6,
        plannedNetRSamples: 1,
      }),
      contextMissing: 1,
      groups: [
        {
          dimension: "setup",
          title: "셋업 종류",
          cohorts: [
            cohort("PULLBACK", "눌림목 (PULLBACK)", {
              total: 1,
              closed: 1,
              validClosed: 1,
              wins: 1,
              winRate: 100,
              avgPnl: 1.2,
              avgPlannedNetR: 1.6,
              plannedNetRSamples: 1,
            }),
            cohort(
              "CONTEXT_MISSING",
              "동결 컨텍스트 누락 (미수집)",
              { total: 1, open: 1 },
              false,
            ),
          ],
        },
      ],
    };
    render(<SimStructurePanel report={withMissing} />);
    expect(
      screen.getByText(/동결 컨텍스트가 없는 v5 거래 1건/),
    ).toBeTruthy();
    const labels = screen.getAllByText(/동결 컨텍스트 누락 \(미수집\)/);
    // 누락 코호트의 계획 netR도 0이 아니라 미수집이다.
    const row = labels.map((el) => el.closest("tr")).find((el) => el != null)!;
    expect(row).toBeTruthy();
    expect(row.textContent).toContain("미수집");
    expect(row.textContent).not.toContain("0R");
  });
});
