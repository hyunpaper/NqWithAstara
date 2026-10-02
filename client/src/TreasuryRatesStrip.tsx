import { absoluteTimeKst } from "./newsFormat";
import {
  etfProxyLine,
  formatBp,
  formatRate,
  rateChangeTone,
  rateDelayLabel,
  rateModeLabel,
  rateSessionLabel,
  ratesStatusLabel,
  type RateEtfProxy,
  type RateTenor,
  type RatesResponse,
} from "./ratesTypes";

const COLOR_NOTE = "상승=빨강 · 하락=초록은 주식 관점 표기입니다(금리 상승은 주가에 부담). 금리는 참고 정보이며 매매 판정에 쓰지 않습니다.";

const tenorTitle = (tenor: RateTenor, proxies: RateEtfProxy[]): string => {
  const lines = [`${tenor.label} · ${rateModeLabel(tenor.mode)} · ${rateDelayLabel(tenor.delayStatus)} · ${rateSessionLabel(tenor.sessionStatus)}`];
  if (tenor.value != null) lines.push(`현재 ${formatRate(tenor.value)} · 당일 ${formatBp(tenor.changeBp)}${tenor.previousClose == null ? "" : ` (전일 종가 ${formatRate(tenor.previousClose)})`}`);
  if (tenor.asOf) lines.push(`데이터 시각 ${absoluteTimeKst(tenor.asOf)}${tenor.delaySeconds == null ? "" : ` · 지연 ${Math.round(tenor.delaySeconds / 60)}분`}`);
  if (tenor.daily) lines.push(`FRED ${tenor.daily.date} ${formatRate(tenor.daily.value)}${tenor.daily.changeBp == null ? "" : ` (${formatBp(tenor.daily.changeBp)})`}`);
  proxies.forEach((proxy) => lines.push(etfProxyLine(proxy)));
  if (tenor.source) lines.push(`출처 ${tenor.source}`);
  if (tenor.reason) lines.push(tenor.reason);
  return lines.join("\n");
};

export default function TreasuryRatesStrip({ rates }: { rates: RatesResponse }) {
  if (!rates.enabled) return null;
  const hasAnyValue = rates.tenors.some((tenor) => tenor.value != null);
  const twoTen = rates.spreads.find((spread) => spread.key === "2s10s") ?? null;
  const stripTitle = [`미국채 금리 · ${ratesStatusLabel(rates.status)} · 조회 ${absoluteTimeKst(rates.asOf)}`, COLOR_NOTE, rates.intradaySource, rates.dailySource, ...rates.limitations].join("\n");
  return (
    <div className={`rates-strip ${rates.status}`} role="group" aria-label="미국채 금리" title={stripTitle}>
      <span className="rates-strip-title">국채</span>
      {!hasAnyValue && <span className="rates-strip-status">{rates.status === "idle" ? "수집 대기" : "데이터 없음"}</span>}
      {hasAnyValue && rates.tenors.map((tenor) => {
        const tone = rateChangeTone(tenor.changeBp);
        const proxies = rates.etfProxies.filter((proxy) => proxy.tenor === tenor.tenor);
        const diverging = proxies.filter((proxy) => proxy.agreement === "diverge");
        return (
          <span key={tenor.tenor} className={`rates-item ${tenor.delayStatus}`} title={tenorTitle(tenor, proxies)} data-mode={tenor.mode}>
            <b>{tenor.tenor}</b>
            <span className="rates-value">{formatRate(tenor.value)}</span>
            <span className={`rates-change ${tone}`}>{tenor.value == null ? "" : formatBp(tenor.changeBp)}</span>
            {tenor.mode === "daily_only" && <small className="rates-tag">전일</small>}
            {tenor.mode === "intraday" && tenor.sessionStatus === "closed" && <small className="rates-tag">마감</small>}
            {tenor.mode === "intraday" && tenor.delayStatus === "stale" && <small className="rates-tag warn">지연</small>}
            {tenor.value == null && <small className="rates-tag warn">없음</small>}
            {diverging.length > 0 && <small className="rates-tag warn" data-etf={diverging.map((proxy) => proxy.symbol).join(",")}>ETF 괴리</small>}
          </span>
        );
      })}
      {hasAnyValue && twoTen && twoTen.valueBp != null && (
        <span className="rates-item spread" title={`2s10s(10Y − 2Y) · ${rateModeLabel(twoTen.mode)}${twoTen.reason ? `\n${twoTen.reason}` : ""}`}>
          <b>2s10s</b>
          <span className="rates-value">{formatBp(twoTen.valueBp, 0)}</span>
        </span>
      )}
      {rates.warnings.length > 0 && (
        <span className="rates-warning" role="note" aria-label="금리 경고" title={rates.warnings.join("\n")}>⚠ {rates.warnings.length}</span>
      )}
    </div>
  );
}
