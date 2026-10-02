// 이슈 #316·#325 — /api/rates(실시간 미국채 2Y·10Y·30Y, ETF 대리변수 괴리) 응답 타입과 표시 헬퍼.
// 금리는 표시·기록용 참고값이며 매매 판정에 쓰지 않는다. 상승=빨강/하락=초록은 주식 관점 표기다.

export type RateMode = "intraday" | "daily_only" | "mixed" | "daily" | "unavailable";
export type RateDelayStatus = "fresh" | "stale" | "unavailable";
export type RateDirection = "up" | "down" | "flat" | "unknown";
export type RateSessionStatus = "open" | "closed" | "unknown";

export type RateDaily = {
  value: number;
  date: string;
  previous: number | null;
  previousDate: string | null;
  changeBp: number | null;
  source: string;
  fetchedAt: string | null;
};

export type RateTenor = {
  tenor: string;
  label: string;
  value: number | null;
  changeBp: number | null;
  previousClose: number | null;
  asOf: string | null;
  fetchedAt: string | null;
  source: string | null;
  mode: RateMode;
  delayStatus: RateDelayStatus;
  sessionStatus: RateSessionStatus;
  delaySeconds: number | null;
  daily: RateDaily | null;
  reason: string | null;
};

export type RateSpread = {
  key: string;
  label: string;
  valueBp: number | null;
  changeBp: number | null;
  mode: RateMode;
  reason: string | null;
};

export type RateDirectionCheck = {
  tenor: string;
  intradayDirection: RateDirection;
  dailyBaselineDirection: RateDirection;
  changeBp: number | null;
  changeVsDailyBp: number | null;
  baselineGapBp: number | null;
  agreement: "agree" | "diverge" | "unknown";
  reason: string | null;
};

export type RateEtfProxy = {
  tenor: string;
  symbol: string;
  duration: number | null;
  price: number | null;
  previousClose: number | null;
  returnPct: number | null;
  impliedChangeBp: number | null;
  rateChangeBp: number | null;
  etfDirection: RateDirection;
  rateDirection: RateDirection;
  agreement: "agree" | "diverge" | "unknown";
  divergeRuns: number;
  asOf: string | null;
  fetchedAt: string | null;
  source: string | null;
  reason: string | null;
};

export type RatesResponse = {
  enabled: boolean;
  status: "disabled" | "idle" | "ok" | "partial" | "unavailable";
  asOf: string;
  intradaySource: string;
  dailySource: string;
  refreshSeconds: number;
  tenors: RateTenor[];
  spreads: RateSpread[];
  directionChecks: RateDirectionCheck[];
  etfProxies: RateEtfProxy[];
  warnings: string[];
  limitations: string[];
};

const record = (value: unknown): Record<string, unknown> | null =>
  typeof value === "object" && value !== null ? value as Record<string, unknown> : null;
const text = (value: unknown): string | null => typeof value === "string" ? value : null;
const finite = (value: unknown): number | null => typeof value === "number" && Number.isFinite(value) ? value : null;
const strings = (value: unknown): string[] => Array.isArray(value) ? value.flatMap((item) => text(item) ?? []) : [];
const mode = (value: unknown): RateMode =>
  value === "intraday" || value === "daily_only" || value === "mixed" || value === "daily" ? value : "unavailable";
const delay = (value: unknown): RateDelayStatus => value === "fresh" || value === "stale" ? value : "unavailable";
const session = (value: unknown): RateSessionStatus => value === "open" || value === "closed" ? value : "unknown";
const direction = (value: unknown): RateDirection =>
  value === "up" || value === "down" || value === "flat" ? value : "unknown";

export const normalizeRates = (value: unknown): RatesResponse | null => {
  const root = record(value);
  if (!root || !Array.isArray(root.tenors)) return null;
  const tenors = root.tenors.flatMap((raw): RateTenor[] => {
    const tenor = record(raw);
    const key = text(tenor?.tenor);
    if (!tenor || !key) return [];
    const rawDaily = record(tenor.daily);
    const dailyValue = finite(rawDaily?.value);
    const dailyDate = text(rawDaily?.date);
    return [{
      tenor: key,
      label: text(tenor.label) ?? key,
      value: finite(tenor.value),
      changeBp: finite(tenor.changeBp),
      previousClose: finite(tenor.previousClose),
      asOf: text(tenor.asOf),
      fetchedAt: text(tenor.fetchedAt),
      source: text(tenor.source),
      mode: mode(tenor.mode),
      delayStatus: delay(tenor.delayStatus),
      sessionStatus: session(tenor.sessionStatus),
      delaySeconds: finite(tenor.delaySeconds),
      daily: rawDaily && dailyValue != null && dailyDate ? {
        value: dailyValue,
        date: dailyDate,
        previous: finite(rawDaily.previous),
        previousDate: text(rawDaily.previousDate),
        changeBp: finite(rawDaily.changeBp),
        source: text(rawDaily.source) ?? "FRED",
        fetchedAt: text(rawDaily.fetchedAt),
      } : null,
      reason: text(tenor.reason),
    }];
  });
  const spreads = Array.isArray(root.spreads) ? root.spreads.flatMap((raw): RateSpread[] => {
    const spread = record(raw);
    const key = text(spread?.key);
    if (!spread || !key) return [];
    return [{
      key,
      label: text(spread.label) ?? key,
      valueBp: finite(spread.valueBp),
      changeBp: finite(spread.changeBp),
      mode: mode(spread.mode),
      reason: text(spread.reason),
    }];
  }) : [];
  const directionChecks = Array.isArray(root.directionChecks) ? root.directionChecks.flatMap((raw): RateDirectionCheck[] => {
    const check = record(raw);
    const tenor = text(check?.tenor);
    if (!check || !tenor) return [];
    const agreement = text(check.agreement);
    return [{
      tenor,
      intradayDirection: direction(check.intradayDirection),
      dailyBaselineDirection: direction(check.dailyBaselineDirection),
      changeBp: finite(check.changeBp),
      changeVsDailyBp: finite(check.changeVsDailyBp),
      baselineGapBp: finite(check.baselineGapBp),
      agreement: agreement === "agree" || agreement === "diverge" ? agreement : "unknown",
      reason: text(check.reason),
    }];
  }) : [];
  const etfProxies = Array.isArray(root.etfProxies) ? root.etfProxies.flatMap((raw): RateEtfProxy[] => {
    const proxy = record(raw);
    const tenor = text(proxy?.tenor);
    const symbol = text(proxy?.symbol);
    if (!proxy || !tenor || !symbol) return [];
    const agreement = text(proxy.agreement);
    return [{
      tenor,
      symbol,
      duration: finite(proxy.duration),
      price: finite(proxy.price),
      previousClose: finite(proxy.previousClose),
      returnPct: finite(proxy.returnPct),
      impliedChangeBp: finite(proxy.impliedChangeBp),
      rateChangeBp: finite(proxy.rateChangeBp),
      etfDirection: direction(proxy.etfDirection),
      rateDirection: direction(proxy.rateDirection),
      agreement: agreement === "agree" || agreement === "diverge" ? agreement : "unknown",
      divergeRuns: Math.max(0, Math.trunc(finite(proxy.divergeRuns) ?? 0)),
      asOf: text(proxy.asOf),
      fetchedAt: text(proxy.fetchedAt),
      source: text(proxy.source),
      reason: text(proxy.reason),
    }];
  }) : [];
  const status = text(root.status);
  return {
    enabled: root.enabled === true,
    status: status === "disabled" || status === "idle" || status === "ok" || status === "partial" ? status : "unavailable",
    asOf: text(root.asOf) ?? "",
    intradaySource: text(root.intradaySource) ?? "출처 미상",
    dailySource: text(root.dailySource) ?? "출처 미상",
    refreshSeconds: finite(root.refreshSeconds) ?? 60,
    tenors,
    spreads,
    directionChecks,
    etfProxies,
    warnings: strings(root.warnings),
    limitations: strings(root.limitations),
  };
};

export const formatRate = (value: number | null): string =>
  value == null ? "—" : `${value.toLocaleString("ko-KR", { minimumFractionDigits: 2, maximumFractionDigits: 3 })}%`;

export const formatBp = (value: number | null, digits = 1): string => {
  if (value == null) return "—";
  const rounded = Number(value.toFixed(digits));
  const sign = rounded > 0 ? "+" : "";
  return `${sign}${rounded.toFixed(digits)}bp`;
};

export const formatPct = (value: number | null, digits = 2): string => {
  if (value == null) return "—";
  const rounded = Number(value.toFixed(digits));
  const sign = rounded > 0 ? "+" : "";
  return `${sign}${rounded.toFixed(digits)}%`;
};

export const etfAgreementLabel = (agreement: RateEtfProxy["agreement"]): string => ({
  agree: "방향 일치",
  diverge: "방향 불일치",
  unknown: "비교 불가",
})[agreement];

export const etfProxyLine = (proxy: RateEtfProxy): string => {
  if (proxy.price == null) return `ETF ${proxy.symbol} 없음${proxy.reason ? ` · ${proxy.reason}` : ""}`;
  const parts = [`ETF ${proxy.symbol} ${formatPct(proxy.returnPct)}`];
  if (proxy.impliedChangeBp != null) parts.push(`≈ 금리 ${formatBp(proxy.impliedChangeBp)}`);
  parts.push(etfAgreementLabel(proxy.agreement));
  if (proxy.agreement === "diverge" && proxy.divergeRuns > 1) parts.push(`${proxy.divergeRuns}회 연속`);
  const line = parts.join(" · ");
  return proxy.reason ? `${line} · ${proxy.reason}` : line;
};

export const rateChangeTone = (changeBp: number | null): "up" | "down" | "flat" | "none" => {
  if (changeBp == null) return "none";
  if (changeBp >= 0.05) return "up";
  if (changeBp <= -0.05) return "down";
  return "flat";
};

export const rateModeLabel = (mode: RateMode): string => ({
  intraday: "실시간",
  daily_only: "FRED 전일",
  mixed: "혼합",
  daily: "전일",
  unavailable: "없음",
})[mode];

export const rateSessionLabel = (status: RateSessionStatus): string => ({
  open: "장중",
  closed: "직전 세션 종가",
  unknown: "세션 미상",
})[status];

export const rateDelayLabel = (status: RateDelayStatus): string => ({
  fresh: "최신",
  stale: "지연",
  unavailable: "없음",
})[status];

export const ratesStatusLabel = (status: RatesResponse["status"]): string => ({
  disabled: "비활성",
  idle: "대기",
  ok: "정상",
  partial: "일부",
  unavailable: "데이터 없음",
})[status];
