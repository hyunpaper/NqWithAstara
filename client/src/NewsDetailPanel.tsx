import { useState } from "react";
import type { NewsArticle } from "./newsTypes";
import { absoluteTimeKst, inputKindLabel, sentimentBadge } from "./newsFormat";

export default function NewsDetailPanel({ article, onClose }: { article: NewsArticle; onClose: () => void }) {
  const [translated, setTranslated] = useState(true);
  const badge = sentimentBadge(article.sentiment);
  const korean = article.body ?? article.summary ?? article.titleKo ?? article.title;
  const original = article.body ?? article.summary ?? article.title;
  return <aside className="news-detail-panel" aria-label="뉴스 상세">
    <div className="news-detail-head"><div><span className="news-kicker">뉴스 상세</span><h3>{translated ? article.titleKo ?? article.title : article.title}</h3></div><button type="button" onClick={onClose} aria-label="뉴스 상세 닫기">×</button></div>
    <div className="news-row-head">{badge && <span className={badge.className}>{badge.label}</span>} {article.sourceKo ?? article.source ?? "출처 미상"} {article.createdAt && <time>{absoluteTimeKst(article.createdAt)}</time>}</div>
    <div className="news-detail-actions"><button type="button" aria-pressed={translated} onClick={() => setTranslated(true)}>한국어</button><button type="button" aria-pressed={!translated} onClick={() => setTranslated(false)}>원문</button>{article.url && <a href={article.url} target="_blank" rel="noreferrer">원문 열기</a>}</div>
    <p className="news-detail-body">{translated ? korean : original}</p>
    <dl className="news-detail-meta"><div><dt>분석 입력</dt><dd>{inputKindLabel(article.inputKind) ?? "알 수 없음"}</dd></div><div><dt>출처</dt><dd>{article.provenance ?? article.source ?? "알 수 없음"}</dd></div><div><dt>번역</dt><dd>{article.translationStatus === "translated" ? "번역 완료" : article.translationStatus === "pending" ? "번역 대기" : "원문"}</dd></div></dl>
  </aside>;
}
