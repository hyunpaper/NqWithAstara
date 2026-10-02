import { useCallback, useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type { NewsArticle, NewsEvidenceItem, NewsSymbolScore } from "./newsTypes";
import { absoluteTimeKst, sentimentBadge } from "./newsFormat";

export default function NewsEvidenceTooltip({ article, id }: { article: NewsArticle; id?: string }) {
  const badge = sentimentBadge(article.sentiment);
  const impacts = Object.entries(article.impactScores ?? {});
  return <span id={id} className="news-evidence-tooltip" role="tooltip" aria-label="뉴스 감성 판정 근거">
    {badge && <strong className={badge.className}>{badge.label}</strong>}
    {article.reason && <span>판정 근거: {article.reason}</span>}
    {article.classificationText && <span>분석 내용: {article.classificationText}</span>}
    {article.evidenceSource && <span>근거 원천: {article.evidenceSource}</span>}
    {article.inputKind && <span>분석 입력: {article.inputKind === "body" ? "본문" : article.inputKind === "summary" ? "요약" : "헤드라인"}</span>}
    {impacts.map(([symbol, score]) => <span key={symbol}>{symbol} 영향도 {score >= 0 ? "+" : ""}{score.toFixed(1)}</span>)}
    {article.evidenceArticleId && <span>동일 기사 분석 근거로 연결됨</span>}
    {article.evidence?.slice(0, 3).map((item) => <span key={item.id}>근거 기사: {item.title} ({item.source ?? "출처 미상"})</span>)}
    {article.createdAt && <span>{absoluteTimeKst(article.createdAt)}</span>}
  </span>;
}

export function NewsScorePopover({ score, label, className, onSelectEvidence }: { score: NewsSymbolScore; label: string; className: string; onSelectEvidence: (evidence: NewsEvidenceItem, trigger: HTMLElement) => void }) {
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState({ left: 16, top: 16, above: false, width: 320 });
  const id = useId();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const restoringFocus = useRef(false);
  const pointerFocus = useRef(false);
  const closeTimer = useRef<number | null>(null);
  const cancelClose = useCallback(() => {
    if (closeTimer.current !== null) window.clearTimeout(closeTimer.current);
    closeTimer.current = null;
  }, []);
  const scheduleClose = useCallback(() => {
    cancelClose();
    closeTimer.current = window.setTimeout(() => setOpen(false), 120);
  }, [cancelClose]);
  const updatePosition = useCallback(() => {
    const trigger = triggerRef.current;
    if (!trigger) return;
    const rect = trigger.getBoundingClientRect();
    const width = Math.min(320, Math.max(180, window.innerWidth - 32));
    const left = Math.min(Math.max(16, rect.left), Math.max(16, window.innerWidth - width - 16));
    const roomBelow = window.innerHeight - rect.bottom;
    const above = roomBelow < 220 && rect.top > roomBelow;
    setPosition({ left, top: above ? rect.top - 6 : rect.bottom + 6, above, width });
  }, []);
  const show = useCallback(() => {
    cancelClose();
    updatePosition();
    setOpen(true);
  }, [cancelClose, updatePosition]);
  useEffect(() => {
    if (!open) return;
    const close = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      setOpen(false);
      restoringFocus.current = true;
      triggerRef.current?.focus();
      queueMicrotask(() => { restoringFocus.current = false; });
    };
    const dismiss = (event: PointerEvent) => {
      const target = event.target instanceof Node ? event.target : null;
      if (triggerRef.current?.contains(target) || popoverRef.current?.contains(target)) return;
      setOpen(false);
    };
    const reposition = () => updatePosition();
    document.addEventListener("keydown", close);
    document.addEventListener("pointerdown", dismiss);
    window.addEventListener("resize", reposition);
    window.addEventListener("scroll", reposition, true);
    return () => {
      document.removeEventListener("keydown", close);
      document.removeEventListener("pointerdown", dismiss);
      window.removeEventListener("resize", reposition);
      window.removeEventListener("scroll", reposition, true);
    };
  }, [open, updatePosition]);
  useEffect(() => () => cancelClose(), [cancelClose]);
  const keepFocusOpen = (next: EventTarget | null) => {
    const node = next instanceof Node ? next : null;
    if (!triggerRef.current?.contains(node) && !popoverRef.current?.contains(node)) setOpen(false);
  };
  const popover = open ? <div
    ref={popoverRef}
    id={id}
    className={`news-score-evidence${position.above ? " above" : ""}`}
    role="dialog"
    aria-label={`${score.symbol} 뉴스 점수 근거`}
    style={{ left: position.left, top: position.top, width: position.width }}
    onMouseEnter={cancelClose}
    onMouseLeave={scheduleClose}
    onBlur={(event) => keepFocusOpen(event.relatedTarget)}
  >
    <strong>{score.symbol} 점수 {score.score >= 0 ? "+" : ""}{score.score.toFixed(1)}</strong>
    {(score.evidence ?? []).length === 0
      ? <span>표시할 동일 시점 근거 기사가 없습니다.</span>
      : score.evidence!.map((item) => <button key={item.id} type="button" onClick={(event) => { event.stopPropagation(); setOpen(false); onSelectEvidence(item, triggerRef.current ?? event.currentTarget); }}>
        <span>{item.title}</span>
        <small>{item.contribution == null ? "기여도 없음" : `점수 기여 ${item.contribution >= 0 ? "+" : ""}${item.contribution.toFixed(2)}`}</small>
      </button>)}
    {(score.remainingEvidenceCount ?? 0) > 0 && <span>외 {score.remainingEvidenceCount}건 · 나머지 기여 {(score.remainingContribution ?? 0) >= 0 ? "+" : ""}{(score.remainingContribution ?? 0).toFixed(2)}</span>}
  </div> : null;
  return <span className="news-score-popover" onMouseEnter={show} onMouseLeave={scheduleClose} onBlur={(event) => keepFocusOpen(event.relatedTarget)}>
    <button
      ref={triggerRef}
      type="button"
      className={className}
      aria-expanded={open}
      aria-controls={id}
      aria-haspopup="dialog"
      onFocus={() => { if (!restoringFocus.current && !pointerFocus.current) show(); }}
      onPointerDown={(event) => { pointerFocus.current = true; event.stopPropagation(); }}
      onPointerCancel={() => { pointerFocus.current = false; }}
      onClick={(event) => { pointerFocus.current = false; event.stopPropagation(); cancelClose(); updatePosition(); setOpen((value) => !value); }}
    >{label}</button>
    {popover && createPortal(popover, document.body)}
  </span>;
}
