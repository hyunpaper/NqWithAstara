import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import TreasuryRatesStrip from "./TreasuryRatesStrip";
import type { RateTenor, RatesResponse } from "./ratesTypes";

const tenor = (overrides: Partial<RateTenor> & Pick<RateTenor, "tenor">): RateTenor => ({
  label: `미국채 ${overrides.tenor}`,
  value: null,
  changeBp: null,
  previousClose: null,
  asOf: null,
  fetchedAt: null,
  source: null,
  mode: "unavailable",
  delayStatus: "unavailable",
  sessionStatus: "unknown",
  delaySeconds: null,
  daily: null,
  reason: null,
  ...overrides,
});

const base: RatesResponse = {
  enabled: true,
  status: "ok",
  asOf: "2026-10-02T14:00:00Z",
  intradaySource: "Yahoo Finance 차트 API (비공식 · ^TNX·^TYX)",
  dailySource: "FRED DGS2·DGS10·DGS30 (일별 공식값)",
  refreshSeconds: 60,
  tenors: [
    tenor({ tenor: "2Y", value: 4.88, changeBp: -1, previousClose: 4.89, asOf: "2026-10-01T20:00:00Z", source: "fred:DGS2", mode: "daily_only", delayStatus: "fresh", sessionStatus: "closed", daily: { value: 4.88, date: "2026-10-01", previous: 4.89, previousDate: "2026-09-30", changeBp: -1, source: "fred:DGS2", fetchedAt: "2026-10-02T12:00:00Z" }, reason: "실시간 지수가 없어 FRED 전일 공식값(전일 대비 변화)만 표시합니다." }),
    tenor({ tenor: "10Y", value: 5.237, changeBp: -5.6, previousClose: 5.293, asOf: "2026-10-02T13:59:30Z", fetchedAt: "2026-10-02T13:59:40Z", source: "yahoo:^TNX", mode: "intraday", delayStatus: "fresh", sessionStatus: "open", delaySeconds: 30 }),
    tenor({ tenor: "30Y", value: 5.603, changeBp: 2.4, previousClose: 5.579, asOf: "2026-10-02T13:59:30Z", fetchedAt: "2026-10-02T13:59:40Z", source: "yahoo:^TYX", mode: "intraday", delayStatus: "fresh", sessionStatus: "open", delaySeconds: 30 }),
  ],
  spreads: [
    { key: "2s10s", label: "10Y − 2Y", valueBp: 35.7, changeBp: null, mode: "mixed", reason: "10Y 실시간 − 2Y 전일 조합입니다." },
    { key: "10s30s", label: "30Y − 10Y", valueBp: 36.6, changeBp: 8, mode: "intraday", reason: null },
  ],
  directionChecks: [],
  etfProxies: [
    { tenor: "2Y", symbol: "SHY", duration: 1.9, price: 81.1, previousClose: 80.959, returnPct: 0.174, impliedChangeBp: -9.2, rateChangeBp: null, etfDirection: "down", rateDirection: "unknown", agreement: "unknown", divergeRuns: 0, asOf: "2026-10-02T13:59:30Z", fetchedAt: "2026-10-02T13:59:40Z", source: "yahoo:SHY", reason: "실시간 금리가 없어 방향을 비교할 수 없습니다." },
    { tenor: "10Y", symbol: "IEF", duration: 7.5, price: 89.3, previousClose: 89.0031, returnPct: 0.334, impliedChangeBp: -4.5, rateChangeBp: -5.6, etfDirection: "down", rateDirection: "down", agreement: "agree", divergeRuns: 0, asOf: "2026-10-02T13:59:30Z", fetchedAt: "2026-10-02T13:59:40Z", source: "yahoo:IEF", reason: null },
  ],
  warnings: [],
  limitations: ["2Y는 실시간 지수가 없어 FRED 전일 공식값만 표시합니다."],
};

describe("TreasuryRatesStrip", () => {
  it("만기별 값·당일 변화·2s10s를 주식 관점 색으로 보여주고 툴팁에 표기 기준을 적는다", () => {
    render(<TreasuryRatesStrip rates={base} />);
    const strip = screen.getByRole("group", { name: "미국채 금리" });
    expect(strip.getAttribute("title")).toContain("상승=빨강 · 하락=초록은 주식 관점 표기");
    expect(strip.getAttribute("title")).toContain("매매 판정에 쓰지 않습니다");
    expect(strip.textContent).toContain("2Y4.88%-1.0bp전일");
    expect(strip.textContent).toContain("10Y5.237%-5.6bp");
    expect(strip.textContent).toContain("30Y5.603%+2.4bp");
    expect(strip.textContent).not.toContain("마감");
    expect(strip.textContent).toContain("2s10s+36bp");
    expect(strip.querySelector('[data-mode="intraday"] .rates-change.down')?.textContent).toBe("-5.6bp");
    expect(strip.querySelector('.rates-change.up')?.textContent).toBe("+2.4bp");
    expect(screen.getByTitle(/FRED 2026-10-01 4.88%/).getAttribute("title")).toContain("FRED 전일");
    expect(screen.getByTitle(/ETF IEF/).getAttribute("title")).toContain("ETF IEF +0.33% · ≈ 금리 -4.5bp · 방향 일치");
    expect(screen.getByTitle(/ETF SHY/).getAttribute("title")).toContain("ETF SHY +0.17% · ≈ 금리 -9.2bp · 비교 불가 · 실시간 금리가 없어");
    expect(strip.textContent).not.toContain("ETF 괴리");
    expect(screen.queryByRole("note")).toBeNull();
  });

  it("ETF 대리변수와 방향이 어긋나면 해당 만기에 괴리 태그를 붙이고 툴팁에 연속 횟수를 적는다", () => {
    render(<TreasuryRatesStrip rates={{
      ...base,
      etfProxies: [
        { ...base.etfProxies[1], price: 88.5, returnPct: -0.565, impliedChangeBp: 7.5, etfDirection: "up", agreement: "diverge", divergeRuns: 3 },
        { tenor: "30Y", symbol: "TLT", duration: 16.5, price: null, previousClose: null, returnPct: null, impliedChangeBp: null, rateChangeBp: null, etfDirection: "unknown", rateDirection: "unknown", agreement: "unknown", divergeRuns: 0, asOf: null, fetchedAt: null, source: null, reason: "ETF 가격 수집 실패: HTTP 429" },
      ],
      warnings: ["10Y ETF IEF 당일 수익률(-0.57% ≈ 금리 +7.5bp)이 실시간 금리 변화(-5.6bp)와 방향이 어긋납니다.", "10Y ETF IEF 괴리가 3회 연속 이어집니다."],
    }} />);
    const strip = screen.getByRole("group", { name: "미국채 금리" });
    expect(strip.textContent).toContain("10Y5.237%-5.6bpETF 괴리");
    expect(strip.textContent).not.toContain("30Y5.603%+2.4bpETF 괴리");
    expect(strip.querySelector('[data-etf="IEF"]')).not.toBeNull();
    expect(screen.getByTitle(/^미국채 10Y/).getAttribute("title")).toContain("ETF IEF -0.56% · ≈ 금리 +7.5bp · 방향 불일치 · 3회 연속");
    expect(screen.getByTitle(/ETF TLT 없음/).getAttribute("title")).toContain("HTTP 429");
    expect(screen.getByRole("note", { name: "금리 경고" }).getAttribute("title")).toContain("3회 연속");
  });

  it("지연·결측 만기를 표시하고 경고 개수를 알린다", () => {
    render(<TreasuryRatesStrip rates={{
      ...base,
      status: "partial",
      tenors: [
        base.tenors[0],
        tenor({ ...base.tenors[1], delayStatus: "stale", sessionStatus: "closed", reason: "마지막 수집 후 갱신이 없습니다." }),
        tenor({ tenor: "30Y", reason: "실시간 수집 실패: HTTP 429" }),
      ],
      spreads: [{ key: "2s10s", label: "10Y − 2Y", valueBp: 35.7, changeBp: null, mode: "mixed", reason: null }],
      warnings: ["10Y 실시간 값이 20분째 갱신되지 않았습니다.", "10Y 출처 전일 종가와 FRED 전일값이 +9.0bp 차이 납니다."],
    }} />);
    const strip = screen.getByRole("group", { name: "미국채 금리" });
    expect(strip.textContent).toContain("10Y5.237%-5.6bp마감지연");
    expect(screen.getAllByTitle(/직전 세션 종가/)).toHaveLength(2);
    expect(strip.textContent).toContain("30Y—없음");
    expect(screen.getByTitle(/HTTP 429/)).toBeTruthy();
    const note = screen.getByRole("note", { name: "금리 경고" });
    expect(note.textContent).toBe("⚠ 2");
    expect(note.getAttribute("title")).toContain("+9.0bp 차이");
  });

  it("값이 전혀 없으면 데이터 없음을, 대기 중이면 수집 대기를 표시한다", () => {
    const empty = { ...base, status: "unavailable" as const, tenors: base.tenors.map((item) => tenor({ tenor: item.tenor })), spreads: [] };
    const { rerender } = render(<TreasuryRatesStrip rates={empty} />);
    expect(screen.getByRole("group", { name: "미국채 금리" }).textContent).toBe("국채데이터 없음");
    rerender(<TreasuryRatesStrip rates={{ ...empty, status: "idle" }} />);
    expect(screen.getByRole("group", { name: "미국채 금리" }).textContent).toBe("국채수집 대기");
  });

  it("기능이 꺼져 있으면 아무것도 그리지 않는다", () => {
    const { container } = render(<TreasuryRatesStrip rates={{ ...base, enabled: false, status: "disabled" }} />);
    expect(container.firstChild).toBeNull();
  });
});
