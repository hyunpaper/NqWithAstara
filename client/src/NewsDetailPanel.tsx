import { useEffect, useRef } from "react";
import type { NewsArticle } from "./newsTypes";
import { absoluteTimeKst, inputKindLabel, sentimentBadge } from "./newsFormat";
import type { NewsDetailState } from "./useNewsDetail";

export default function NewsDetailPanel({ article, state = "idle", onClose }: { article: NewsArticle; state?: NewsDetailState; onClose: () => void }) {
  const closeRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    closeRef.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === "Escape") onClose(); };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [article.id, onClose]);
  const badge = sentimentBadge(article.sentiment);
  const body = article.body ?? article.summary;
  const originalUrl = (() => { try { const u = new URL(article.url ?? ""); return u.protocol === "https:" || u.protocol === "http:" ? u.toString() : null; } catch { return null; } })();
  const showStoredDetail = state === "idle" || state === "error";
  return <aside className="news-detail-panel" role="dialog" aria-modal="false" aria-label="뉴스 상세">
    <div className="news-detail-head"><div><span className="news-kicker">뉴스 상세</span><h3>{article.title}</h3></div><button ref={closeRef} type="button" onClick={onClose} aria-label="뉴스 상세 닫기">×</button></div>
    <div className="news-row-head">{badge && <span className={badge.className}>{badge.label}</span>} {article.source ?? "출처 미상"} {article.createdAt && <time>{absoluteTimeKst(article.createdAt)}</time>}</div>
    {originalUrl && <div className="news-detail-actions"><a href={originalUrl} target="_blank" rel="noreferrer">원문 열기</a></div>}
    {state === "loading" && <p className="news-state">기사를 불러오는 중…</p>}
    {state === "error" && <p className="news-state news-error" role="alert">기사 상세를 불러오지 못했습니다. 목록 정보만 표시합니다.</p>}
    {state === "not-found" && <p className="news-state news-error" role="alert">저장된 기사 상세를 찾을 수 없습니다.</p>}
    {showStoredDetail && <><div className="news-detail-body">{body ?? "기사 본문이 제공되지 않았습니다."}</div>
      {article.classificationText && <p className="news-reason">판정 근거: {article.classificationText}</p>}
      {article.evidence && article.evidence.length > 0 && <section className="news-detail-evidence"><h4>동일 시점 감성 근거</h4><ul>{article.evidence.map((item) => <li key={item.id}>{item.title} · {item.contribution == null ? "점수 기여 없음" : `점수 기여 ${item.contribution >= 0 ? "+" : ""}${item.contribution.toFixed(2)}`}</li>)}</ul></section>}
      <dl className="news-detail-meta"><div><dt>분석 입력</dt><dd>{inputKindLabel(article.inputKind) ?? "알 수 없음"}</dd></div><div><dt>근거 원천</dt><dd>{article.evidenceSource ?? article.classificationSource ?? "알 수 없음"}</dd></div>{article.collectedAt && <div><dt>수집 시각</dt><dd>{absoluteTimeKst(article.collectedAt)}</dd></div>}{article.classifiedAt && <div><dt>분석 시각</dt><dd>{absoluteTimeKst(article.classifiedAt)}</dd></div>}</dl></>}
  </aside>;
}
