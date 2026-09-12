import { useEffect, useState } from "react";
import { Newspaper } from "lucide-react";
import { normalizeArticlesResponse, type NewsArticle } from "./newsTypes";
import { absoluteTimeKst, inputKindLabel, relativeTimeKo, sentimentBadge } from "./newsFormat";

const NEWS_URL = "https://www.saveticker.com/news";

export default function NewsPanel({ symbol }: { symbol: string }) {
  const [articles, setArticles] = useState<NewsArticle[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    setArticles([]);
    setLoaded(false);
    let active = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;

    const schedule = () => {
      if (timer) clearTimeout(timer);
      if (active && document.visibilityState === "visible") timer = setTimeout(run, 15000);
    };
    const run = async () => {
      if (!active || inFlight || document.visibilityState !== "visible") return;
      inFlight = true;
      request = new AbortController();
      try {
        const response = await fetch(
          `/api/news?symbol=${encodeURIComponent(symbol)}&limit=10`,
          { signal: request.signal },
        );
        if (!response.ok) throw new Error(`뉴스 조회 실패 (${response.status})`);
        const next = normalizeArticlesResponse(await response.json());
        if (active) {
          setArticles(next.articles);
          setLoaded(true);
        }
      } catch (e) {
        if (active && (e as Error).name !== "AbortError") setLoaded(true);
        /* 실패는 조용히 무시하고 이전 값을 유지한다 */
      } finally {
        inFlight = false;
        request = undefined;
        schedule();
      }
    };
    const visibility = () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
      if (document.visibilityState === "visible" && !inFlight) void run();
    };
    void run();
    document.addEventListener("visibilitychange", visibility);
    return () => {
      active = false;
      if (timer) clearTimeout(timer);
      request?.abort();
      document.removeEventListener("visibilitychange", visibility);
    };
  }, [symbol]);

  return (
    <section className="panel news-panel">
      <div className="panel-head">
        <div>
          <h2>{symbol} 뉴스</h2>
          <p>최근 10건 · 감성 판정은 참고용입니다</p>
        </div>
        <Newspaper size={18} />
      </div>
      {!loaded ? (
        <div className="news-state">뉴스를 불러오는 중…</div>
      ) : articles.length === 0 ? (
        <div className="news-state">최근 관련 기사 없음</div>
      ) : (
        <ul className="news-list">
          {articles.map((a) => {
            const badge = sentimentBadge(a.sentiment);
            const kind = inputKindLabel(a.inputKind);
            return (
              <li key={a.id}>
                <a href={NEWS_URL} target="_blank" rel="noreferrer">
                  <div className="news-row-head">
                    <span
                      className="news-time"
                      title={absoluteTimeKst(a.createdAt)}
                    >
                      {relativeTimeKo(a.createdAt)}
                    </span>
                    {a.source && <span className="news-source">{a.source}</span>}
                    {badge && <span className={badge.className}>{badge.label}</span>}
                  </div>
                  <div className="news-title">{a.title}</div>
                  {a.reason && <div className="news-reason">{a.reason}</div>}
                  {kind && <span className="news-kind">{kind}</span>}
                </a>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
