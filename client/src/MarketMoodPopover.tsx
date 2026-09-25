import { useCallback, useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { absoluteTimeKst } from "./newsFormat";
import { marketMoodBadge, marketMoodDirectionLabel, type MarketMoodResponse } from "./marketMoodTypes";

export default function MarketMoodPopover({ mood }: { mood: MarketMoodResponse }) {
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState({ left: 16, top: 16, above: false, width: 360 });
  const id = useId();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const restoringFocus = useRef(false);
  const pointerFocus = useRef(false);
  const closeTimer = useRef<number | null>(null);
  const badge = marketMoodBadge(mood);
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
    const width = Math.min(360, Math.max(240, window.innerWidth - 32));
    const left = Math.min(Math.max(16, rect.left), Math.max(16, window.innerWidth - width - 16));
    const roomBelow = window.innerHeight - rect.bottom;
    const above = roomBelow < 310 && rect.top > roomBelow;
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
    className={`market-mood-evidence${position.above ? " above" : ""}`}
    role="dialog"
    aria-label="시장 분위기 근거"
    style={{ left: position.left, top: position.top, width: position.width }}
    onMouseEnter={cancelClose}
    onMouseLeave={scheduleClose}
    onBlur={(event) => keepFocusOpen(event.relatedTarget)}
  >
    <strong>동일 가중치 평균 {mood.score == null ? "계산 불가" : `${mood.score >= 0 ? "+" : ""}${mood.score.toFixed(2)}%`}</strong>
    <span className="market-mood-summary">집계 {mood.availableCount}/{mood.totalCount} · {absoluteTimeKst(mood.asOf)}</span>
    <ul>
      {mood.assets.map((asset) => <li key={asset.key} className={asset.isAvailable ? "" : "excluded"}>
        <span><b>{asset.label}</b> {asset.symbol}{asset.proxy ? " ETF 프록시" : ""}</span>
        <span>{marketMoodDirectionLabel(asset)}{asset.changePercent == null ? "" : ` ${asset.changePercent >= 0 ? "+" : ""}${asset.changePercent.toFixed(2)}%`}</span>
        <small>{asset.reason ?? `${absoluteTimeKst(asset.asOf)} · ${asset.source}`}</small>
      </li>)}
    </ul>
    <strong>오늘의 미국 경제 일정</strong>
    {mood.economicCalendar.events.length === 0
      ? <span className="market-mood-limit">{mood.economicCalendar.reason ?? "등록된 일정이 없습니다."}</span>
      : <ul>{mood.economicCalendar.events.map((event) => <li key={event.id} className={event.status === "published" ? "" : "excluded"}>
        <span><b>{event.title}</b> · {absoluteTimeKst(event.scheduledAt)}</span>
        <span>{event.status === "published" ? `발표 ${event.actual ?? "값 없음"}${event.unit ?? ""}` : event.status === "unpublished" ? "미발표" : event.status === "delayed" ? "지연" : "미지원"}</span>
        <small>예상 {event.forecast ?? "미제공"} · 이전 {event.previous ?? "미제공"} · 영향 {event.impactDirection} · {event.source}{event.reason ? ` · ${event.reason}` : ""}</small>
      </li>)}</ul>}
    {mood.limitations.map((limitation) => <small className="market-mood-limit" key={limitation}>{limitation}</small>)}
  </div> : null;

  return <span className="news-score-popover" onMouseEnter={show} onMouseLeave={scheduleClose} onBlur={(event) => keepFocusOpen(event.relatedTarget)}>
    <button
      ref={triggerRef}
      type="button"
      className={badge.className}
      aria-expanded={open}
      aria-controls={id}
      aria-haspopup="dialog"
      onFocus={() => { if (!restoringFocus.current && !pointerFocus.current) show(); }}
      onPointerDown={(event) => { pointerFocus.current = true; event.stopPropagation(); }}
      onPointerCancel={() => { pointerFocus.current = false; }}
      onClick={(event) => { pointerFocus.current = false; event.stopPropagation(); cancelClose(); updatePosition(); setOpen((value) => !value); }}
    >{badge.label}</button>
    {popover && createPortal(popover, document.body)}
  </span>;
}
