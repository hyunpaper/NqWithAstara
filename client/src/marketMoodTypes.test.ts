import { describe, expect, it } from "vitest";
import { marketMoodBadge, normalizeMarketMood } from "./marketMoodTypes";

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
      assets: [
        { key: "gold", label: "금", symbol: "GLD", assetKind: "commodity", proxy: true, isAvailable: true, direction: "up", changePercent: 1.25, asOf: "2026-09-22T14:59:00Z", source: "Toss", delayStatus: "fresh", sessionStatus: "open", reason: null },
        { key: "oil", label: "유가", symbol: "USO", assetKind: "commodity", proxy: true, isAvailable: false, direction: "unknown", changePercent: null, asOf: null, source: "Toss", delayStatus: "unavailable", sessionStatus: "closed", reason: "휴장" },
      ],
    });

    expect(mood?.evidence).toEqual(["gold"]);
    expect(mood?.assets[0].proxy).toBe(true);
    expect(mood?.assets[1]).toMatchObject({ isAvailable: false, sessionStatus: "closed", reason: "휴장" });
    expect(marketMoodBadge(mood!)).toEqual({ label: "시장 분위기 상승 +1.25%", className: "news-badge positive" });
  });

  it("집계값이 없을 때에도 누락 근거를 열 수 있는 배지를 만든다", () => {
    const mood = normalizeMarketMood({ status: "unavailable", score: null, assets: [] });
    expect(mood).not.toBeNull();
    expect(marketMoodBadge(mood!).label).toBe("시장 분위기 정보 없음");
  });
});
