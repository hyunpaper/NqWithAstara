import { useState } from "react";
import { useVisiblePolling } from "./useVisiblePolling";
import { normalizeArticlesResponse, type NewsArticle } from "./newsTypes";

type Health = { enabled?: boolean; lastSuccessAt?: string | null; lastError?: string | null };
export const aggregateTickerArticles = (articles: NewsArticle[], now = Date.now()) => {
  const recent = articles.filter((a) => a.createdAt != null && Date.parse(a.createdAt) >= now - 24 * 60 * 60 * 1000).slice(0, 200);
  const counts = recent.reduce<Record<string, number>>((out, article) => { out[article.sentiment] = (out[article.sentiment] ?? 0) + 1; return out; }, {});
  return { recent, counts };
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
  const { recent, counts } = aggregateTickerArticles(articles);
  const status = !health ? "대기" : health.enabled === false ? "비활성" : fetchError || health.lastError ? "피드 장애" : !health.lastSuccessAt ? "대기" : recent.length === 0 ? "데이터 없음" : "정상";
  const summary = `AI 감성 · ${status} · 최근 24시간 ${recent.length}건 · 호재 ${counts.positive ?? 0} · 악재 ${counts.negative ?? 0} · 중립 ${counts.neutral ?? 0} · 미분류 ${counts.unclassified ?? 0}`;
  return <section className={`news-ticker ${status === "정상" ? "ok" : "notice"}`} aria-label="전체 뉴스 감성 티커"><div className="news-ticker-summary">{summary}</div>{recent.length > 0 && <div className="news-ticker-viewport" tabIndex={0}><div className="news-ticker-track" aria-live="polite">{[...recent, ...recent].map((a, i) => <span key={`${a.id}-${i}`} className={`news-ticker-item ${a.sentiment}`}><b>{a.sentiment}</b> {a.title}</span>)}</div></div>}</section>;
}
