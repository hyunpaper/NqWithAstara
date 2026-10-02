import { describe, expect, it } from "vitest";
import { etfProxyLine, formatBp, formatPct, formatRate, normalizeRates, rateChangeTone, rateModeLabel, ratesStatusLabel } from "./ratesTypes";

describe("국채 금리 API 계약", () => {
  it("만기·커브·방향 검사·경고를 보존하고 알 수 없는 값은 안전한 기본값으로 바꾼다", () => {
    const rates = normalizeRates({
      enabled: true,
      status: "partial",
      asOf: "2026-10-02T14:00:00Z",
      intradaySource: "Yahoo Finance 차트 API (비공식 · ^TNX·^TYX)",
      dailySource: "FRED DGS2·DGS10·DGS30 (일별 공식값)",
      refreshSeconds: 60,
      tenors: [
        { tenor: "2Y", label: "미국채 2년", value: 4.88, changeBp: -1, previousClose: 4.89, asOf: "2026-10-01T20:00:00Z", fetchedAt: "2026-10-02T12:00:00Z", source: "fred:DGS2", mode: "daily_only", delayStatus: "fresh", sessionStatus: "closed", delaySeconds: null, daily: { value: 4.88, date: "2026-10-01", previous: 4.89, previousDate: "2026-09-30", changeBp: -1, source: "fred:DGS2", fetchedAt: "2026-10-02T12:00:00Z" }, reason: "실시간 지수가 없어 FRED 전일 공식값(전일 대비 변화)만 표시합니다." },
        { tenor: "10Y", label: "미국채 10년", value: 5.237, changeBp: -5.6, previousClose: 5.293, asOf: "2026-10-02T13:59:30Z", fetchedAt: "2026-10-02T13:59:40Z", source: "yahoo:^TNX", mode: "intraday", delayStatus: "fresh", sessionStatus: "open", delaySeconds: 30, daily: null, reason: null },
        { tenor: "30Y", label: "미국채 30년", value: null, changeBp: null, previousClose: null, asOf: null, fetchedAt: null, source: null, mode: "weird", delayStatus: "weird", delaySeconds: null, daily: { value: "x" }, reason: "실시간 수집 실패: HTTP 429" },
        { label: "키 없음" },
      ],
      spreads: [
        { key: "2s10s", label: "10Y − 2Y", valueBp: 35.7, changeBp: null, mode: "mixed", reason: "10Y 실시간 − 2Y 전일 조합입니다." },
        { key: "10s30s", label: "30Y − 10Y", valueBp: null, changeBp: null, mode: "unavailable", reason: null },
      ],
      directionChecks: [
        { tenor: "10Y", intradayDirection: "down", dailyBaselineDirection: "up", changeBp: -5.6, changeVsDailyBp: 4, baselineGapBp: 9, agreement: "diverge", reason: null },
        { tenor: "30Y", intradayDirection: "sideways", dailyBaselineDirection: null, agreement: "maybe" },
      ],
      etfProxies: [
        { tenor: "10Y", symbol: "IEF", duration: 7.5, price: 88.5, previousClose: 89.0031, returnPct: -0.565, impliedChangeBp: 7.5, rateChangeBp: -5.6, etfDirection: "up", rateDirection: "down", agreement: "diverge", divergeRuns: 2.7, asOf: "2026-10-02T13:59:30Z", fetchedAt: "2026-10-02T13:59:40Z", source: "yahoo:IEF", reason: null },
        { tenor: "30Y", symbol: "TLT", agreement: "maybe", etfDirection: "sideways", divergeRuns: "x" },
        { tenor: "2Y" },
      ],
      warnings: ["10Y 실시간 변화 방향(하락)이 FRED 전일값 기준 방향(상승)과 어긋납니다.", 42],
      limitations: ["2Y는 실시간 지수가 없어 FRED 전일 공식값만 표시합니다."],
    });

    expect(rates).not.toBeNull();
    expect(rates!.status).toBe("partial");
    expect(rates!.tenors.map((tenor) => tenor.tenor)).toEqual(["2Y", "10Y", "30Y"]);
    expect(rates!.tenors[0]).toMatchObject({ mode: "daily_only", value: 4.88, changeBp: -1, daily: { date: "2026-10-01", changeBp: -1 } });
    expect(rates!.tenors[1]).toMatchObject({ mode: "intraday", delayStatus: "fresh", sessionStatus: "open", delaySeconds: 30, daily: null });
    expect(rates!.tenors[0].sessionStatus).toBe("closed");
    expect(rates!.tenors[2]).toMatchObject({ mode: "unavailable", delayStatus: "unavailable", sessionStatus: "unknown", value: null, daily: null, reason: "실시간 수집 실패: HTTP 429" });
    expect(rates!.spreads[0]).toMatchObject({ key: "2s10s", valueBp: 35.7, mode: "mixed" });
    expect(rates!.directionChecks[0]).toMatchObject({ agreement: "diverge", baselineGapBp: 9 });
    expect(rates!.directionChecks[1]).toMatchObject({ intradayDirection: "unknown", dailyBaselineDirection: "unknown", agreement: "unknown" });
    expect(rates!.etfProxies).toHaveLength(2);
    expect(rates!.etfProxies[0]).toMatchObject({ tenor: "10Y", symbol: "IEF", returnPct: -0.565, impliedChangeBp: 7.5, rateChangeBp: -5.6, etfDirection: "up", rateDirection: "down", agreement: "diverge", divergeRuns: 2 });
    expect(rates!.etfProxies[1]).toMatchObject({ symbol: "TLT", price: null, returnPct: null, etfDirection: "unknown", rateDirection: "unknown", agreement: "unknown", divergeRuns: 0, reason: null });
    expect(rates!.warnings).toEqual(["10Y 실시간 변화 방향(하락)이 FRED 전일값 기준 방향(상승)과 어긋납니다."]);
    expect(rates!.limitations).toHaveLength(1);
  });

  it("etfProxies가 없는 응답도 빈 배열로 받는다", () => {
    expect(normalizeRates({ enabled: true, status: "ok", tenors: [] })?.etfProxies).toEqual([]);
  });

  it("tenors가 없으면 null이고 알 수 없는 status는 데이터 없음으로 본다", () => {
    expect(normalizeRates(null)).toBeNull();
    expect(normalizeRates({ enabled: false })).toBeNull();
    expect(normalizeRates({ enabled: false, status: "???", tenors: [] })).toMatchObject({ enabled: false, status: "unavailable", tenors: [], spreads: [], warnings: [] });
    expect(normalizeRates({ enabled: false, status: "disabled", tenors: [] })?.status).toBe("disabled");
  });

  it("표시 헬퍼는 bp 부호·소수와 색 톤을 정한다", () => {
    expect(formatRate(5.237)).toBe("5.237%");
    expect(formatRate(4.8)).toBe("4.80%");
    expect(formatRate(null)).toBe("—");
    expect(formatBp(-5.6)).toBe("-5.6bp");
    expect(formatBp(3)).toBe("+3.0bp");
    expect(formatBp(0)).toBe("0.0bp");
    expect(formatBp(35.7, 0)).toBe("+36bp");
    expect(formatBp(null)).toBe("—");
    expect(rateChangeTone(3)).toBe("up");
    expect(rateChangeTone(-0.1)).toBe("down");
    expect(rateChangeTone(0)).toBe("flat");
    expect(rateChangeTone(null)).toBe("none");
    expect(rateModeLabel("daily_only")).toBe("FRED 전일");
    expect(ratesStatusLabel("unavailable")).toBe("데이터 없음");
    expect(formatPct(0.334)).toBe("+0.33%");
    expect(formatPct(-0.565)).toBe("-0.56%");
    expect(formatPct(-0.567)).toBe("-0.57%");
    expect(formatPct(0)).toBe("0.00%");
    expect(formatPct(null)).toBe("—");
  });

  it("ETF 대리변수 한 줄 요약은 수익률·환산 bp·일치 여부·연속 횟수를 담는다", () => {
    const proxy = { tenor: "10Y", symbol: "IEF", duration: 7.5, price: 88.5, previousClose: 89.0031, returnPct: -0.565, impliedChangeBp: 7.5, rateChangeBp: -5.6, etfDirection: "up" as const, rateDirection: "down" as const, agreement: "diverge" as const, divergeRuns: 3, asOf: null, fetchedAt: null, source: "yahoo:IEF", reason: null };
    expect(etfProxyLine(proxy)).toBe("ETF IEF -0.56% · ≈ 금리 +7.5bp · 방향 불일치 · 3회 연속");
    expect(etfProxyLine({ ...proxy, agreement: "agree", divergeRuns: 0, returnPct: 0.334, impliedChangeBp: -4.5 })).toBe("ETF IEF +0.33% · ≈ 금리 -4.5bp · 방향 일치");
    expect(etfProxyLine({ ...proxy, agreement: "unknown", divergeRuns: 0, reason: "실시간 금리가 없어 방향을 비교할 수 없습니다." })).toBe("ETF IEF -0.56% · ≈ 금리 +7.5bp · 비교 불가 · 실시간 금리가 없어 방향을 비교할 수 없습니다.");
    expect(etfProxyLine({ ...proxy, price: null, returnPct: null, impliedChangeBp: null, agreement: "unknown", divergeRuns: 0, reason: "ETF 가격 수집 실패: HTTP 429" })).toBe("ETF IEF 없음 · ETF 가격 수집 실패: HTTP 429");
  });
});
