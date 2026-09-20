// 이슈 #152 — 뉴스 감성 응답 타입/정규화. 서버 계약(#151, NewsQueryService.cs)을
// 그대로 신뢰하지 않고 결측·알 수 없는 필드를 안전하게 무시한다.

export type NewsSentimentLabel = "positive" | "negative" | "neutral" | "unclassified";
export type NewsInputKind = "summary" | "body" | "headline";

export type NewsArticle = {
  id: string;
  title: string;
  source: string | null;
  createdAt: string | null;
  tickers: string[];
  matchedSymbols: string[];
  symbols: string[];
  sentiment: NewsSentimentLabel;
  strength: number | null;
  reason: string | null;
  model: string | null;
  latencyMs: number | null;
  classifiedAt: string | null;
  inputKind: NewsInputKind | null;
  entities: NewsEntity[];
};
export type NewsEntity = { symbol: string; name: string; industry: string; sentimentScore: number | null; matchScore: number | null };

export type NewsArticlesResponse = {
  enabled: boolean;
  count: number;
  articles: NewsArticle[];
};

export type NewsSymbolScore = {
  symbol: string;
  score: number;
  count: number;
  latestAt: string | null;
  weight?: number | null;
};

export type NewsSentimentResponse = {
  enabled: boolean;
  asOf: string | null;
  halfLifeMinutes: number | null;
  market: NewsSymbolScore | null;
  symbols: NewsSymbolScore[];
};

/** `/api/health`의 news 필드(§NewsRuntimeState.Health). 구버전 서버에는 없을 수 있다. */
export type NewsHealth = {
  enabled: boolean;
  lastPollAt: string | null;
  queue: number | null;
  dropped: number | null;
  seen: number | null;
  classified: number | null;
  storageLimited: boolean | null;
  ollama: "ok" | "down" | null;
};

const KNOWN_SENTIMENTS: NewsSentimentLabel[] = ["positive", "negative", "neutral", "unclassified"];
const KNOWN_INPUT_KINDS: NewsInputKind[] = ["summary", "body", "headline"];

const str = (v: unknown): string | null => (typeof v === "string" && v.length > 0 ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);
const bool = (v: unknown): boolean => v === true;
const strArray = (v: unknown): string[] =>
  Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];

const sentimentOf = (v: unknown): NewsSentimentLabel =>
  KNOWN_SENTIMENTS.includes(v as NewsSentimentLabel) ? (v as NewsSentimentLabel) : "unclassified";
const inputKindOf = (v: unknown): NewsInputKind | null =>
  KNOWN_INPUT_KINDS.includes(v as NewsInputKind) ? (v as NewsInputKind) : null;

/** id 없는 기사는 목록에서 제외한다 — 키·클릭 대상 식별이 불가능하다. */
export const normalizeArticle = (raw: unknown): NewsArticle | null => {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const id = str(r.id);
  if (!id) return null;
  return {
    id,
    title: str(r.title) ?? "(제목 없음)",
    source: str(r.source),
    createdAt: str(r.createdAt),
    tickers: strArray(r.tickers),
    matchedSymbols: strArray(r.matchedSymbols),
    symbols: strArray(r.symbols),
    sentiment: sentimentOf(r.sentiment),
    strength: num(r.strength),
    reason: str(r.reason),
    model: str(r.model),
    latencyMs: num(r.latencyMs),
    classifiedAt: str(r.classifiedAt),
    inputKind: inputKindOf(r.inputKind),
    entities: Array.isArray(r.entities) ? r.entities.filter((e): e is Record<string, unknown> => typeof e === "object" && e !== null).map((e) => ({ symbol: str(e.symbol) ?? "", name: str(e.name) ?? "", industry: str(e.industry) ?? "", sentimentScore: num(e.sentimentScore), matchScore: num(e.matchScore) })).filter((e) => e.industry.length > 0) : [],
  };
};

export const normalizeArticlesResponse = (raw: unknown): NewsArticlesResponse => {
  const r = (typeof raw === "object" && raw !== null ? raw : {}) as Record<string, unknown>;
  const articles = Array.isArray(r.articles)
    ? r.articles.map(normalizeArticle).filter((x): x is NewsArticle => x !== null)
    : [];
  return { enabled: bool(r.enabled), count: num(r.count) ?? articles.length, articles };
};

const normalizeScore = (raw: unknown): NewsSymbolScore | null => {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const symbol = str(r.symbol);
  const score = num(r.score);
  if (!symbol || score === null) return null;
  return { symbol, score, count: num(r.count) ?? 0, latestAt: str(r.latestAt), weight: num(r.weight) };
};

export const normalizeSentimentResponse = (raw: unknown): NewsSentimentResponse => {
  const r = (typeof raw === "object" && raw !== null ? raw : {}) as Record<string, unknown>;
  const symbols = Array.isArray(r.symbols)
    ? r.symbols.map(normalizeScore).filter((x): x is NewsSymbolScore => x !== null)
    : [];
  return {
    enabled: bool(r.enabled),
    asOf: str(r.asOf),
    halfLifeMinutes: num(r.halfLifeMinutes),
    market: normalizeScore(r.market),
    symbols,
  };
};

export const normalizeNewsHealth = (raw: unknown): NewsHealth | null => {
  if (typeof raw !== "object" || raw === null) return null;
  const r = raw as Record<string, unknown>;
  const ollama = r.ollama === "ok" || r.ollama === "down" ? r.ollama : null;
  return {
    enabled: bool(r.enabled),
    lastPollAt: str(r.lastPollAt),
    queue: num(r.queue),
    dropped: num(r.dropped),
    seen: num(r.seen),
    classified: num(r.classified),
    storageLimited: typeof r.storageLimited === "boolean" ? r.storageLimited : null,
    ollama,
  };
};

/**
 * 뉴스 UI 렌더 여부의 단일 판정 지점. health.news.enabled와 sentiment.enabled 중
 * 하나라도 false/결측이면 배지·패널을 렌더하지 않는다(§2, §3 공통 조건).
 */
export const shouldRenderNewsUi = (
  healthEnabled: boolean | null | undefined,
  sentimentEnabled: boolean | null | undefined,
): boolean => healthEnabled === true && sentimentEnabled === true;

/** 사이드바 종목 행 배지용 점수 조회. 없으면 null(배지 미표시). */
export const findSymbolScore = (
  symbols: NewsSymbolScore[],
  symbol: string,
): NewsSymbolScore | null =>
  symbols.find((s) => s.symbol.toUpperCase() === symbol.toUpperCase()) ?? null;
