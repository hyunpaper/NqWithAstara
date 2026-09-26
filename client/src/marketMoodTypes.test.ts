import { describe, expect, it } from "vitest";
import { marketMoodBadge, normalizeMarketMood, policyRateDelayLabel, policyRateStatusLabel } from "./marketMoodTypes";

describe("시장 분위기 API 계약", () => {
  it("프록시·지연·휴장 근거를 보존한다", () => {
    const mood = normalizeMarketMood({
      asOf: "2026-09-22T15:00:00Z",
      status: "partial",
      score: 1.25,
      direction: "up",
      availableCount: 1,
      totalCount: 2,
      aggregation: "equal_weight_available_only",
      source: "Toss Open API · 미국 주식·ETF",
      refreshSeconds: 60,
      limitations: ["ETF 프록시입니다."],
      evidence: ["gold"],
      economicCalendar: {
        marketDate: "2026-09-22", timeZone: "America/New_York", status: "unsupported", source: "미연결", asOf: "2026-09-22T15:00:00Z",
        reason: "검증된 무료 제공자가 없습니다.", events: [],
      },
      policyRates: {
        status: "available", checkedAt: "2026-09-22T15:00:00Z", source: "SBHNews 표시값 (원천 미확인)", reason: null,
        rates: [{ key: "fed", label: "미 연준", value: 4, previous: 3.75, asOf: "2026-09-16", note: "목표범위", checkedAt: "2026-09-22T15:00:00Z", source: "SBHNews 표시값 (원천 미확인)", delayStatus: "fresh", reason: null }],
      },
      assets: [
        { key: "gold", label: "금", symbol: "GLD", assetKind: "commodity", proxy: true, isAvailable: true, direction: "up", changePercent: 1.25, asOf: "2026-09-22T14:59:00Z", source: "Toss", delayStatus: "fresh", sessionStatus: "open", reason: null },
        { key: "oil", label: "유가", symbol: "USO", assetKind: "commodity", proxy: true, isAvailable: false, direction: "unknown", changePercent: null, asOf: null, source: "Toss", delayStatus: "unavailable", sessionStatus: "closed", reason: "휴장" },
      ],
    });

    expect(mood?.evidence).toEqual(["gold"]);
    expect(mood?.economicCalendar).toMatchObject({ status: "unsupported", events: [], reason: "검증된 무료 제공자가 없습니다." });
    expect(mood?.assets[0].proxy).toBe(true);
    expect(mood?.assets[1]).toMatchObject({ isAvailable: false, sessionStatus: "closed", reason: "휴장" });
    expect(mood?.policyRates).toMatchObject({ status: "available", source: "SBHNews 표시값 (원천 미확인)", rates: [{ key: "fed", value: 4, previous: 3.75, delayStatus: "fresh" }] });
    expect(marketMoodBadge(mood!)).toEqual({ label: "시장 분위기 상승 +1.25%", className: "news-badge positive" });
  });

  it("집계값이 없을 때에도 누락 근거를 열 수 있는 배지를 만든다", () => {
    const mood = normalizeMarketMood({ status: "unavailable", score: null, assets: [] });
    expect(mood).not.toBeNull();
    expect(marketMoodBadge(mood!).label).toBe("시장 분위기 정보 없음");
    expect(mood?.policyRates).toEqual({ status: "unsupported", checkedAt: null, source: "미연결", reason: null, rates: [] });
  });

  it("정책금리 구버전·손상 필드를 안전한 기본값으로 정규화한다", () => {
    const mood = normalizeMarketMood({
      status: "available", score: 0, direction: "flat", assets: [],
      policyRates: { status: "future", rates: [{ key: "fed", label: "미 연준", value: "4", delayStatus: "future" }] },
    });
    expect(mood?.policyRates).toEqual({ status: "unsupported", checkedAt: null, source: "미연결", reason: null, rates: [] });
  });

  it("정책금리 상태를 한국어로 구분한다", () => {
    expect(["available", "delayed", "unavailable", "unsupported"].map((status) => policyRateStatusLabel(status as "available" | "delayed" | "unavailable" | "unsupported")))
      .toEqual(["최신", "지연", "조회 불가", "미지원"]);
    expect(["fresh", "stale", "unavailable"].map((status) => policyRateDelayLabel(status as "fresh" | "stale" | "unavailable")))
      .toEqual(["최신", "지연", "조회 불가"]);
  });
});
