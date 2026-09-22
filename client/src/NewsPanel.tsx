import { useEffect, useState } from "react";
import type { MouseEvent } from "react";
import { Newspaper } from "lucide-react";
import { normalizeArticlesResponse, type NewsArticle } from "./newsTypes";
import { absoluteTimeKst, inputKindLabel, relativeTimeKo, sentimentBadge } from "./newsFormat";
import NewsEvidenceTooltip from "./NewsEvidenceTooltip";

export default function NewsPanel({ symbol, onSelectArticle }: { symbol: string; onSelectArticle: (article: NewsArticle, trigger: HTMLElement) => void }) {
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
            const evidenceId = badge ? `news-evidence-${encodeURIComponent(a.id)}` : undefined;
            return (
              <li key={a.id}>
                  <button type="button" className="news-row-button" onClick={(event: MouseEvent<HTMLButtonElement>) => onSelectArticle(a, event.currentTarget)} aria-label={`${a.titleKo ?? a.title} 상세 보기`} aria-describedby={evidenceId}>
                  <div className="news-row-head">
                    <span
                      className="news-time"
                      title={absoluteTimeKst(a.createdAt)}
                    >
                      {relativeTimeKo(a.createdAt)}
                    </span>
                    {(a.sourceKo ?? a.source) && <span className="news-source">{a.sourceKo ?? a.source}</span>}
                    {badge && <span className="news-badge-wrap"><span className={badge.className}>{badge.label}</span><NewsEvidenceTooltip id={evidenceId} article={a} /></span>}
                  </div>
                  <div className="news-title">{a.titleKo ?? a.title}</div>
                  {a.reason && <div className="news-reason">{a.reason}</div>}
                  {kind && <span className="news-kind">{kind}</span>}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
