export type MarketMoodAsset = {
  key: string;
  label: string;
  symbol: string;
  assetKind: string;
  proxy: boolean;
  isAvailable: boolean;
  direction: "up" | "down" | "flat" | "unknown";
  changePercent: number | null;
  asOf: string | null;
  source: string;
  delayStatus: "fresh" | "stale" | "unavailable";
  sessionStatus: "open" | "closed" | "unknown";
  reason: string | null;
};

export type MarketMoodResponse = {
  asOf: string;
  status: "available" | "partial" | "unavailable";
  score: number | null;
  direction: "up" | "down" | "flat" | "unavailable";
  availableCount: number;
  totalCount: number;
  aggregation: string;
  source: string;
  refreshSeconds: number;
  limitations: string[];
  evidence: string[];
  economicCalendar: EconomicCalendarSnapshot;
  assets: MarketMoodAsset[];
};

export type EconomicCalendarEvent = {
  id: string;
  title: string;
  scheduledAt: string;
  status: "published" | "unpublished" | "delayed" | "unsupported";
  actual: string | null;
  forecast: string | null;
  previous: string | null;
  unit: string | null;
  impactDirection: string;
  source: string;
  reason: string | null;
};

export type EconomicCalendarSnapshot = {
  marketDate: string;
  timeZone: string;
  status: "available" | "partial" | "unpublished" | "delayed" | "unsupported";
  source: string;
  asOf: string;
  reason: string | null;
  events: EconomicCalendarEvent[];
};

const record = (value: unknown): Record<string, unknown> | null =>
  typeof value === "object" && value !== null ? value as Record<string, unknown> : null;
const text = (value: unknown): string | null => typeof value === "string" ? value : null;
const finite = (value: unknown): number | null => typeof value === "number" && Number.isFinite(value) ? value : null;

export const normalizeMarketMood = (value: unknown): MarketMoodResponse | null => {
  const root = record(value);
  if (!root || !Array.isArray(root.assets)) return null;
  const assets = root.assets.flatMap((raw): MarketMoodAsset[] => {
    const asset = record(raw);
    const key = text(asset?.key);
    const label = text(asset?.label);
    const symbol = text(asset?.symbol);
    if (!asset || !key || !label || !symbol) return [];
    const direction = text(asset.direction);
    const delayStatus = text(asset.delayStatus);
    const sessionStatus = text(asset.sessionStatus);
    return [{
      key,
      label,
      symbol,
      assetKind: text(asset.assetKind) ?? "unknown",
      proxy: asset.proxy === true,
      isAvailable: asset.isAvailable === true,
      direction: direction === "up" || direction === "down" || direction === "flat" ? direction : "unknown",
      changePercent: finite(asset.changePercent),
      asOf: text(asset.asOf),
      source: text(asset.source) ?? "출처 미상",
      delayStatus: delayStatus === "fresh" || delayStatus === "stale" ? delayStatus : "unavailable",
      sessionStatus: sessionStatus === "open" || sessionStatus === "closed" ? sessionStatus : "unknown",
      reason: text(asset.reason),
    }];
  });
  const status = text(root.status);
  const direction = text(root.direction);
  const rawCalendar = record(root.economicCalendar);
  const calendarStatus = text(rawCalendar?.status);
  const calendarEvents = Array.isArray(rawCalendar?.events) ? rawCalendar.events.flatMap((raw): EconomicCalendarEvent[] => {
    const event = record(raw);
    const id = text(event?.id);
    const title = text(event?.title);
    const scheduledAt = text(event?.scheduledAt);
    if (!event || !id || !title || !scheduledAt) return [];
    const eventStatus = text(event.status);
    return [{
      id, title, scheduledAt,
      status: eventStatus === "published" || eventStatus === "unpublished" || eventStatus === "delayed" ? eventStatus : "unsupported",
      actual: text(event.actual),
      forecast: text(event.forecast),
      previous: text(event.previous),
      unit: text(event.unit),
      impactDirection: text(event.impactDirection) ?? "unknown",
      source: text(event.source) ?? "출처 미상",
      reason: text(event.reason),
    }];
  }) : [];
  return {
    asOf: text(root.asOf) ?? "",
    status: status === "available" || status === "partial" ? status : "unavailable",
    score: finite(root.score),
    direction: direction === "up" || direction === "down" || direction === "flat" ? direction : "unavailable",
    availableCount: finite(root.availableCount) ?? assets.filter((asset) => asset.isAvailable).length,
    totalCount: finite(root.totalCount) ?? assets.length,
    aggregation: text(root.aggregation) ?? "equal_weight_available_only",
    source: text(root.source) ?? "출처 미상",
    refreshSeconds: finite(root.refreshSeconds) ?? 60,
    limitations: Array.isArray(root.limitations) ? root.limitations.flatMap((item) => text(item) ?? []) : [],
    evidence: Array.isArray(root.evidence) ? root.evidence.flatMap((item) => text(item) ?? []) : [],
    economicCalendar: {
      marketDate: text(rawCalendar?.marketDate) ?? "",
      timeZone: text(rawCalendar?.timeZone) ?? "America/New_York",
      status: calendarStatus === "available" || calendarStatus === "partial" || calendarStatus === "unpublished" || calendarStatus === "delayed" ? calendarStatus : "unsupported",
      source: text(rawCalendar?.source) ?? "미연결",
      asOf: text(rawCalendar?.asOf) ?? "",
      reason: text(rawCalendar?.reason),
      events: calendarEvents,
    },
    assets,
  };
};

export const marketMoodBadge = (mood: MarketMoodResponse) => {
  if (mood.score == null) return { label: "시장 분위기 정보 없음", className: "news-badge neutral" };
  const value = `${mood.score >= 0 ? "+" : ""}${mood.score.toFixed(2)}%`;
  if (mood.direction === "up") return { label: `시장 분위기 상승 ${value}`, className: "news-badge positive" };
  if (mood.direction === "down") return { label: `시장 분위기 하락 ${value}`, className: "news-badge negative" };
  return { label: `시장 분위기 보합 ${value}`, className: "news-badge neutral" };
};

export const marketMoodDirectionLabel = (asset: MarketMoodAsset): string => {
  const direction = asset.direction === "up" ? "상승" : asset.direction === "down" ? "하락" : asset.direction === "flat" ? "보합" : "";
  if (!asset.isAvailable) return direction ? `집계 제외 · ${direction}` : "집계 제외";
  return direction || "방향 없음";
};
