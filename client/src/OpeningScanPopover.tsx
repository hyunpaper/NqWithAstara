import { useCallback, useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import {
  openingScanTriggerLabel,
  type OpeningScanResponse,
  type OpeningScanRow,
  type OpeningGrade,
} from "./openingScanTypes";

const gradeLabel = (grade: OpeningGrade): string =>
  ({ STRONG: "강세", VOLUME_ONLY: "거래량", PRICE_ONLY: "상승", WEAK: "약함" })[grade];

const pct = (value: number | null): string => value == null ? "—" : `${value >= 0 ? "+" : ""}${value.toFixed(1)}%`;

const rvolCell = (row: OpeningScanRow, lookback: number): string => {
  if (row.volumeStatus === "insufficient" || row.rvolNow == null) return `기준 없음 (${row.sampleCount}/${lookback})`;
  if (row.volumeStatus === "partial") return `${row.rvolNow.toFixed(1)}× (${row.sampleCount}세션)`;
  return `${row.rvolNow.toFixed(1)}×`;
};

const first5Cell = (row: OpeningScanRow): string => {
  if (!row.first5) return "—";
  return row.first5.barsSeen >= 5 ? `양봉 ${row.first5.upBars}·신고 ${row.first5.newHighs}` : `양봉 ${row.first5.upBars}`;
};

function ScanTable({ rows, lookback }: { rows: OpeningScanRow[]; lookback: number }) {
  return (
    <table className="opening-scan-table">
      <thead>
        <tr>
          <th>종목</th><th>등급</th><th>RVOL(표본)</th><th>시가대비</th><th>전일대비</th><th>VWAP</th><th>첫5봉</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.symbol} title={row.reasons.join("\n")} className={`opening-grade-${row.grade.toLowerCase()}`}>
            <td><b>{row.symbol}</b></td>
            <td>{gradeLabel(row.grade)}</td>
            <td>{rvolCell(row, lookback)}</td>
            <td>{pct(row.changeFromOpenPercent)}</td>
            <td>{pct(row.changeFromPrevClosePercent)}</td>
            <td>{row.aboveVwap == null ? "—" : row.aboveVwap ? "위" : "아래"}</td>
            <td>{first5Cell(row)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export default function OpeningScanPopover({ scan }: { scan: OpeningScanResponse }) {
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState({ left: 16, top: 16, above: false, width: 460 });
  const id = useId();
  const triggerRef = useRef<HTMLButtonElement>(null);
  const popoverRef = useRef<HTMLDivElement>(null);
  const restoringFocus = useRef(false);
  const pointerFocus = useRef(false);
  const closeTimer = useRef<number | null>(null);
  const trigger = openingScanTriggerLabel(scan);

  const cancelClose = useCallback(() => {
    if (closeTimer.current !== null) window.clearTimeout(closeTimer.current);
    closeTimer.current = null;
  }, []);
  const scheduleClose = useCallback(() => {
    cancelClose();
    closeTimer.current = window.setTimeout(() => setOpen(false), 120);
  }, [cancelClose]);
  const updatePosition = useCallback(() => {
    const el = triggerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const width = Math.min(460, Math.max(280, window.innerWidth - 32));
    const left = Math.min(Math.max(16, rect.left), Math.max(16, window.innerWidth - width - 16));
    const roomBelow = window.innerHeight - rect.bottom;
    const above = roomBelow < 340 && rect.top > roomBelow;
    setPosition({ left, top: above ? rect.top - 6 : rect.bottom + 6, above, width });
  }, []);
  const show = useCallback(() => { cancelClose(); updatePosition(); setOpen(true); }, [cancelClose, updatePosition]);

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

  if (!trigger) return null;

  const keepFocusOpen = (next: EventTarget | null) => {
    const node = next instanceof Node ? next : null;
    if (!triggerRef.current?.contains(node) && !popoverRef.current?.contains(node)) setOpen(false);
  };

  const strong = scan.rows.filter((r) => r.grade !== "WEAK");
  const weak = scan.rows.filter((r) => r.grade === "WEAK");
  const summary = scan.summary;

  const popover = open ? (
    <div
      ref={popoverRef}
      id={id}
      className={`opening-scan-popover${position.above ? " above" : ""}`}
      role="dialog"
      aria-label="개장 초반 스캔"
      style={{ left: position.left, top: position.top, width: position.width }}
      onMouseEnter={cancelClose}
      onMouseLeave={scheduleClose}
      onBlur={(event) => keepFocusOpen(event.relatedTarget)}
    >
      {scan.phase === "scanning" && (
        <>
          <strong>개장 09:30~10:00 · {scan.elapsedMinutes}분 경과</strong>
          {strong.length > 0 ? <ScanTable rows={strong} lookback={scan.lookbackSessions} />
            : <span className="opening-scan-empty">아직 강세 종목이 없습니다.</span>}
          {weak.length > 0 && (
            <details className="opening-scan-weak">
              <summary>약함 {weak.length}종목</summary>
              <ScanTable rows={weak} lookback={scan.lookbackSessions} />
            </details>
          )}
        </>
      )}
      {scan.phase === "summary" && (
        summary && (summary.at5.length > 0 || summary.at30.length > 0)
          ? <>
              <strong>09:30~10:00 기록</strong>
              <span className="opening-scan-sub">5분 시점 상위</span>
              <ScanTable rows={summary.at5} lookback={scan.lookbackSessions} />
              {summary.at30.length > 0 && <>
                <span className="opening-scan-sub">30분 시점 상위</span>
                <ScanTable rows={summary.at30} lookback={scan.lookbackSessions} />
              </>}
              {summary.followup30.length > 0 && <>
                <span className="opening-scan-sub">+30분 수익률</span>
                <table className="opening-scan-table"><thead><tr><th>종목</th><th>등급</th><th>+30분</th><th>최대</th><th>최소</th></tr></thead>
                  <tbody>{summary.followup30.map((f) => <tr key={f.symbol}><td><b>{f.symbol}</b></td><td>{gradeLabel(f.gradeAt5)}</td><td>{pct(f.returnPercent30)}</td><td>{pct(f.mfePercent30)}</td><td>{pct(f.maePercent30)}</td></tr>)}</tbody>
                </table>
              </>}
              {summary.close.length > 0 && <>
                <span className="opening-scan-sub">마감 수익률</span>
                <table className="opening-scan-table"><thead><tr><th>종목</th><th>등급</th><th>마감</th></tr></thead>
                  <tbody>{summary.close.map((c) => <tr key={c.symbol}><td><b>{c.symbol}</b></td><td>{gradeLabel(c.gradeAt5)}</td><td>{pct(c.returnPercentClose)}</td></tr>)}</tbody>
                </table>
              </>}
            </>
          : <span className="opening-scan-empty">이 세션 기록 없음(서버 미가동 또는 관심종목 없음)</span>
      )}
      {scan.phase === "pending" && <span className="opening-scan-empty">개장 봉 대기</span>}
      <small className="opening-scan-note">표시·기록 전용 · 진입 판정에 쓰지 않음 · 정책 {scan.policyVersion}</small>
    </div>
  ) : null;

  return (
    <span className="opening-scan-trigger" onMouseEnter={show} onMouseLeave={scheduleClose} onBlur={(event) => keepFocusOpen(event.relatedTarget)}>
      <button
        ref={triggerRef}
        type="button"
        className="news-badge neutral"
        aria-expanded={open}
        aria-controls={id}
        aria-haspopup="dialog"
        onFocus={() => { if (!restoringFocus.current && !pointerFocus.current) show(); }}
        onPointerDown={(event) => { pointerFocus.current = true; event.stopPropagation(); }}
        onPointerCancel={() => { pointerFocus.current = false; }}
        onClick={(event) => { pointerFocus.current = false; event.stopPropagation(); cancelClose(); updatePosition(); setOpen((v) => !v); }}
      >{trigger.label}{trigger.detail ? ` · ${trigger.detail}` : ""}</button>
      {popover && createPortal(popover, document.body)}
    </span>
  );
}
