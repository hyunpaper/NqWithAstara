import { describe, expect, it } from "vitest";
import {
  normalizeOpeningScan,
  openingRvolCell,
  openingScanBadge,
  openingScanTriggerLabel,
  type OpeningRvolWindow,
  type OpeningScanResponse,
  type OpeningScanRow,
} from "./openingScanTypes";

const win = (lookback: number, ratio: number | null = null, baselineVolume: number | null = null, sampleCount = 0): OpeningRvolWindow =>
  ({ lookbackSessions: lookback, ratio, baselineVolume, sampleCount });

const row = (over: Partial<OpeningScanRow>): OpeningScanRow => ({
  symbol: "NVDA", name: "엔비디아", grade: "WEAK", score: 0, volumeStatus: "pending",
  rvolNow: null, rvol3: win(3), rvol5: win(5), rvol20: win(20), sampleCount: 0, changeFromOpenPercent: null, changeFromPrevClosePercent: null,
  gapPercent: null, prevCloseSource: null, aboveVwap: null, first5: null, brokeOpeningRange: null,
  premarket: null, quoteStatus: "fresh", reasons: [], observedAt: "", elapsedMinutes: 0, ...over,
});

const base = (over: Partial<OpeningScanResponse>): OpeningScanResponse => ({
  sessionDate: "2026-10-07", phase: "scanning", windowStart: null, windowEnd: null, asOf: "2026-10-07T13:37:00Z",
  elapsedMinutes: 7, policyVersion: "opening-scan.1", lookbackSessions: 20, minimumSessions: 5, refreshSeconds: 60,
  rows: [], summary: null, warnings: [], ...over,
});

describe("개장 스캔 API 계약", () => {
  it("phase를 열거값으로 정규화하고 null 필드를 보존한다", () => {
    const scan = normalizeOpeningScan({
      sessionDate: "2026-10-07", phase: "unknown", asOf: "x", elapsedMinutes: 7, policyVersion: "opening-scan.1",
      lookbackSessions: 20, minimumSessions: 5, refreshSeconds: 60,
      rows: [{ symbol: "NVDA", grade: "STRONG", score: 74, volumeStatus: "partial", rvolNow: 2.8, sampleCount: 12, changeFromOpenPercent: 0.9, reasons: ["a"], elapsedMinutes: 7 }],
      summary: null, warnings: [],
    })!;
    expect(scan.phase).toBe("idle");
    expect(scan.rows[0].grade).toBe("STRONG");
    expect(scan.rows[0].aboveVwap).toBeNull();
    expect(scan.rows[0].changeFromPrevClosePercent).toBeNull();
  });

  it("summary 구조를 복원한다", () => {
    const scan = normalizeOpeningScan({
      phase: "summary", asOf: "x", policyVersion: "opening-scan.1", rows: [],
      summary: {
        at5: [{ symbol: "NVDA", grade: "STRONG" }],
        at30: [],
        followup30: [{ symbol: "NVDA", gradeAt5: "STRONG", returnPercent30: 0.8, mfePercent30: 1.4, maePercent30: -0.3 }],
        close: [{ symbol: "NVDA", gradeAt5: "STRONG", returnPercentClose: 1.5 }],
      },
      warnings: [],
    })!;
    expect(scan.phase).toBe("summary");
    expect(scan.summary!.at5).toHaveLength(1);
    expect(scan.summary!.followup30[0].returnPercent30).toBe(0.8);
    expect(scan.summary!.close[0].returnPercentClose).toBe(1.5);
  });

  it("등급별 배지 문구를 만들고 WEAK는 null이다", () => {
    expect(openingScanBadge(row({ grade: "STRONG", rvolNow: 2.8, changeFromOpenPercent: 0.9, reasons: ["r1", "r2"] }))).toEqual({ label: "개장 2.8× ▲0.9%", title: "r1\nr2" });
    expect(openingScanBadge(row({ grade: "VOLUME_ONLY", rvolNow: 2.8 }))?.label).toBe("개장 2.8×");
    expect(openingScanBadge(row({ grade: "PRICE_ONLY", changeFromOpenPercent: 0.9 }))?.label).toBe("개장 ▲0.9%");
    expect(openingScanBadge(row({ grade: "WEAK" }))).toBeNull();
  });

  it("3·5·20일 RVOL 창을 정규화하고 결측은 표본만 남긴다", () => {
    const scan = normalizeOpeningScan({
      phase: "scanning", asOf: "x", policyVersion: "opening-scan.1",
      rows: [{
        symbol: "NVDA", grade: "STRONG", rvolNow: 2.8,
        rvol3: { lookbackSessions: 3, ratio: 3.1, baselineVolume: 401000, sampleCount: 3 },
        rvol5: { lookbackSessions: 5, ratio: 2.8, baselineVolume: 439000, sampleCount: 5 },
        rvol20: { lookbackSessions: 20, ratio: null, baselineVolume: null, sampleCount: 4 },
        reasons: [],
      }],
      summary: null, warnings: [],
    })!;
    expect(scan.rows[0].rvol3.ratio).toBe(3.1);
    expect(scan.rows[0].rvol5.baselineVolume).toBe(439000);
    expect(scan.rows[0].rvol20.ratio).toBeNull();
    expect(scan.rows[0].rvol20.sampleCount).toBe(4);
  });

  it("RVOL 셀을 3/5/20일로 나란히, 툴팁에 각 기준 평균 거래량을 담는다", () => {
    const cell = openingRvolCell(row({
      rvol3: win(3, 3.1, 401000, 3),
      rvol5: win(5, 2.8, 439000, 5),
      rvol20: win(20, null, null, 4),
    }));
    expect(cell.text).toBe("3.1× / 2.8× / 4/20");
    expect(cell.title).toContain("5일 평균 439,000주");
    expect(cell.title).toContain("20일 평균 표본 부족");
  });

  it("트리거 라벨을 4상태로 만든다", () => {
    expect(openingScanTriggerLabel(base({ phase: "scanning", elapsedMinutes: 7, rows: [row({ grade: "STRONG" }), row({ grade: "WEAK" })] }))).toEqual({ label: "개장 초반 강세 1", detail: "7분" });
    expect(openingScanTriggerLabel(base({ phase: "pending" }))?.label).toBe("개장 봉 대기");
    expect(openingScanTriggerLabel(base({ phase: "summary" }))?.label).toBe("개장 초반 기록");
    expect(openingScanTriggerLabel(base({ phase: "idle" }))).toBeNull();
  });
});
