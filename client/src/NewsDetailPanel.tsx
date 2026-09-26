import { useEffect, useRef, useState } from "react";
import type { NewsArticle } from "./newsTypes";
import { absoluteTimeKst, inputKindLabel, sentimentBadge } from "./newsFormat";
import type { NewsDetailState, NewsTranslationRequestState } from "./useNewsDetail";

const translationStateText = (status: string | null | undefined, hasKorean: boolean) => {
  if (hasKorean) return null;
  if (status === "pending") return "한국어 번역 대기 중입니다.";
  if (status === "quota_wait") return "번역 할당량이 복구되기를 기다리고 있습니다.";
  if (status === "failed") return "한국어 번역에 실패했습니다.";
  if (status === "not_configured") return "번역 서비스가 설정되지 않았습니다.";
  if (status === "not_available") return "번역할 기사 본문이 제공되지 않았습니다.";
  if (status === "not_requested") return "한국어 번역이 아직 요청되지 않았습니다.";
  if (status === "translated" || status === "partial") return "한국어 본문이 제공되지 않았습니다.";
  return "한국어 번역이 없습니다.";
};

const translationStatusLabel = (status: string | null | undefined) => {
  if (status === "translated") return "기사 발췌 번역";
  if (status === "partial") return "일부 발췌 번역";
  if (status === "pending") return "번역 대기";
  if (status === "quota_wait") return "번역 할당량 대기";
  if (status === "failed") return "번역 실패";
  if (status === "not_configured") return "번역 설정 없음";
  if (status === "not_available") return "본문 미제공";
  if (status === "not_needed") return "번역 불필요";
  if (status === "not_requested") return "번역 미요청";
  return "원문만 제공";
};

export default function NewsDetailPanel({ article, state = "idle", translationState = "idle", onRetryTranslation, onClose }: { article: NewsArticle; state?: NewsDetailState; translationState?: NewsTranslationRequestState; onRetryTranslation?: () => void; onClose: () => void }) {
  const [translated, setTranslated] = useState(true);
  const closeRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    setTranslated(true);
    closeRef.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === "Escape") onClose(); };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [article.id, onClose]);
  const badge = sentimentBadge(article.sentiment);
  const korean = article.contentKo ?? article.summaryKo;
  const original = article.body ?? article.summary;
  const contentStatus = article.contentKo != null || article.body != null
    ? article.contentTranslationStatus ?? article.translationStatus
    : article.summaryKo != null || article.summary != null
      ? article.summaryTranslationStatus ?? article.translationStatus
      : article.contentTranslationStatus ?? article.summaryTranslationStatus ?? article.translationStatus;
  const koreanState = translationStateText(contentStatus, korean != null);
  const classificationText = translated ? article.classificationTextKo : article.classificationText;
  const originalUrl = (() => { try { const u = new URL(article.url ?? ""); return u.protocol === "https:" || u.protocol === "http:" ? u.toString() : null; } catch { return null; } })();
  const showStoredDetail = state === "idle" || state === "error";
  return <aside className="news-detail-panel" role="dialog" aria-modal="false" aria-label="뉴스 상세">
    <div className="news-detail-head"><div><span className="news-kicker">뉴스 상세</span><h3>{translated ? article.titleKo ?? article.title : article.title}</h3></div><button ref={closeRef} type="button" onClick={onClose} aria-label="뉴스 상세 닫기">×</button></div>
    <div className="news-row-head">{badge && <span className={badge.className}>{badge.label}</span>} {article.sourceKo ?? article.source ?? "출처 미상"} {article.createdAt && <time>{absoluteTimeKst(article.createdAt)}</time>}</div>
    <div className="news-detail-actions"><button type="button" aria-pressed={translated} onClick={() => setTranslated(true)}>한국어</button><button type="button" aria-pressed={!translated} onClick={() => setTranslated(false)}>원문</button>{originalUrl && <a href={originalUrl} target="_blank" rel="noreferrer">원문 열기</a>}</div>
    {state === "loading" && <p className="news-state">기사를 불러오는 중…</p>}
    {state === "error" && <p className="news-state news-error" role="alert">기사 상세를 불러오지 못했습니다. 목록 정보만 표시합니다.</p>}
    {state === "not-found" && <p className="news-state news-error" role="alert">저장된 기사 상세를 찾을 수 없습니다.</p>}
    {showStoredDetail && <><div className="news-detail-body">{translated ? (korean ?? <><p className="news-translation-state">{koreanState}</p>{original && <><p className="news-original-label">원문</p><p>{original}</p></>}</>) : (original ?? "원문 본문이 제공되지 않았습니다.")}</div>
      {translated && !korean && onRetryTranslation && <button className="news-translation-retry" type="button" onClick={onRetryTranslation} disabled={translationState === "requesting" || translationState === "queued"}>{translationState === "requesting" ? "번역 요청 중…" : translationState === "queued" ? "번역 처리 대기 중…" : translationState === "error" ? "번역 다시 요청" : "번역 요청"}</button>}
      {classificationText && <p className="news-reason">판정 근거: {classificationText}</p>}
      {article.evidence && article.evidence.length > 0 && <section className="news-detail-evidence"><h4>동일 시점 감성 근거</h4><ul>{article.evidence.map((item) => <li key={item.id}>{item.titleKo ?? item.title} · {item.contribution == null ? "점수 기여 없음" : `점수 기여 ${item.contribution >= 0 ? "+" : ""}${item.contribution.toFixed(2)}`}</li>)}</ul></section>}
      <dl className="news-detail-meta"><div><dt>분석 입력</dt><dd>{inputKindLabel(article.inputKind) ?? "알 수 없음"}</dd></div><div><dt>근거 원천</dt><dd>{article.evidenceSource ?? article.classificationSource ?? "알 수 없음"}</dd></div><div><dt>번역</dt><dd>{translationStatusLabel(contentStatus)}</dd></div>{article.collectedAt && <div><dt>수집 시각</dt><dd>{absoluteTimeKst(article.collectedAt)}</dd></div>}{article.classifiedAt && <div><dt>분석 시각</dt><dd>{absoluteTimeKst(article.classifiedAt)}</dd></div>}</dl></>}
  </aside>;
}
