import { useState } from "react";
import { useVisiblePolling } from "./useVisiblePolling";
import { normalizeArticlesResponse, type NewsArticle } from "./newsTypes";

type Health = { enabled?: boolean; lastSuccessAt?: string | null; lastError?: string | null };
const sentimentLabel = (value: NewsArticle["sentiment"]) => ({
  positive: "호재",
  negative: "악재",
  neutral: "중립",
  unclassified: "미분류",
}[value]);
export const aggregateTickerArticles = (articles: NewsArticle[], now = Date.now()) => {
  const recent = articles.filter((a) => a.createdAt != null && Date.parse(a.createdAt) >= now - 24 * 60 * 60 * 1000).slice(0, 200);
  const counts = recent.reduce<Record<string, number>>((out, article) => { out[article.sentiment] = (out[article.sentiment] ?? 0) + 1; return out; }, {});
  const sectors = recent.flatMap((a) => a.entities.map((e) => ({ sector: e.industry, sentiment: a.sentiment }))).reduce<Record<string, Record<string, number>>>((out, x) => { const row = out[x.sector] ??= {}; row[x.sentiment] = (row[x.sentiment] ?? 0) + 1; return out; }, {});
  return { recent, counts, sectors };
};

export default function NewsTicker() {
  const [articles, setArticles] = useState<NewsArticle[]>([]);
  const [health, setHealth] = useState<Health | null>(null);
  const [fetchError, setFetchError] = useState(false);
  useVisiblePolling(async () => {
    try {
      const [rowsResponse, healthResponse] = await Promise.all([fetch("/api/news?limit=200"), fetch("/api/health")]);
      if (!rowsResponse.ok || !healthResponse.ok) throw new Error("news feed unavailable");
      const [rows, status] = await Promise.all([rowsResponse.json(), healthResponse.json()]);
      setArticles(normalizeArticlesResponse(rows).articles);
      setHealth(status.news ?? null);
      setFetchError(false);
    } catch { setFetchError(true); }
  }, 60000);
  const { recent } = aggregateTickerArticles(articles);
  const status = fetchError ? "피드 장애" : !health ? "대기" : health.enabled === false ? "비활성" : health.lastError ? "피드 장애" : !health.lastSuccessAt ? "대기" : recent.length === 0 ? "데이터 없음" : "정상";
  const text = (a: NewsArticle) => `${a.titleKo ?? a.title} · ${a.sourceKo ?? a.source ?? "출처 미상"} · 영향도 ${Object.values(a.impactScores ?? {})[0] ?? "-"}`;
  return <section className={`news-ticker ${status === "정상" ? "ok" : "notice"}`} aria-label="전체 뉴스 감성 티커">{recent.length > 0 && <div className="news-ticker-viewport" tabIndex={0}><div className="news-ticker-track" aria-live="polite">{recent.map((a, i) => <span key={`${a.id}-${i}`} className={`news-ticker-item ${a.sentiment}`}><b>{sentimentLabel(a.sentiment)}</b> {text(a)}</span>)}{recent.map((a, i) => <span key={`repeat-${a.id}-${i}`} className={`news-ticker-item ${a.sentiment}`} aria-hidden="true"><b>{sentimentLabel(a.sentiment)}</b> {text(a)}</span>)}</div></div>}</section>;
}
