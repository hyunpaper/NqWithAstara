import { useEffect, useRef, useState } from "react";
import { normalizeArticle, type NewsArticle } from "./newsTypes";
import { absoluteTimeKst, inputKindLabel, sentimentBadge } from "./newsFormat";

export default function NewsDetailPanel({ article, state = "idle", onClose }: { article: NewsArticle; state?: "idle" | "loading" | "error"; onClose: () => void }) {
  const [translated, setTranslated] = useState(true);
  const [evidence, setEvidence] = useState<NewsArticle>(article);
  const [evidenceState, setEvidenceState] = useState<"idle" | "loading" | "error">("loading");
  const closeRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    closeRef.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => { if (event.key === "Escape") onClose(); };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [onClose]);
  useEffect(() => {
    setEvidence(article); setEvidenceState("loading");
    const controller = new AbortController();
    fetch(`/api/news/${encodeURIComponent(article.id)}/evidence`, { signal: controller.signal })
      .then(async (response) => { if (!response.ok) throw new Error("근거 조회 실패"); return response.json(); })
      .then((raw) => { if (!controller.signal.aborted) {
        const normalized = normalizeArticle({ ...raw, body: raw.body ?? raw.content });
        if (!normalized) throw new Error("상세 응답 형식이 올바르지 않습니다.");
        setEvidence({ ...article, ...normalized }); setEvidenceState("idle");
      } })
      .catch((error) => { if (!controller.signal.aborted) setEvidenceState("error"); });
    return () => controller.abort();
  }, [article]);
  article = evidence;
  const badge = sentimentBadge(article.sentiment);
  const korean = article.translationStatus === "translated"
    ? (article.contentKo ?? article.summaryKo ?? article.titleKo ?? "번역된 본문이 없습니다.")
    : (article.titleKo ?? "번역된 본문이 없습니다.");
  const original = article.body ?? article.summary ?? article.title;
  const originalUrl = (() => { try { const u = new URL(article.url ?? ""); return u.protocol === "https:" || u.protocol === "http:" ? u.toString() : null; } catch { return null; } })();
  return <aside className="news-detail-panel" role="dialog" aria-modal="false" aria-label="뉴스 상세">
    <div className="news-detail-head"><div><span className="news-kicker">뉴스 상세</span><h3>{translated ? article.titleKo ?? article.title : article.title}</h3></div><button ref={closeRef} type="button" onClick={onClose} aria-label="뉴스 상세 닫기">×</button></div>
    <div className="news-row-head">{badge && <span className={badge.className}>{badge.label}</span>} {article.sourceKo ?? article.source ?? "출처 미상"} {article.createdAt && <time>{absoluteTimeKst(article.createdAt)}</time>}</div>
    <div className="news-detail-actions"><button type="button" aria-pressed={translated} onClick={() => setTranslated(true)}>한국어</button><button type="button" aria-pressed={!translated} onClick={() => setTranslated(false)}>원문</button>{originalUrl && <a href={originalUrl} target="_blank" rel="noreferrer">원문 열기</a>}</div>
    {state === "loading" && <p className="news-state">기사를 불러오는 중…</p>}
    {state === "error" && <p className="news-state news-error">기사 상세를 불러오지 못했습니다. 목록에 저장된 내용만 표시합니다.</p>}
    <p className="news-detail-body">{translated ? korean : original}</p>
    {article.translationStatus !== "translated" && translated && <p className="news-state">한국어 번역이 없어 원문을 표시합니다.</p>}
    {evidenceState === "error" && <p className="news-state news-error">감성 근거를 불러오지 못했습니다.</p>}
    {article.evidence && article.evidence.length > 0 && <section className="news-detail-evidence"><h4>동일 시점 감성 근거</h4><ul>{article.evidence.map((item) => <li key={item.id}>{item.titleKo ?? item.title} · {item.contribution == null ? "영향도 없음" : `영향도 ${item.contribution >= 0 ? "+" : ""}${item.contribution.toFixed(1)}`}</li>)}</ul></section>}
    <dl className="news-detail-meta"><div><dt>분석 입력</dt><dd>{inputKindLabel(article.inputKind) ?? "알 수 없음"}</dd></div><div><dt>출처</dt><dd>{article.provenance ?? article.classificationSource ?? article.source ?? "알 수 없음"}</dd></div><div><dt>번역</dt><dd>{article.translationStatus === "translated" ? "본문 번역 완료" : article.translationStatus === "pending" ? "번역 대기" : "원문만 제공"}</dd></div></dl>
  </aside>;
}
