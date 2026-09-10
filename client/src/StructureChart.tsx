import { useEffect, useMemo, useRef, useState } from "react";
import {
  StructureAnalysis,
  StructureCandidate,
  arr,
  clock,
  price,
  priceRange,
  zoneRoleGroup,
  zoneRoleLabel,
  sourceKindLabel,
} from "./structureTypes";

// v5 구조 엔진 D4 — 구조 분석 전용 차트(설계 §13).
//
// 그리는 것: 지지·저항 **음영 구간**, (서버가 좌표를 주면) 확정 피벗 마커, 계획 Entry/Stop/Target 수평선.
// 그리지 않는 것: 구조가 없을 때의 임의 선. 계획(plan)이 없으면 Entry/Stop/Target을 아예 그리지 않고
// 캡션으로 사유를 적는다(§13, §19-5).
//
// 가격 곡선은 `/api/state`의 v4 signal 차트 배열(최근 완료 1분봉 종가)을 배경으로만 쓴다.
// 구조 분석 자체의 봉은 API로 노출되지 않으므로 "표시용 배경"이라고 명시한다.
// TODO(D3 대조): 구조 엔진이 쓴 봉/피벗 좌표가 DTO에 추가되면 배경도 그 값으로 바꾼다.

export type ChartBar = { time: string; close: number };

type Props = {
  bars: ChartBar[];
  analysis: StructureAnalysis | null;
  candidate: StructureCandidate | null;
};

type Tip = { x: number; y: number; lines: string[] };
type ZoneHit = { top: number; bottom: number; lines: string[] };
type PointHit = { x: number; y: number; lines: string[] };

const COLOR = {
  line: "#8fa3b8",
  support: "rgba(90, 160, 255, 0.20)",
  supportEdge: "rgba(90, 160, 255, 0.75)",
  resistance: "rgba(214, 145, 74, 0.20)",
  resistanceEdge: "rgba(214, 145, 74, 0.75)",
  neutral: "rgba(150, 150, 165, 0.16)",
  neutralEdge: "rgba(150, 150, 165, 0.6)",
  entry: "#b3a5ff",
  stop: "#e0857f",
  target: "#66c2b5",
  quote: "#f0c674",
  pivot: "#9fb4ff",
};

/** 값이 유한한 숫자인지. 서버는 NaN/Infinity를 보내지 않지만 화면에서 다시 확인한다(§11). */
const finite = (v: number | null | undefined): v is number =>
  typeof v === "number" && Number.isFinite(v);

export default function StructureChart({ bars, analysis, candidate }: Props) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const zoneHits = useRef<ZoneHit[]>([]);
  const pointHits = useRef<PointHit[]>([]);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [tip, setTip] = useState<Tip | null>(null);

  const plan = candidate?.plan ?? null;
  const zones = useMemo(
    () => arr(analysis?.zones).filter((z) => !z.retired && finite(z.lower) && finite(z.upper)),
    [analysis],
  );
  const pivots = useMemo(
    () => arr(analysis?.pivots).filter((p) => finite(p.price)),
    [analysis],
  );
  const series = useMemo(() => bars.filter((b) => finite(b.close)), [bars]);

  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const apply = () => {
      const rect = el.getBoundingClientRect();
      setSize({ width: Math.round(rect.width), height: Math.round(rect.height) });
    };
    apply();
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(apply);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const canvas = canvasRef.current;
    zoneHits.current = [];
    pointHits.current = [];
    if (!canvas || size.width < 40 || size.height < 40 || series.length < 2) return;

    const dpr = window.devicePixelRatio || 1;
    canvas.width = size.width * dpr;
    canvas.height = size.height * dpr;
    const ctx = canvas.getContext("2d");
    if (!ctx) return;
    ctx.scale(dpr, dpr);
    ctx.clearRect(0, 0, size.width, size.height);

    const padLeft = 8;
    const padRight = 62; // 오른쪽 가격 라벨 자리
    const padTop = 10;
    const padBottom = 16;
    const plotW = Math.max(10, size.width - padLeft - padRight);
    const plotH = Math.max(10, size.height - padTop - padBottom);

    const closes = series.map((b) => b.close);
    const barLo = Math.min(...closes);
    const barHi = Math.max(...closes);
    const barRange = barHi - barLo || Math.max(0.01, barHi * 0.002);

    // 차트 세로 범위: 봉 범위 ±1.2배 안에 들어오는 구조만 포함한다.
    // 멀리 있는 구간까지 억지로 담아 봉 움직임을 뭉개지 않는다. 제외한 개수는 캡션으로 알린다.
    const windowLo = barLo - barRange * 1.2;
    const windowHi = barHi + barRange * 1.2;
    const inWindow = (v: number) => v >= windowLo && v <= windowHi;

    const visibleZones = zones.filter((z) => z.upper >= windowLo && z.lower <= windowHi);
    const hiddenZoneCount = zones.length - visibleZones.length;

    const extras: number[] = [];
    visibleZones.forEach((z) => {
      extras.push(Math.max(z.lower, windowLo), Math.min(z.upper, windowHi));
    });
    if (plan) {
      [plan.entryReference, plan.stop, plan.target].forEach((v) => {
        if (finite(v) && inWindow(v)) extras.push(v);
      });
    }
    if (finite(analysis?.quotePrice) && inWindow(analysis!.quotePrice!))
      extras.push(analysis!.quotePrice!);
    pivots.forEach((p) => {
      if (inWindow(p.price)) extras.push(p.price);
    });

    const lo = Math.min(barLo, ...extras);
    const hi = Math.max(barHi, ...extras);
    const pad = (hi - lo) * 0.06 || 0.05;
    const domLo = lo - pad;
    const domHi = hi + pad;
    const span = domHi - domLo || 1;

    const px = (i: number) => padLeft + (i / (series.length - 1)) * plotW;
    const py = (v: number) => padTop + ((domHi - v) / span) * plotH;

    // ── 지지·저항 음영 구간 ──
    visibleZones.forEach((zone) => {
      const group = zoneRoleGroup(zone.role);
      const top = py(Math.min(zone.upper, domHi));
      const bottom = py(Math.max(zone.lower, domLo));
      const height = Math.max(2, bottom - top);
      const eligible = zone.eligible === true;
      ctx.save();
      ctx.globalAlpha = eligible ? 1 : 0.45;
      ctx.fillStyle =
        group === "support"
          ? COLOR.support
          : group === "resistance"
            ? COLOR.resistance
            : COLOR.neutral;
      ctx.fillRect(padLeft, top, plotW, height);
      ctx.strokeStyle =
        group === "support"
          ? COLOR.supportEdge
          : group === "resistance"
            ? COLOR.resistanceEdge
            : COLOR.neutralEdge;
      ctx.lineWidth = 1;
      ctx.setLineDash(eligible ? [] : [3, 3]);
      ctx.beginPath();
      ctx.moveTo(padLeft, top);
      ctx.lineTo(padLeft + plotW, top);
      ctx.moveTo(padLeft, bottom);
      ctx.lineTo(padLeft + plotW, bottom);
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.restore();

      const lines = [
        `${zoneRoleLabel(zone.role)} ${priceRange(zone.lower, zone.upper)}`,
        `강도 ${zone.strength == null ? "산출 불가" : `${zone.strength.toFixed(2)}(0~1)`} · 독립 증거 ${zone.independentFamilies ?? 0}계열`,
        `완료 반응 ${zone.completedEpisodes ?? 0}회 (성공 ${zone.successEpisodes ?? 0} / 실패 ${zone.failedEpisodes ?? 0}${(zone.pendingEpisodes ?? 0) > 0 ? ` / 진행 ${zone.pendingEpisodes}` : ""})`,
        `최초 확정 ${clock(zone.firstConfirmedAt)} · 최근 확정 ${clock(zone.lastConfirmedAt)}`,
        `원천: ${arr(zone.sourceKinds).map(sourceKindLabel).join(", ") || "미상"}`,
      ];
      if (!eligible) lines.push("자격 미달 — 계획 근거로 쓰지 않습니다");
      if (zone.profileOnly)
        lines.push("추정 거래량 프로파일만 근거 (실제 체결 분포·수급 아님)");
      zoneHits.current.push({ top, bottom: top + height, lines });
    });

    // ── 배경 가격 곡선 ──
    ctx.beginPath();
    series.forEach((b, i) => {
      const x = px(i);
      const y = py(b.close);
      i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
    });
    ctx.strokeStyle = COLOR.line;
    ctx.lineWidth = 1.6;
    ctx.stroke();

    // ── 계획 수평선: plan이 있을 때만 ──
    const label = (text: string, y: number, color: string) => {
      ctx.fillStyle = color;
      ctx.font = "10px Inter, sans-serif";
      ctx.textBaseline = "middle";
      ctx.fillText(text, padLeft + plotW + 5, Math.min(size.height - 6, Math.max(8, y)));
    };
    const hline = (v: number, color: string, dash: number[], text: string) => {
      if (!finite(v) || !inWindow(v)) return;
      const y = py(v);
      ctx.beginPath();
      ctx.setLineDash(dash);
      ctx.moveTo(padLeft, y);
      ctx.lineTo(padLeft + plotW, y);
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.4;
      ctx.stroke();
      ctx.setLineDash([]);
      label(`${text} ${v.toFixed(2)}`, y, color);
    };

    if (plan) {
      hline(plan.target, COLOR.target, [6, 3], "목표");
      hline(plan.entryReference, COLOR.entry, [2, 3], "진입");
      hline(plan.stop, COLOR.stop, [6, 3], "손절");
    }
    if (finite(analysis?.quotePrice)) hline(analysis!.quotePrice!, COLOR.quote, [1, 4], "호가");

    // ── 확정 피벗 마커 (서버가 좌표를 줄 때만) ──
    const timeIndex = (value: string | null | undefined): number | null => {
      if (!value) return null;
      const t = new Date(value).getTime();
      if (Number.isNaN(t)) return null;
      let best = -1;
      let bestGap = Number.POSITIVE_INFINITY;
      series.forEach((b, i) => {
        const bt = new Date(b.time).getTime();
        if (Number.isNaN(bt)) return;
        const gap = Math.abs(bt - t);
        if (gap < bestGap) {
          bestGap = gap;
          best = i;
        }
      });
      return best < 0 ? null : best;
    };

    pivots.forEach((p) => {
      if (!inWindow(p.price)) return;
      const idx = timeIndex(p.occurredAt ?? p.confirmedAt);
      if (idx == null) return;
      const x = px(idx);
      const y = py(p.price);
      const high = (p.kind ?? "").toUpperCase() === "HIGH";
      ctx.beginPath();
      ctx.fillStyle = COLOR.pivot;
      if (high) {
        ctx.moveTo(x, y - 5);
        ctx.lineTo(x - 4, y + 2);
        ctx.lineTo(x + 4, y + 2);
      } else {
        ctx.moveTo(x, y + 5);
        ctx.lineTo(x - 4, y - 2);
        ctx.lineTo(x + 4, y - 2);
      }
      ctx.closePath();
      ctx.fill();
      pointHits.current.push({
        x,
        y,
        lines: [
          `확정 피벗 ${high ? "고점" : "저점"} ${p.price.toFixed(2)}`,
          `발생 ${clock(p.occurredAt)} · 확인 ${clock(p.confirmedAt)}`,
          p.timeframe ? `봉 ${p.timeframe}` : "",
        ].filter(Boolean),
      });
    });

    // ── 오른쪽 위 캡션(범위 밖 구조 개수) ──
    if (hiddenZoneCount > 0) {
      ctx.fillStyle = "rgba(150,150,165,0.9)";
      ctx.font = "10px Inter, sans-serif";
      ctx.textBaseline = "top";
      ctx.fillText(`차트 범위 밖 구간 ${hiddenZoneCount}개`, padLeft + 2, 2);
    }
  }, [series, zones, pivots, plan, analysis, size]);

  const onMove = (event: React.MouseEvent<HTMLDivElement>) => {
    const el = wrapRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const x = event.clientX - rect.left;
    const y = event.clientY - rect.top;
    const point = pointHits.current.find(
      (p) => Math.abs(p.x - x) <= 7 && Math.abs(p.y - y) <= 7,
    );
    if (point) {
      setTip({ x, y, lines: point.lines });
      return;
    }
    const zone = zoneHits.current.find((z) => y >= z.top - 1 && y <= z.bottom + 1);
    setTip(zone ? { x, y, lines: zone.lines } : null);
  };

  const empty = series.length < 2;
  const planNote = !analysis
    ? "구조 분석 결과가 없어 계획선을 그리지 않습니다."
    : plan
      ? null
      : candidate
        ? "이 후보에는 성립한 계획이 없어 진입·손절·목표 선을 그리지 않습니다 (사유는 아래 설명 참고)."
        : "진입 후보가 없어 계획선을 그리지 않습니다.";

  return (
    <div className="structure-chart-block">
      <div className="structure-chart-meta">
        <span className="sc-chip" title="마지막 완료 봉의 종료 시각 — 구조 표시 기준">
          완료 봉 AsOf(표시 기준) <b>{clock(analysis?.analysisAsOf)}</b>
        </span>
        <span className="sc-chip" title="후보 판정 cutoff — 트리거 봉 시작 시각">
          후보 판정 cutoff <b>{clock(analysis?.lastCompletedBarStart)}</b>
        </span>
        <span className="sc-chip quote">
          실시간 호가 <b>{price(analysis?.quotePrice)}</b> · {clock(analysis?.quoteAt)}
        </span>
      </div>
      <div
        className="structure-chart"
        ref={wrapRef}
        onMouseMove={onMove}
        onMouseLeave={() => setTip(null)}
      >
        {empty ? (
          <div className="structure-chart-empty">
            배경 가격 곡선(완료 1분봉)이 아직 부족합니다. 구간·계획 값은 아래 설명에서 확인하세요.
          </div>
        ) : (
          <canvas ref={canvasRef} />
        )}
        {tip && (
          <div
            className="structure-tip"
            style={{
              left: Math.min(Math.max(8, tip.x + 12), Math.max(8, size.width - 220)),
              top: Math.min(Math.max(4, tip.y + 12), Math.max(4, size.height - 90)),
            }}
          >
            {tip.lines.map((line, i) => (
              <div key={i}>{line}</div>
            ))}
          </div>
        )}
      </div>
      <div className="structure-chart-legend">
        <span>
          <i className="sc-zone support" /> 지지 구간(음영)
        </span>
        <span>
          <i className="sc-zone resistance" /> 저항 구간(음영)
        </span>
        <span>
          <i className="sc-zone dashed" /> 자격 미달 구간(점선)
        </span>
        <span>
          <i className="sc-line entry" /> 진입
        </span>
        <span>
          <i className="sc-line stop" /> 손절
        </span>
        <span>
          <i className="sc-line target" /> 목표
        </span>
        <span>
          <i className="sc-line quote" /> 실시간 호가
        </span>
      </div>
      <p className="structure-chart-note">
        배경 곡선은 `/api/state`가 주는 최근 완료 1분봉 종가이며 표시용입니다. 구조 판단은 구간·계획
        값으로만 하세요.
        {pivots.length === 0 && (
          <>
            {" "}
            확정 피벗 좌표는 현재 구조 API에 노출되지 않아 마커를 그리지 않습니다. 각 구간의 확정
            시각은 음영 위에 마우스를 올리면 표시됩니다.
          </>
        )}
        {planNote && <> {planNote}</>}
      </p>
    </div>
  );
}
