import { useCallback, useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type { ConfluenceContributor, ConfluenceSummary } from "./confluenceTypes";
import { describeContributor, gaugeTone, scoreText2 } from "./confluenceFormat";

type Props = {
  symbol: string;
  score: number | null;
  summary: ConfluenceSummary | null | undefined;
};

function ContributorRow({ contributor }: { contributor: ConfluenceContributor }) {
  const display = describeContributor(contributor);
  return (
    <div className="confluence-tip-row">
      <span className="confluence-tip-name">{display.label}</span>
      <span className="confluence-tip-detail">{display.detail}</span>
      <b className={`confluence-tip-score ${gaugeTone(contributor.contribution)}`}>{display.contribution}</b>
    </div>
  );
}

/** 이슈 #379: 사이드바 컨플루언스 배지 — 호버·키보드 포커스 시 +/− 원인 툴팁을 띄운다. */
export function ConfluenceBadgePopover({ symbol, score, summary }: Props) {
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
    const width = Math.min(320, Math.max(220, window.innerWidth - 32));
    const left = Math.min(Math.max(16, rect.left), Math.max(16, window.innerWidth - width - 16));
    const roomBelow = window.innerHeight - rect.bottom;
    const above = roomBelow < 240 && rect.top > roomBelow;
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
  const top = summary?.top ?? [];
  const bottom = summary?.bottom ?? [];
  const empty = top.length === 0 && bottom.length === 0;
  const popover = open ? (
    <div
      ref={popoverRef}
      id={id}
      className={`confluence-tip${position.above ? " above" : ""}`}
      role="dialog"
      aria-label={`${symbol} 컨플루언스 점수 근거`}
      style={{ left: position.left, top: position.top, width: position.width }}
      onMouseEnter={cancelClose}
      onMouseLeave={scheduleClose}
      onBlur={(event) => keepFocusOpen(event.relatedTarget)}
    >
      <strong className={gaugeTone(score)}>
        {symbol} 컨플루언스 {scoreText2(score)}
      </strong>
      <span className="confluence-tip-note">관측 전용 · 진입 판단에 쓰지 않음</span>
      {empty ? (
        <span className="confluence-tip-note">기여 요소 데이터 없음</span>
      ) : (
        <>
          {top.length > 0 && (
            <div className="confluence-tip-group">
              <span className="confluence-tip-head">상승 기여</span>
              {top.map((c) => (
                <ContributorRow key={`top-${c.name}`} contributor={c} />
              ))}
            </div>
          )}
          {bottom.length > 0 && (
            <div className="confluence-tip-group">
              <span className="confluence-tip-head">하락 요인</span>
              {bottom.map((c) => (
                <ContributorRow key={`bottom-${c.name}`} contributor={c} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  ) : null;
  return (
    <span
      className="confluence-badge-popover"
      onMouseEnter={show}
      onMouseLeave={scheduleClose}
      onBlur={(event) => keepFocusOpen(event.relatedTarget)}
    >
      <button
        ref={triggerRef}
        type="button"
        className={`confluence-mini-badge ${gaugeTone(score)}`}
        aria-expanded={open}
        aria-controls={id}
        aria-haspopup="dialog"
        aria-label={`${symbol} 컨플루언스 점수 ${scoreText2(score)}, 원인 보기`}
        onFocus={() => { if (!restoringFocus.current && !pointerFocus.current) show(); }}
        onPointerDown={(event) => { pointerFocus.current = true; event.stopPropagation(); }}
        onPointerCancel={() => { pointerFocus.current = false; }}
        onClick={(event) => { pointerFocus.current = false; event.stopPropagation(); cancelClose(); updatePosition(); setOpen((value) => !value); }}
      >
        {scoreText2(score)}
      </button>
      {popover && createPortal(popover, document.body)}
    </span>
  );
}
