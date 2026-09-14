import { FormEvent, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import {
  Activity,
  AlertTriangle,
  BarChart3,
  Bell,
  BellOff,
  ChevronRight,
  Gauge,
  History,
  LayoutDashboard,
  Layers,
  PanelLeftClose,
  PanelLeftOpen,
  Play,
  Plus,
  Search,
  Square,
  Trash2,
  Wifi,
  WifiOff,
  X,
} from "lucide-react";
import { useVisiblePolling } from "./useVisiblePolling";
import LiquidityPanel from "./LiquidityPanel";
import FeeWarningBadge from "./FeeWarningBadge";
import StructurePanel from "./StructurePanel";
// 이슈 #84: v5 코호트 섹션(SimStructurePanel)은 대시보드 렌더링에서 제거했다.
// data.structure/서버 집계(SimulationCohorts.cs)는 유지되며, 아래 import와
// <SimStructurePanel report={data.structure} /> 한 줄을 되돌리면 복원된다.
import LiveStructureCells from "./LiveStructureCells";
import RealVsV5Panel from "./RealVsV5Panel";
import { planV5Notifications, v4PushEnabled } from "./alertPlanner";
import { formatViewHash, parseViewHash } from "./viewRoute";
import type {
  StructureEventRow,
  StructureSummary,
  StructureSummaryRow,
} from "./structureTypes";
import { sidebarConfluenceScore } from "./structureTypes";
import {
  LiveSortKey,
  compareByEntryQuality,
  compareBySymbol,
  compareByTrendDirection,
  compareByTrendStrength,
  compareByV5State,
  resolveSortKey,
  sortKeysForMode,
} from "./structureSort";
import type { StructureCohortReport, TradeStructure } from "./dashboardTypes";
import { tradeEntryTooltip } from "./dashboardTypes";
import { blockTradeLabel, flowSourceLabel } from "./tradeTape";
import { turnoverText } from "./metricsFormat";
import NewsPanel from "./NewsPanel";
import {
  findSymbolScore,
  normalizeNewsHealth,
  normalizeSentimentResponse,
  shouldRenderNewsUi,
  type NewsSentimentResponse,
} from "./newsTypes";
import { scoreBadge, scoreBadgeTitle } from "./newsFormat";
import ConfluencePanel from "./ConfluencePanel";
import { gaugeTone, scoreText2 } from "./confluenceFormat";
import HistoricalReplayPanel from "./HistoricalReplayPanel";

type Bar = { time: string; close: number; ema?: number; vwap?: number };
type Indicators = {
  rsi: number | null;
  emaFast: number | null;
  emaSlow: number | null;
  vwap: number | null;
  atr: number | null;
  relativeVolume: number | null;
};
type Position = {
  entryPrice: number;
  quantity: number;
  target: number | null;
  stop: number | null;
  targetBasis?: string | null;
  stopBasis?: string | null;
  pnlPercent: number | null;
  status: string;
};

export function WatchRowContent({
  symbol,
  name,
  children,
}: {
  symbol: string;
  name: string;
  children?: ReactNode;
}) {
  return (
    <>
      <div className="watch-identity">
        <b>{symbol}</b>
        <small>{name}</small>
      </div>
      {children && <div className="watch-metrics">{children}</div>}
    </>
  );
}
type Signal = {
  symbol: string;
  name: string;
  price: number | null;
  changePercent: number | null;
  score: number | null;
  action: string;
  reasons: string[];
  updatedAt: string | null;
  stale: boolean;
  indicators: Indicators | null;
  bars: Bar[];
  position?: Position;
  setup?: string | null;
  setupAt?: string | null;
  breakout?: string | null;
  breakoutAt?: string | null;
};
type State = {
  running: boolean;
  mode: "live";
  connection: { status: string; message: string };
  transport?: {
    mode: "websocket" | "polling";
    status: string;
    message: string;
    lastTickAt: string | null;
  };
  market: { isOpen: boolean; label: string; nextOpen?: string };
  updatedAt: string;
  watchlist: { symbol: string; name: string }[];
  signals: Signal[];
  events: unknown[];
  /**
   * 설계 §12의 additive 필드. v4 score/action의 의미를 덮어쓰지 않는다.
   * 구버전 서버(또는 구조 엔진 미구성)에서는 없을 수 있으므로 optional로 둔다.
   */
  structureSummary?: StructureSummary | null;
  /**
   * 이슈 #26: 서버가 발행한 v5 알림 이벤트(최근 50건, seq 단조 증가). active에서만 채워지며
   * FE는 seq seed + Notification tag로 소비만 한다. 구버전 서버에는 없다.
   */
  structureEvents?: StructureEventRow[] | null;
  /**
   * 이슈 #130: 실계좌 US 왕복 수수료와 StructurePolicy.RoundTripFeePercent 불일치·만료 임박 경고.
   * 구버전 서버에는 없을 수 있으므로 optional로 둔다.
   */
  warnings?: string[] | null;
};
type SearchResult = { symbol: string; name: string };
type DailyMetrics = {
  volumeRatio3: number | null;
  volumeRatio5: number | null;
  volumeRatio20: number | null;
  todayVolume: number;
  gapPercent: number | null;
  changeFromPrevClose: number | null;
  rangePosition20: number | null;
  maGap5: number | null;
  maGap20: number | null;
  sessionElapsedPercent: number;
};
type TickFlow = {
  buyVolume: number;
  sellVolume: number;
  strength: number | null;
  buyShare: number | null;
  windowMinutes: number;
};
type SimStats = {
  total: number;
  open: number;
  closed: number;
  wins: number;
  winRate: number | null;
  avgPnl: number | null;
  totalPnl: number | null;
  validClosed?: number;
  missingPnl?: number;
  estimatedExits?: number;
};
type SimTradeRow = {
  id: string;
  symbol: string;
  kind: string;
  enteredAt: string;
  entryPrice: number;
  target: number;
  stop: number;
  targetBasis?: string | null;
  stopBasis?: string | null;
  status: string;
  exitPrice?: number | null;
  exitAt?: string | null;
  pnlPercent?: number | null;
  lastPrice: number;
  score?: number | null;
  extSigma?: number | null;
  relVolume?: number | null;
  buyShare?: number | null;
  rsi?: number | null;
  reasons?: string[] | null;
  logic?: string | null;
  exitEstimated?: boolean | null;
  lastPriceAt?: string | null;
  /** 이슈 #27: v5 거래의 진입 시점 동결 컨텍스트(§11). v4/legacy 거래에는 없다. */
  structure?: TradeStructure | null;
};
type SimProfile = {
  count: number;
  score: number | null;
  extSigma: number | null;
  relVolume: number | null;
  buyShare: number | null;
  rsi: number | null;
};
type SimAnalysis = {
  generatedAt: string;
  closedCount: number;
  winRate: number;
  targetWinRate: number;
  avgWin: number | null;
  avgLoss: number | null;
  expectancy: number;
  exitShare: { stop: number; cut: number; eod: number; target: number };
  winnerProfile: SimProfile;
  loserProfile: SimProfile;
  insights: string[];
  sampleWarning?: string;
};
type SimData = {
  summary: SimStats;
  byKind: { kind: string; stats: SimStats }[];
  analysis: SimAnalysis | null;
  trades: SimTradeRow[];
  /** 이슈 #27: v5 동결 근거 기준 코호트 집계(additive). 구버전 서버에는 없을 수 있다. */
  structure?: StructureCohortReport | null;
};
type Metrics = {
  symbol: string;
  marketOpen: boolean;
  running: boolean;
  daily: DailyMetrics | null;
  daily5m: DailyMetrics | null;
  daily10m: DailyMetrics | null;
  flow: TickFlow | null;
  flow5m: TickFlow | null;
  flow10m: TickFlow | null;
  updatedAt: string;
  /** 이슈 #133: 체결강도 원천("ws"|"rest"|"none")과 블록 체결 건수. 구버전 서버에는 없다. */
  flowSource?: string | null;
  blockTradeCount?: number | null;
  /** 이슈 #132: 당일 누적 거래량 / 상장주식수(%). 메타가 없는 구버전 서버·종목에서는 없다. */
  turnoverPercent?: number | null;
};
const api = async <T,>(url: string, init?: RequestInit): Promise<T> => {
  const r = await fetch(url, init);
  if (!r.ok) {
    let message = `요청 실패 (${r.status})`;
    try {
      const body = await r.json();
      message =
        body.message || body.detail || body.title || body.error || message;
    } catch {
      /* non-JSON response */
    }
    throw new Error(message);
  }
  if (r.status === 204) return undefined as T;
  const text = await r.text();
  return text ? JSON.parse(text) : (undefined as T);
};
const money = (v: number | null | undefined) =>
  v == null
    ? "—"
    : new Intl.NumberFormat("en-US", {
        style: "currency",
        currency: "USD",
        minimumFractionDigits: 2,
      }).format(v);
const percent = (v: number | null | undefined) =>
  v == null ? "—" : `${v >= 0 ? "+" : ""}${v.toFixed(2)}%`;
const beep = () => {
  try {
    const ctx = new AudioContext();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.frequency.value = 880;
    gain.gain.value = 0.12;
    osc.start();
    osc.frequency.setValueAtTime(1175, ctx.currentTime + 0.12);
    gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.35);
    osc.stop(ctx.currentTime + 0.4);
    setTimeout(() => ctx.close(), 600);
  } catch {
    /* 오디오가 차단된 환경 */
  }
};
const liveSortLabel = (key: LiveSortKey): string =>
  key === "v5"
    ? "v5 평가"
    : key === "trend"
      ? "추세 강도"
      : key === "direction"
        ? "추세 방향"
        : key === "quality"
          ? "진입 품질"
          : "종목명";
const liveSortTitle = (key: LiveSortKey): string =>
  key === "v5"
    ? "v5 상태 우선 → 진입 품질 → 추세 강도 → 심볼"
    : key === "trend"
      ? "v5 추세 강도(절대값) 내림차순 → 심볼"
      : key === "direction"
        ? "추세 방향(부호 있는 값) 내림차순 → 심볼 — 상승이 위, 하락이 아래"
        : key === "quality"
          ? "진입 품질 내림차순 → 심볼. 결측(후보 없음)은 항상 마지막"
          : "종목명 오름차순";
const liveSortDescription = (key: LiveSortKey): string =>
  key === "v5"
    ? "v5 상태·진입 품질 순 정렬"
    : key === "trend"
      ? "v5 추세 강도 순 정렬"
      : key === "direction"
        ? "추세 방향(부호) 순 정렬"
        : key === "quality"
          ? "진입 품질 순 정렬"
          : "종목명 알파벳순 정렬";
const transportLabel = (state: State | null) => {
  if (!state || state.connection.status !== "connected") return "대기";
  if (state.transport?.mode === "websocket") return "웹소켓";
  if (state.transport?.mode === "polling") return "REST 폴링";
  return "대기";
};
const time = (v: string | null | undefined) => {
  if (!v) return "—";
  const d = new Date(v);
  return Number.isNaN(d.getTime())
    ? "—"
    : d.toLocaleTimeString("ko-KR", {
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
      });
};

function MiniChart({ bars, positive }: { bars: Bar[]; positive: boolean }) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const c = ref.current;
    if (!c || bars.length < 2) return;
    const rect = c.getBoundingClientRect(),
      dpr = devicePixelRatio || 1;
    c.width = rect.width * dpr;
    c.height = rect.height * dpr;
    const x = c.getContext("2d")!;
    x.scale(dpr, dpr);
    const all = bars.flatMap((b) =>
      [b.close, b.ema, b.vwap].filter((v): v is number => v != null),
    );
    const lo = Math.min(...all),
      hi = Math.max(...all),
      range = hi - lo || 1;
    const px = (i: number) => (i / (bars.length - 1)) * rect.width;
    const py = (v: number) => 8 + ((hi - v) / range) * (rect.height - 20);
    const line = (
      get: (b: Bar) => number | undefined,
      color: string,
      width: number,
      dash: number[],
    ) => {
      x.beginPath();
      x.setLineDash(dash);
      let started = false;
      bars.forEach((b, i) => {
        const v = get(b);
        if (v == null) return;
        started ? x.lineTo(px(i), py(v)) : x.moveTo(px(i), py(v));
        started = true;
      });
      x.strokeStyle = color;
      x.lineWidth = width;
      x.stroke();
      x.setLineDash([]);
    };
    x.clearRect(0, 0, rect.width, rect.height);
    const grad = x.createLinearGradient(0, 0, 0, rect.height);
    const color = positive ? "#27c499" : "#ef6571";
    grad.addColorStop(
      0,
      positive ? "rgba(39,196,153,.25)" : "rgba(239,101,113,.22)",
    );
    grad.addColorStop(1, "rgba(0,0,0,0)");
    x.beginPath();
    bars.forEach((b, i) => {
      i ? x.lineTo(px(i), py(b.close)) : x.moveTo(px(i), py(b.close));
    });
    x.lineTo(rect.width, rect.height);
    x.lineTo(0, rect.height);
    x.fillStyle = grad;
    x.fill();
    line((b) => b.vwap, "#e8a04c", 1.3, [5, 4]);
    line((b) => b.ema, "#6aa7ff", 1.3, []);
    line((b) => b.close, color, 2, []);
  }, [bars, positive]);
  return bars.length < 2 ? (
    <div className="chart-empty">차트 데이터 대기 중</div>
  ) : (
    <>
      <canvas ref={ref} />
      {bars.some((b) => b.vwap != null) && (
        <div className="chart-legend">
          <i className="l-close" /> 종가 <i className="l-ema" /> EMA9{" "}
          <i className="l-vwap" /> VWAP
        </div>
      )}
    </>
  );
}

export default function App() {
  const loadingState = useRef(false);
  const stateRef = useRef<State | null>(null);
  const [state, setState] = useState<State | null>(null),
    [selected, setSelected] = useState(
      () => parseViewHash(window.location.hash).symbol ?? "",
    ),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false),
    [query, setQuery] = useState(""),
    [results, setResults] = useState<SearchResult[]>([]),
    [searching, setSearching] = useState(false),
    [sideOpen, setSideOpen] = useState(
      () => localStorage.getItem("astra-side") !== "closed",
    ),
    [metrics, setMetrics] = useState<Metrics | null>(null),
    [alertsOn, setAlertsOn] = useState(
      () => localStorage.getItem("astra-alerts") === "on",
    ),
    [notice, setNotice] = useState(""),
    // v5 구조 분석은 실시간 시그널 화면과 섞지 않고 별도 뷰로 분리한다(설계 §13, §19-10).
    // 이슈 #128: 새로고침 후에도 화면을 유지하기 위해 URL hash에서 초기값을 읽는다.
    [view, setView] = useState<"live" | "dash" | "structure" | "replay">(
      () => parseViewHash(window.location.hash).view,
    ),
    // 이슈 #26: 라이브 목록 정렬 선택(모드별 유효성은 resolveSortKey가 판정). localStorage에 저장.
    [liveSortChoice, setLiveSortChoice] = useState(
      () => localStorage.getItem("astra-live-sort") || "",
    ),
    // 이슈 #152: 뉴스 감성. news.enabled(health)와 sentiment 응답 enabled가 모두 true일 때만 렌더한다.
    [newsHealthEnabled, setNewsHealthEnabled] = useState<boolean | null>(null),
    [newsSentiment, setNewsSentiment] = useState<NewsSentimentResponse | null>(null),
    // 이슈 #168/#181: 선택 종목은 ConfluencePanel의 기존 폴링 결과를, 나머지 행은
    // structureSummary 캐시 요약을 그대로 쓴다(추가 호출 없음, §4).
    [confluenceScore, setConfluenceScore] = useState<{ symbol: string; score: number | null } | null>(null),
    [form, setForm] = useState({ entryPrice: "", quantity: "" });
  const seenSetups = useRef<Record<string, string>>({});
  const seenBreakouts = useRef<Record<string, string>>({});
  // 이슈 #26 PR-3: v5 이벤트 소비 기준 seq. null = 아직 seed 전(새로고침 직후 과거 이벤트를 울리지 않음).
  const lastV5Seq = useRef<number | null>(null);
  const alertsSeeded = useRef(false);
  const connectionFailed = useRef(false);
  const suppressAlertSnapshot = useRef(false);
  const previousRunning = useRef<boolean | null>(null);
  const lastNotified = useRef<Record<string, number>>({});
  const formDrafts = useRef<Record<string, { entryPrice: string; quantity: string }>>({});
  const load = async (silent = false) => {
    if (loadingState.current) return;
    loadingState.current = true;
    try {
      const d = await api<State>("/api/state");
      if (connectionFailed.current) suppressAlertSnapshot.current = true;
      connectionFailed.current = false;
      stateRef.current = d;
      setState(d);
      setError("");
      setSelected(
        (v) => d.watchlist.some((w) => w.symbol === v) ? v : d.signals[0]?.symbol || d.watchlist[0]?.symbol || "",
      );
    } catch (e) {
      connectionFailed.current = true;
      setState((prev) =>
        prev
          ? {
              ...prev,
              connection: { status: "disconnected", message: "시세 연결 끊김" },
              signals: prev.signals.map((s) => ({
                ...s,
                stale: true,
                action: "HOLD",
              })),
            }
          : prev,
      );
      if (!silent || !stateRef.current)
        setError(e instanceof Error ? e.message : "서버에 연결할 수 없습니다");
    } finally {
      loadingState.current = false;
    }
  };
  // 이슈 #152: 실패는 조용히 무시하고 이전 값을 유지한다(설계 §4).
  const loadNews = async () => {
    try {
      const health = await api<{ news?: unknown }>("/api/health");
      setNewsHealthEnabled(normalizeNewsHealth(health.news)?.enabled ?? false);
    } catch {
      /* 이전 값 유지 */
    }
    try {
      setNewsSentiment(normalizeSentimentResponse(await api("/api/news/sentiment")));
    } catch {
      /* 이전 값 유지 */
    }
  };
  useEffect(() => {
    let active = true;
    let id: ReturnType<typeof setTimeout>;
    const poll = async () => {
      await load(true);
      await loadNews();
      if (active) id = setTimeout(poll, 3000);
    };
    void poll();
    return () => {
      active = false;
      clearTimeout(id);
    };
  }, []);
  useEffect(() => {
    localStorage.setItem("astra-side", sideOpen ? "open" : "closed");
  }, [sideOpen]);
  useEffect(() => {
    localStorage.setItem("astra-alerts", alertsOn ? "on" : "off");
  }, [alertsOn]);
  useEffect(() => {
    const symbol = view === "structure" ? selected : undefined;
    history.replaceState(null, "", formatViewHash(view, symbol));
  }, [view, selected]);
  useEffect(() => {
    const restarting = previousRunning.current === false && state?.running === true;
    const suppress = !alertsSeeded.current || suppressAlertSnapshot.current || restarting;
    const notify = (symbol: string, title: string, body: string) => {
      const last = lastNotified.current[symbol] ?? 0;
      if (Date.now() - last <= 5 * 60_000) return;
      lastNotified.current[symbol] = Date.now();
      beep();
      if ("Notification" in window && Notification.permission === "granted") {
        const n = new Notification(title, {
          body,
          tag: `astra-${symbol}`,
        });
        n.onclick = () => {
          window.focus();
          setSelected(symbol);
          n.close();
        };
      }
    };
    // ── 이슈 #26 PR-3: active에서는 서버가 발행한 v5 이벤트만 푸시·소리를 울린다(승인 설계안 §4).
    // 이슈 #88: 화면에서는 v4 표시를 전면 제거했지만, off/shadow의 셋업·돌파 푸시·소리는
    // 승인된 §4 설계 그대로 유지한다(화면 표시 제거와 알림 동작은 별개 결정).
    // 중복 방지: seq seed(새로고침 회귀) + Notification tag.
    // 심볼당 5분 스로틀은 v5 이벤트에 적용하지 않는다 — READY 직후 ENTERED를 삼키면 안 된다.
    const structureMode = state?.structureSummary?.mode ?? null;
    const v4Push = v4PushEnabled(structureMode);
    if (structureMode === "active") {
      const plan = planV5Notifications(state?.structureEvents, lastV5Seq.current);
      // 알림 꺼짐이어도 seq는 전진시켜, 나중에 켰을 때 백로그를 한꺼번에 쏟지 않는다.
      lastV5Seq.current = plan.nextSeq;
      if (alertsOn && plan.notifications.length > 0) {
        beep();
        for (const v5 of plan.notifications) {
          if ("Notification" in window && Notification.permission === "granted") {
            const n = new Notification(v5.title, { body: v5.body, tag: v5.tag });
            n.onclick = () => {
              window.focus();
              setSelected(v5.symbol);
              n.close();
            };
          }
        }
      }
    }
    for (const s of state?.signals ?? []) {
      const setupIdentity = s.setup && s.setupAt ? `${s.setup}:${s.setupAt}` : "";
      const setupName =
        s.setup === "SETUP"
          ? "진입 셋업"
          : s.setup === "REBOUND"
            ? "과매도 반등"
            : null;
      if (
        v4Push &&
        !suppress &&
        alertsOn &&
        setupName &&
        setupIdentity &&
        seenSetups.current[s.symbol] !== setupIdentity
      )
        notify(
          s.symbol,
          `${s.symbol} ${setupName} · ${Math.round(s.score ?? 0)}점`,
          `반등 트리거 · ${money(s.price)}`,
        );
      if (setupIdentity) seenSetups.current[s.symbol] = setupIdentity;
      const breakoutIdentity =
        s.breakout && s.breakoutAt ? `${s.breakout}:${s.breakoutAt}` : "";
      if (
        v4Push &&
        !suppress &&
        alertsOn &&
        s.breakout &&
        breakoutIdentity &&
        seenBreakouts.current[s.symbol] !== breakoutIdentity
      )
        notify(
          s.symbol,
          `${s.symbol} 저항 돌파`,
          `${s.breakout} 상향 돌파 · ${money(s.price)}`,
        );
      if (breakoutIdentity) seenBreakouts.current[s.symbol] = breakoutIdentity;
    }
    if (state) {
      alertsSeeded.current = true;
      suppressAlertSnapshot.current = false;
      previousRunning.current = state.running;
    }
  }, [state, alertsOn]);
  useEffect(() => {
    setConfluenceScore(null);
  }, [selected]);
  useEffect(() => {
    if (!selected) return;
    const draft = formDrafts.current[selected];
    if (draft) {
      setForm(draft);
      return;
    }
    const price = state?.signals.find((x) => x.symbol === selected)?.price;
    if (price != null) {
      const next = { entryPrice: String(Math.round(price * 100) / 100), quantity: "" };
      formDrafts.current[selected] = next;
      setForm(next);
    } else {
      setForm({ entryPrice: "", quantity: "" });
    }
  }, [selected, state]);
  const updateForm = (next: { entryPrice: string; quantity: string }) => {
    if (selected) formDrafts.current[selected] = next;
    setForm(next);
  };
  const toggleAlerts = async () => {
    if (alertsOn) {
      setAlertsOn(false);
      return;
    }
    if ("Notification" in window && Notification.permission !== "granted") {
      const p = await Notification.requestPermission();
      setNotice(
        p === "granted"
          ? ""
          : "브라우저 알림 권한이 거부되어 소리 알림만 사용합니다. 주소창 왼쪽 자물쇠 → 사이트 설정 → 알림을 허용으로 바꾸면 됩니다.",
      );
    }
    beep();
    setAlertsOn(true);
  };
  const running = state?.running ?? false;
  useEffect(() => {
    if (!selected || !running) {
      setMetrics(null);
      return;
    }
    let active = true;
    let id: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const d = await api<Metrics>(`/api/metrics/${selected}`);
        if (active) setMetrics(d);
      } catch {
        if (active) setMetrics(null);
      }
      if (active) id = setTimeout(poll, 15000);
    };
    poll();
    return () => {
      active = false;
      clearTimeout(id);
    };
  }, [selected, running]);
  useEffect(() => {
    if (!query.trim()) {
      setResults([]);
      setSearching(false);
      return;
    }
    const ctl = new AbortController(),
      requestQuery = query.trim(),
      id = setTimeout(async () => {
        setSearching(true);
        try {
          setResults(
            await api<SearchResult[]>(
              `/api/search?q=${encodeURIComponent(requestQuery)}`,
              { signal: ctl.signal },
            ),
          );
        } catch (e) {
          if ((e as Error).name !== "AbortError")
            setError((e as Error).message);
        } finally {
          if (!ctl.signal.aborted) setSearching(false);
        }
      }, 300);
    return () => {
      clearTimeout(id);
      ctl.abort();
    };
  }, [query]);
  const mutate = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    try {
      await fn();
      await load();
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "요청을 처리하지 못했습니다");
    } finally {
      setBusy(false);
    }
  };
  const add = async (r: SearchResult) => {
    setBusy(true);
    try {
      await api("/api/watchlist", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(r),
      });
      await load();
      setQuery("");
      setResults([]);
      setSelected(r.symbol);
    } catch (e) {
      setError(e instanceof Error ? e.message : "종목을 추가하지 못했습니다");
    } finally {
      setBusy(false);
    }
  };
  const signal = state?.signals.find((x) => x.symbol === selected);
  const selectedWatch = state?.watchlist.find((x) => x.symbol === selected);
  // 이슈 #152: news.enabled(health) 또는 sentiment.enabled가 false면 뉴스 UI를 아무것도 그리지 않는다.
  const newsUiEnabled = shouldRenderNewsUi(newsHealthEnabled, newsSentiment?.enabled);
  const marketNewsBadge = newsUiEnabled ? scoreBadge(newsSentiment?.market?.score) : null;
  // ── 이슈 #26/#88: 모드별 정렬 ──
  // active/shadow = v5 계열 정렬(v5 상태/추세 강도/추세 방향/진입 품질/종목명) 중 선택.
  // off·summary 부재는 v5 분석이 없으므로 선택지 없이 종목 알파벳순으로 고정한다.
  // v5 결측 행을 `?? 0`으로 0점 취급하지 않는다 — 비교 함수가 결측을 항상 마지막에 둔다.
  const structureMode = state?.structureSummary?.mode ?? null;
  const structureRows = new Map<string, StructureSummaryRow>();
  for (const row of state?.structureSummary?.symbols ?? [])
    if (row?.symbol) structureRows.set(row.symbol.toUpperCase(), row);
  const rowOf = (symbol: string): StructureSummaryRow =>
    structureRows.get(symbol.toUpperCase()) ?? { symbol };
  const liveSort = resolveSortKey(structureMode, liveSortChoice);
  const liveSortOptions = sortKeysForMode(structureMode);
  const pickLiveSort = (key: LiveSortKey) => {
    setLiveSortChoice(key);
    localStorage.setItem("astra-live-sort", key);
  };
  const ranked = [...(state?.signals || [])]
    .filter((item) => !item.stale)
    .sort((a, b) => {
      if (liveSortOptions.length === 0) return compareBySymbol(a, b);
      switch (liveSort) {
        case "trend":
          return compareByTrendStrength(rowOf(a.symbol), rowOf(b.symbol));
        case "direction":
          return compareByTrendDirection(rowOf(a.symbol), rowOf(b.symbol));
        case "quality":
          return compareByEntryQuality(rowOf(a.symbol), rowOf(b.symbol));
        case "symbol":
          return compareBySymbol(a, b);
        default:
          return compareByV5State(rowOf(a.symbol), rowOf(b.symbol));
      }
    });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!selected) return;
    mutate(() =>
      api(`/api/positions/${selected}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          entryPrice: Number(form.entryPrice),
          quantity: Number(form.quantity),
        }),
      }),
    );
  };
  return (
    <div className={`app ${sideOpen ? "" : "side-closed"}`}>
      <aside>
        <div className="brand">
          <div className="mark">
            <Activity size={17} />
          </div>
          <span>ASTRA</span>
          <small>MARKET SIGNALS</small>
        </div>
        <div className="watch-head">
          <span>관심종목</span>
          <span>{state?.watchlist.length || 0}</span>
        </div>
        <div className="search">
          <Search size={15} />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="검색 · AAPL 또는 애플"
          />
          {query && (
            <button onClick={() => setQuery("")} aria-label="검색어 지우기">
              <X size={14} />
            </button>
          )}
        </div>
        {query && (
          <div className="results">
            {searching ? (
              <div className="muted pad">검색 중…</div>
            ) : results.length ? (
              results.map((r) => (
                <button key={r.symbol} onClick={() => add(r)}>
                  <div>
                    <b>{r.symbol}</b>
                    <small>{r.name}</small>
                  </div>
                  <Plus size={15} />
                </button>
              ))
            ) : (
              <div className="muted pad">검색 결과가 없습니다</div>
            )}
          </div>
        )}
        <div className="watch-list">
          {state?.watchlist.map((w) => {
            const s = state.signals.find((v) => v.symbol === w.symbol);
            const newsScore = newsUiEnabled
              ? findSymbolScore(newsSentiment?.symbols ?? [], w.symbol)
              : null;
            const newsBadge = scoreBadge(newsScore?.score);
            // 이슈 #181: K3 폴링 최신값(선택 종목)이 없으면 structureSummary 캐시로 전 종목 배지를 채운다.
            const confluence = sidebarConfluenceScore(confluenceScore, w.symbol, rowOf(w.symbol));
            return (
              <div
                className={`watch-row ${selected === w.symbol ? "active" : ""}`}
                key={w.symbol}
              >
                <button
                  className="watch-select"
                  onClick={() => setSelected(w.symbol)}
                >
                  <WatchRowContent symbol={w.symbol} name={w.name}>
                    {newsBadge && (
                      <span
                        className={newsBadge.className}
                        title={scoreBadgeTitle(newsScore?.count, newsScore?.latestAt)}
                      >
                        {newsBadge.label}
                      </span>
                    )}
                    {confluence != null && (
                      <span
                        className={`confluence-mini-badge ${gaugeTone(confluence)}`}
                        title="컨플루언스 점수(관측 전용)"
                      >
                        {scoreText2(confluence)}
                      </span>
                    )}
                    {s && (
                      <span
                        className={(s.changePercent ?? 0) >= 0 ? "up" : "down"}
                      >
                        {percent(s.changePercent)}
                      </span>
                    )}
                  </WatchRowContent>
                </button>
                <button
                  className="delete"
                  aria-label={`${w.symbol} 관심종목 삭제`}
                  onClick={() =>
                    mutate(() =>
                      api(`/api/watchlist/${w.symbol}`, { method: "DELETE" }),
                    )
                  }
                >
                  <Trash2 size={14} />
                </button>
              </div>
            );
          })}
          {state && !state.watchlist.length && (
            <div className="empty-side">
              검색으로 관심종목을
              <br />
              추가해 주세요.
            </div>
          )}
        </div>
        <div className="side-footer">
          <div>
            <span
              className={`dot ${state?.connection.status === "connected" ? "ok" : ""}`}
            />
            {!state ? "연결 확인 중" : state.connection.message}
          </div>
        </div>
      </aside>
      <main>
        <header>
          <div className="header-title">
            <button
              className="side-toggle"
              aria-label={sideOpen ? "관심종목 탭 닫기" : "관심종목 탭 열기"}
              onClick={() => setSideOpen(!sideOpen)}
            >
              {sideOpen ? (
                <PanelLeftClose size={18} />
              ) : (
                <PanelLeftOpen size={18} />
              )}
            </button>
            <div>
              <h1>
                {view === "dash"
                  ? "시뮬레이션 대시보드"
                  : view === "replay"
                    ? "과거 검증"
                  : view === "structure"
                    ? "구조 분석 (v5)"
                    : "실시간 시그널"}
              </h1>
              {/* 이슈 #29: 대시보드/구조 화면 부제에서 구현 설명·v4 비교 문구를 뺐다. */}
              <p>
                {view === "dash"
                  ? "청산 실적 요약"
                  : view === "replay"
                    ? "기간 지정 가상 replay"
                  : view === "structure"
                    ? "종목별 추세 · 후보 · 계획"
                    : "미국 정규장 · 조건 기반 모니터링"}
              </p>
            </div>
          </div>
          <div className="header-actions">
            {marketNewsBadge && (
              <span
                className={marketNewsBadge.className}
                title={scoreBadgeTitle(newsSentiment?.market?.count, newsSentiment?.market?.latestAt)}
              >
                시장 분위기 {marketNewsBadge.label}
              </span>
            )}
            <FeeWarningBadge warnings={state?.warnings} />
            <div className={`market ${state?.market.isOpen ? "open" : ""}`}>
              <span />
              {state?.market.label || "시장 상태 확인 중"}
            </div>
            <button
              className={`theme ${view === "replay" ? "alert-toggle on" : ""}`}
              title={view === "replay" ? "실시간 시그널로 돌아가기" : "과거 검증"}
              onClick={() => setView(view === "replay" ? "live" : "replay")}
            >
              {view === "replay" ? <Activity size={18} /> : <History size={18} />}
            </button>
            <button
              className={`start ${state?.running ? "stop" : ""}`}
              disabled={busy || !state}
              onClick={() =>
                mutate(() =>
                  api(state?.running ? "/api/stop" : "/api/start", {
                    method: "POST",
                  }),
                )
              }
            >
              {state?.running ? (
                <>
                  <Square size={13} /> STOP
                </>
              ) : (
                <>
                  <Play size={14} fill="currentColor" /> START
                </>
              )}
            </button>
            <button
              className={`theme ${view === "structure" ? "structure-toggle on" : ""}`}
              title={view === "structure" ? "실시간 시그널로 돌아가기" : "구조 분석 (v5)"}
              onClick={() => setView(view === "structure" ? "live" : "structure")}
            >
              {view === "structure" ? <Activity size={18} /> : <Layers size={18} />}
            </button>
            <button
              className={`theme ${view === "dash" ? "alert-toggle on" : ""}`}
              title={
                view === "dash" ? "실시간 시그널로 돌아가기" : "시뮬레이션 대시보드"
              }
              onClick={() => setView(view === "dash" ? "live" : "dash")}
            >
              {view === "dash" ? (
                <Activity size={18} />
              ) : (
                <LayoutDashboard size={18} />
              )}
            </button>
            <button
              className={`theme alert-toggle ${alertsOn ? "on" : ""}`}
              title={
                alertsOn
                  ? "알림 켜짐 (브라우저 알림 + 소리) — active 모드: v5 이벤트 · off/shadow: 참고 셋업·돌파"
                  : "알림 꺼짐 — 누르면 켜집니다"
              }
              onClick={toggleAlerts}
            >
              {alertsOn ? <Bell size={18} /> : <BellOff size={18} />}
            </button>
          </div>
        </header>
        {notice && (
          <div className="notice">
            <Bell size={15} />
            <span>{notice}</span>
            <button onClick={() => setNotice("")} aria-label="안내 닫기">
              <X size={14} />
            </button>
          </div>
        )}
        {error && (
          <div className="error">
            <WifiOff size={16} />
            <span>{error}</span>
            <button onClick={() => load()}>다시 연결</button>
          </div>
        )}
        {!error && state?.connection.status === "error" && (
          <div className="error connection-error" role="alert">
            <WifiOff size={16} />
            <span>{state.connection.message}</span>
            <a
              href="https://developers.tossinvest.com/docs"
              target="_blank"
              rel="noreferrer"
            >
              Toss 인증·허용 IP 설정 보기
            </a>
          </div>
        )}
        {view === "dash" && <Dashboard />}
        {view === "replay" && <HistoricalReplayPanel />}
        {view === "structure" && (
          <StructurePanel
            watchlist={state?.watchlist ?? []}
            summary={state?.structureSummary ?? null}
            selected={selected}
            onSelect={setSelected}
            bars={state?.signals.find((s) => s.symbol === selected)?.bars ?? []}
          />
        )}
        <div hidden={view !== "live"}>
        <section className="status-strip">
          <div>
            <span>엔진</span>
            <b className={state?.running ? "green" : ""}>
              {state?.running ? "분석 중" : "대기"}
            </b>
          </div>
          <div>
            <span>데이터 모드</span>
            <b title={state?.transport?.message}>{transportLabel(state)}</b>
          </div>
          {state?.transport?.lastTickAt && (
            <div>
              <span>마지막 시세</span>
              <b>{time(state.transport.lastTickAt)}</b>
            </div>
          )}
          <div>
            <span>마지막 갱신</span>
            <b>{state ? time(state.updatedAt) : "—"}</b>
          </div>
          {!state?.market.isOpen && state?.market.nextOpen && (
            <div>
              <span>다음 개장</span>
              <b>{new Date(state.market.nextOpen).toLocaleString("ko-KR")}</b>
            </div>
          )}
        </section>
        <div className="workspace">
          <section className="ranking panel">
            <div className="panel-head">
              <div>
                <h2>시그널 순위</h2>
                <p>
                  {liveSortOptions.length === 0
                    ? "종목명 알파벳순 고정 — v5 분석 없음"
                    : liveSortDescription(liveSort)}
                </p>
              </div>
              {liveSortOptions.length > 1 ? (
                <div className="structure-sort live-sort">
                  {liveSortOptions.map((key) => (
                    <button
                      key={key}
                      className={liveSort === key ? "on" : ""}
                      onClick={() => pickLiveSort(key)}
                      title={liveSortTitle(key)}
                    >
                      {liveSortLabel(key)}
                    </button>
                  ))}
                </div>
              ) : (
                <BarChart3 size={18} />
              )}
            </div>
            {!state ? (
              <Loading />
            ) : !state.running ? (
              <Empty
                icon="play"
                title="분석이 멈춰 있습니다"
                text="START를 누르면 관심종목의 실시간 조건 분석을 시작합니다."
              />
            ) : !ranked.length ? (
              <Empty
                title={
                  state.watchlist.length
                    ? "시세를 기다리고 있습니다"
                    : "관심종목이 없습니다"
                }
                text={
                  state.watchlist.length
                    ? "정규장 상태와 데이터 연결을 확인하며 첫 분석 결과를 준비 중입니다."
                    : "왼쪽 검색창에서 관심종목을 추가해 주세요."
                }
              />
            ) : (
              <div className="rank-list">
                {structureMode === "off" && (
                  <p className="v5-off-note">구조 엔진 꺼짐 — v5 분석이 없어 종목명 알파벳순으로 표시합니다.</p>
                )}
                {ranked.map((s, i) => (
                  <button
                    key={s.symbol}
                    className={selected === s.symbol ? "selected" : ""}
                    onClick={() => setSelected(s.symbol)}
                  >
                    <span className="rank">
                      {String(i + 1).padStart(2, "0")}
                    </span>
                    <div className="ticker">
                      <b>{s.symbol}</b>
                    </div>
                    <div className="quote">
                      <b>{money(s.price)}</b>
                      <small
                        className={(s.changePercent ?? 0) >= 0 ? "up" : "down"}
                      >
                        {percent(s.changePercent)}
                      </small>
                    </div>
                    <ChevronRight size={15} />
                    <LiveStructureCells row={rowOf(s.symbol)} mode={structureMode} />
                  </button>
                ))}
              </div>
            )}
          </section>
          <section className="detail panel">
            {signal?.price != null ? (
              <>
                <div className="stock-head">
                  <div>
                    <h2>{signal.symbol}</h2>
                  </div>
                  <div className="big-price">
                    <b>{money(signal.price)}</b>
                    <span
                      title="정규장 시가 대비"
                      className={
                        (signal.changePercent ?? 0) >= 0 ? "up" : "down"
                      }
                    >
                      {percent(signal.changePercent)}
                    </span>
                  </div>
                </div>
                {signal.stale && (
                  <div className="stale">
                    <AlertTriangle size={14} /> 현재 시세가 지연되고 있습니다 ·{" "}
                    {time(signal.updatedAt)} 기준
                  </div>
                )}
                {(structureMode === "active" || structureMode === "shadow") && (
                  <div className="v5-detail-strip">
                    <LiveStructureCells row={rowOf(signal.symbol)} mode={structureMode} />
                    <button onClick={() => setView("structure")}>구조 분석(v5) 상세</button>
                  </div>
                )}
                <div className="chart">
                  <MiniChart
                    bars={signal.bars || []}
                    positive={(signal.changePercent ?? 0) >= 0}
                  />
                </div>
                <div className="indicators">
                  <Metric
                    label="RSI (14)"
                    value={signal.indicators?.rsi?.toFixed(1)}
                    tone={
                      signal.indicators?.rsi == null
                        ? undefined
                        : signal.indicators.rsi >= 70 ||
                            signal.indicators.rsi < 30
                          ? "down"
                          : signal.indicators.rsi >= 50
                            ? "up"
                            : undefined
                    }
                    help="50↑ 상승 모멘텀 · 70↑ 과매수 · 30↓ 과매도"
                  />
                  <Metric
                    label="EMA 9 / 21"
                    value={
                      signal.indicators?.emaFast == null ||
                      signal.indicators?.emaSlow == null
                        ? undefined
                        : `${signal.indicators.emaFast.toFixed(2)} / ${signal.indicators.emaSlow.toFixed(2)}`
                    }
                    tone={
                      signal.indicators?.emaFast == null ||
                      signal.indicators?.emaSlow == null
                        ? undefined
                        : signal.indicators.emaFast >= signal.indicators.emaSlow
                          ? "up"
                          : "down"
                    }
                    help="9선이 21선 위면 단기 상승 추세"
                  />
                  <Metric
                    label="VWAP"
                    value={money(signal.indicators?.vwap)}
                    tone={
                      signal.indicators?.vwap == null || signal.price == null
                        ? undefined
                        : signal.price >= signal.indicators.vwap
                          ? "up"
                          : "down"
                    }
                    help="현재가가 위면 매수 우위 (장중 평균 체결가)"
                  />
                  <Metric
                    label="상대 거래량"
                    value={
                      signal.indicators?.relativeVolume == null
                        ? undefined
                        : `${signal.indicators.relativeVolume.toFixed(2)}×`
                    }
                    tone={
                      signal.indicators?.relativeVolume == null ||
                      signal.indicators.relativeVolume < 1.3
                        ? undefined
                        : (signal.changePercent ?? 0) >= 0
                          ? "up"
                          : "down"
                    }
                    help="1.3× 이상이면 유의미 · 방향은 등락 기준"
                  />
                </div>
                <div className="position">
                  <div>
                    <h3>포지션 추적</h3>
                    {signal.position ? (
                      <PositionStatus
                        status={signal.position.status}
                        waiting={signal.stale || !state?.market.isOpen}
                      />
                    ) : (
                      <p>체결한 매수가와 수량을 직접 입력하세요.</p>
                    )}
                  </div>
                  {signal.position ? (
                    <div className="position-live">
                      <p className="position-basis-note">
                        목표·손절은 ATR·레벨 기준 자동 산정입니다(참고).
                      </p>
                      <div>
                        <small>진입가</small>
                        <b>{money(signal.position.entryPrice)}</b>
                      </div>
                      <div>
                        <small>목표가</small>
                        <b className="up">
                          {signal.position.target
                            ? money(signal.position.target)
                            : "산정 대기"}
                        </b>
                        {signal.position.targetBasis && (
                          <span className="basis">
                            {signal.position.targetBasis}
                          </span>
                        )}
                      </div>
                      <div>
                        <small>손절가</small>
                        <b className="down">
                          {signal.position.stop
                            ? money(signal.position.stop)
                            : "산정 대기"}
                        </b>
                        {signal.position.stopBasis && (
                          <span className="basis">
                            {signal.position.stopBasis}
                          </span>
                        )}
                      </div>
                      <div>
                        <small>실질 손익</small>
                        <b
                          className={
                            (signal.position.pnlPercent ?? 0) >= 0
                              ? "up"
                              : "down"
                          }
                        >
                          {percent(signal.position.pnlPercent)}
                        </b>
                        <span className="basis">왕복 수수료 0.2% 차감</span>
                      </div>
                      <button
                        onClick={() =>
                          mutate(() =>
                            api(`/api/positions/${signal.symbol}`, {
                              method: "DELETE",
                            }),
                          )
                        }
                      >
                        추적 종료
                      </button>
                    </div>
                  ) : (
                    <PositionForm
                      form={form}
                      setForm={updateForm}
                      submit={submit}
                      busy={busy}
                    />
                  )}
                </div>
              </>
            ) : selectedWatch ? (
              <PendingStock
                stock={selectedWatch}
                position={signal?.position}
                form={form}
                setForm={updateForm}
                submit={submit}
                busy={busy}
                onDelete={() =>
                  mutate(() =>
                    api(`/api/positions/${selectedWatch.symbol}`, {
                      method: "DELETE",
                    }),
                  )
                }
              />
            ) : (
              <Empty
                title="종목을 선택해 주세요"
                text="관심종목의 시그널과 지표가 이곳에 표시됩니다."
              />
            )}
          </section>
        </div>
        {selected && (
          <MetricsCard
            symbol={selected}
            metrics={metrics?.symbol === selected ? metrics : null}
            running={!!state?.running}
          />
        )}
        {selected && <LiquidityPanel key={selected} symbol={selected} />}
        {selected && (
          <ConfluencePanel
            key={selected}
            symbol={selected}
            onScore={(symbol, score) => setConfluenceScore({ symbol, score })}
          />
        )}
        {selected && newsUiEnabled && <NewsPanel key={selected} symbol={selected} />}
        </div>
        <footer>
          본 화면의 시그널은 기술적 조건 충족 점수이며 수익 확률이나 투자 권유가
          아닙니다.
          <span className="app-version">v{__ASTRA_VERSION__}</span>
        </footer>
      </main>
    </div>
  );
}
const kindLabel = (k: string) =>
  k === "SETUP"
    ? "진입 셋업"
    : k === "REBOUND"
      ? "과매도 반등"
      : k === "BREAKOUT"
        ? "돌파"
        : k === "PULLBACK"
          ? "눌림목"
          : k;
const simStatusLabel = (s: string) =>
  s === "OPEN"
    ? "진행 중"
    : s === "TARGET"
      ? "익절"
      : s === "STOP"
        ? "손절"
        : s === "CUT"
          ? "약세 청산"
          : s === "EOD"
            ? "장마감 청산"
            : s;
/** 이슈 #131: 대시보드 탭 전환. 시뮬 성과와 실매매 대조는 표본이 다르므로 한 화면에 섞지 않는다. */
function DashTabs({ tab, onChange }: { tab: "sim" | "real"; onChange: (next: "sim" | "real") => void }) {
  return (
    <div className="cohort-picker">
      <button className={`theme ${tab === "sim" ? "alert-toggle on" : ""}`} onClick={() => onChange("sim")}>
        시뮬 성과
      </button>
      <button className={`theme ${tab === "real" ? "alert-toggle on" : ""}`} onClick={() => onChange("real")}>
        실매매 대조
      </button>
    </div>
  );
}
function Dashboard() {
  const [data, setData] = useState<SimData | null>(null);
  const [tab, setTab] = useState<"sim" | "real">("sim");
  const [loadError, setLoadError] = useState("");
  useVisiblePolling(async () => {
    try {
      setData(await api<SimData>("/api/sim"));
      setLoadError("");
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : "시뮬레이션 보고서를 불러오지 못했습니다.");
    }
  }, 10000);
  const dt = (v: string | null | undefined) =>
    v
      ? new Date(v).toLocaleString("ko-KR", {
          month: "numeric",
          day: "numeric",
          hour: "2-digit",
          minute: "2-digit",
        })
      : "—";
  if (tab === "real")
    return (
      <div className="dash">
        <DashTabs tab={tab} onChange={setTab} />
        <RealVsV5Panel />
      </div>
    );
  if (!data) return loadError ? <div className="error"><WifiOff size={16} /><span>{loadError} 10초 후 다시 시도합니다.</span></div> : <Loading />;
  const s = data.summary;
  const analysis = data.analysis;
  const byKind = data.byKind;
  const trades = data.trades;
  return (
    <div className="dash">
      <DashTabs tab={tab} onChange={setTab} />
      <section className="panel metrics-panel">
        <div className="panel-head">
          <div>
            <h2>전체 성과 요약</h2>
          </div>
          <LayoutDashboard size={18} />
        </div>
        <div className="metrics-body">
          <div className="metrics-grid">
            <MetricTile label="총 시뮬 매매" value={String(s.total)} />
            <MetricTile label="진행 중" value={String(s.open)} />
            <MetricTile
              label="승률"
              value={s.winRate == null ? "—" : `${s.winRate}%`}
              tone={
                s.winRate == null ? undefined : s.winRate >= 50 ? "up" : "down"
              }
            />
            <MetricTile
              label="평균 손익"
              value={percent(s.avgPnl)}
              tone={
                s.avgPnl == null ? undefined : s.avgPnl >= 0 ? "up" : "down"
              }
            />
            <MetricTile
              label="건별 수익률 합계"
              value={percent(s.totalPnl)}
              tone={
                s.totalPnl == null ? undefined : s.totalPnl >= 0 ? "up" : "down"
              }
            />
            {!!s.estimatedExits && <MetricTile label="추정 청산" value={`${s.estimatedExits}건`} />}
          </div>
        </div>
      </section>
      {!!s.missingPnl && <div className="sample-warning">청산 {s.closed}건 중 손익이 없는 {s.missingPnl}건은 승률·평균·합계 계산에서 제외했습니다.</div>}
      {analysis?.sampleWarning && <div className="sample-warning">{analysis.sampleWarning}</div>}
      {analysis && (
        <section className="panel metrics-panel">
          <div className="panel-head">
            <div>
              <h2>자동 분석 · 청산 {analysis.closedCount}건 기준</h2>
              <p>수익 vs 손실 트레이드의 진입 조건 비교와 보완점</p>
            </div>
            <AlertTriangle size={18} />
          </div>
          <div className="metrics-body">
            <div className="metrics-grid">
              <MetricTile
                label="승률 / 목표"
                value={`${analysis.winRate}% / ${analysis.targetWinRate}%`}
                tone={
                  analysis.winRate >= analysis.targetWinRate
                    ? "up"
                    : "down"
                }
              />
              <MetricTile
                label="기대값 (평균 손익)"
                value={percent(analysis.expectancy)}
                tone={analysis.expectancy >= 0 ? "up" : "down"}
                help="승률과 함께 봐야 하는 값"
              />
              <MetricTile
                label="평균 익절 / 손절"
                value={`${percent(analysis.avgWin)} / ${percent(analysis.avgLoss)}`}
              />
              <MetricTile
                label="청산 분포"
                value={`익절 ${analysis.exitShare.target}%`}
                help={`손절 ${analysis.exitShare.stop}% · 약세 ${analysis.exitShare.cut}% · 장마감 ${analysis.exitShare.eod}%`}
              />
            </div>
            <div className="table-wrap">
              <table className="sim-table">
                <thead>
                  <tr>
                    <th>진입 조건 평균</th>
                    <th>VWAP 이격 σ</th>
                    <th>상대 거래량</th>
                    <th>매수 비중</th>
                    <th>RSI</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td className="up">수익 ({analysis.winnerProfile.count}건)</td>
                    <td>{analysis.winnerProfile.extSigma ?? "—"}</td>
                    <td>{analysis.winnerProfile.relVolume ?? "—"}</td>
                    <td>{analysis.winnerProfile.buyShare ?? "—"}</td>
                    <td>{analysis.winnerProfile.rsi ?? "—"}</td>
                  </tr>
                  <tr>
                    <td className="down">손실 ({analysis.loserProfile.count}건)</td>
                    <td>{analysis.loserProfile.extSigma ?? "—"}</td>
                    <td>{analysis.loserProfile.relVolume ?? "—"}</td>
                    <td>{analysis.loserProfile.buyShare ?? "—"}</td>
                    <td>{analysis.loserProfile.rsi ?? "—"}</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <ul className="insights">
              {analysis.insights.map((line, i) => (
                <li key={i}>{line}</li>
              ))}
            </ul>
          </div>
        </section>
      )}
      <section className="panel metrics-panel">
        <div className="panel-head">
          <div>
            <h2>신호별 성과</h2>
          </div>
        </div>
        <div className="table-wrap">
          <table className="sim-table">
            <thead>
              <tr>
                <th>신호</th>
                <th>매매</th>
                <th>진행 중</th>
                <th>승률</th>
                <th>평균 손익</th>
                <th>건별 수익률 합계</th>
              </tr>
            </thead>
            <tbody>
              {byKind.map((k) => (
                <tr key={k.kind}>
                  <td>{kindLabel(k.kind)}</td>
                  <td>{k.stats.total}</td>
                  <td>{k.stats.open}</td>
                  <td>
                    {k.stats.winRate == null ? "—" : `${k.stats.winRate}%`}
                  </td>
                  <td
                    className={
                      (k.stats.avgPnl ?? 0) >= 0 && k.stats.avgPnl != null
                        ? "up"
                        : k.stats.avgPnl != null
                          ? "down"
                          : ""
                    }
                  >
                    {percent(k.stats.avgPnl)}
                  </td>
                  <td
                    className={
                      (k.stats.totalPnl ?? 0) >= 0 && k.stats.totalPnl != null
                        ? "up"
                        : k.stats.totalPnl != null
                          ? "down"
                          : ""
                    }
                  >
                    {percent(k.stats.totalPnl)}
                  </td>
                </tr>
              ))}
              {!byKind.length && (
                <tr>
                  <td colSpan={6} className="muted">
                    아직 발동한 신호가 없습니다. 신호가 뜨면 자동으로 기록됩니다.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>
      <section className="panel metrics-panel">
        <div className="panel-head">
          <div>
            <h2>매매 기록</h2>
            <p>최근 200건 · 최신순</p>
          </div>
        </div>
        <div className="table-wrap">
          <table className="sim-table">
            <thead>
              <tr>
                <th>진입 시각</th>
                <th>종목</th>
                <th>진입가</th>
                <th>손절가</th>
                <th>목표가</th>
                <th>결과</th>
                <th>청산가</th>
                <th>손익</th>
              </tr>
            </thead>
            <tbody>
              {trades.map((t) => (
                <tr key={t.id}>
                  <td>{dt(t.enteredAt)}</td>
                  <td className="has-tip" title={`${kindLabel(t.kind)} · ${tradeEntryTooltip(t)}`}>
                    <b>{t.symbol}</b>
                    {t.structure && <small className="estimated-exit">v5 동결</small>}
                  </td>
                  <td>{money(t.entryPrice)}</td>
                  <td title={t.stopBasis || undefined}>{money(t.stop)}</td>
                  <td title={t.targetBasis || undefined}>{money(t.target)}</td>
                  <td>
                    <span className={`sim-status ${t.status.toLowerCase()}`}>
                      {simStatusLabel(t.status)}
                    </span>
                    {t.exitEstimated && <small className="estimated-exit">추정</small>}
                  </td>
                  <td>
                    {t.status === "OPEN" ? money(t.lastPrice) : money(t.exitPrice)}
                  </td>
                  <td
                    className={
                      t.pnlPercent == null
                        ? ""
                        : t.pnlPercent >= 0
                          ? "up"
                          : "down"
                    }
                  >
                    {t.status === "OPEN"
                      ? percent(
                          Math.round(
                            ((t.lastPrice / t.entryPrice - 1) * 100 - 0.2) * 100,
                          ) / 100,
                        )
                      : percent(t.pnlPercent)}
                  </td>
                </tr>
              ))}
              {!trades.length && (
                <tr>
                  <td colSpan={8} className="muted">
                    기록이 없습니다.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
function MetricsCard({
  symbol,
  metrics,
  running,
}: {
  symbol: string;
  metrics: Metrics | null;
  running: boolean;
}) {
  const d = metrics?.daily ?? null;
  const f = metrics?.flow ?? null;
  const d5 = metrics?.daily5m ?? null;
  const d10 = metrics?.daily10m ?? null;
  const f5 = metrics?.flow5m ?? null;
  const f10 = metrics?.flow10m ?? null;
  const past = (v5: string | null, v10: string | null) =>
    v5 == null && v10 == null
      ? undefined
      : `5분 전 ${v5 ?? "—"} · 10분 전 ${v10 ?? "—"}`;
  const ratio = (v: number | null | undefined) =>
    v == null ? "—" : `×${v.toFixed(2)}`;
  const optRatio = (v: number | null | undefined) =>
    v == null ? null : `×${v.toFixed(2)}`;
  const optPct = (v: number | null | undefined) =>
    v == null ? null : `${v >= 0 ? "+" : ""}${v.toFixed(2)}%`;
  const optInt = (v: number | null | undefined) =>
    v == null ? null : `${v.toFixed(0)}%`;
  const ratioTone = (v: number | null | undefined) =>
    v == null ? undefined : v >= 1.3 ? "up" : v <= 0.7 ? "down" : undefined;
  const pctTone = (v: number | null | undefined) =>
    v == null ? undefined : v >= 0 ? "up" : "down";
  const compact = (v: number | null | undefined) =>
    v == null
      ? "—"
      : new Intl.NumberFormat("en-US", {
          notation: "compact",
          maximumFractionDigits: 1,
        }).format(v);
  return (
    <section className="panel metrics-panel">
      <div className="panel-head">
        <div>
          <h2>매매 보조지표 · {symbol}</h2>
          <p>세션 경과를 보정한 거래량 배율 · 틱 룰 기반 체결강도</p>
        </div>
        <Gauge size={18} />
      </div>
      {!running ? (
        <div className="muted pad">START 후 미국 정규장 중에 계산됩니다.</div>
      ) : !d && !f ? (
        <div className="muted pad">
          {metrics && !metrics.marketOpen
            ? "미국 정규장 중에만 계산됩니다."
            : "데이터 수집 중…"}
        </div>
      ) : (
        <div className="metrics-body">
          <div className="metrics-grid">
            <MetricTile
              label="거래량 · 3일 대비"
              value={ratio(d?.volumeRatio3)}
              tone={ratioTone(d?.volumeRatio3)}
              help="과거 일평균 거래량을 세션 경과율로 선형 보정한 추정치"
              history={past(
                optRatio(d5?.volumeRatio3),
                optRatio(d10?.volumeRatio3),
              )}
            />
            <MetricTile
              label="거래량 · 5일 대비"
              value={ratio(d?.volumeRatio5)}
              tone={ratioTone(d?.volumeRatio5)}
              help="과거 일평균 거래량을 세션 경과율로 선형 보정한 추정치"
              history={past(
                optRatio(d5?.volumeRatio5),
                optRatio(d10?.volumeRatio5),
              )}
            />
            <MetricTile
              label="거래량 · 20일 대비"
              value={ratio(d?.volumeRatio20)}
              tone={ratioTone(d?.volumeRatio20)}
              help="과거 일평균 거래량을 세션 경과율로 선형 보정한 추정치"
              history={past(
                optRatio(d5?.volumeRatio20),
                optRatio(d10?.volumeRatio20),
              )}
            />
            <MetricTile
              label="체결강도"
              value={f?.strength == null ? "—" : `${f.strength.toFixed(0)}%`}
              tone={
                f?.strength == null
                  ? undefined
                  : f.strength >= 100
                    ? "up"
                    : "down"
              }
              help={`최근 ${f?.windowMinutes ?? 5}분 수집 표본의 매수÷매도 추정량 · 원천 ${flowSourceLabel(metrics?.flowSource)}`}
              history={past(optInt(f5?.strength), optInt(f10?.strength))}
            />
            <MetricTile
              label="체결강도 원천"
              value={flowSourceLabel(metrics?.flowSource)}
              help="실시간 틱이 60초 이상 끊기면 체결 내역(REST)으로 보정한다"
            />
            <MetricTile
              label="블록 체결"
              value={blockTradeLabel(metrics?.blockTradeCount)}
              help="최근 50건 중 수량이 중앙값의 10배를 넘는 체결 · 매수/매도 구분 없음(미검증 상수)"
            />
            <MetricTile
              label="매수 체결 비중"
              value={f?.buyShare == null ? "—" : `${f.buyShare.toFixed(0)}%`}
              tone={
                f?.buyShare == null
                  ? undefined
                  : f.buyShare >= 50
                    ? "up"
                    : "down"
              }
              help="틱 룰 분류 기준"
              history={past(optInt(f5?.buyShare), optInt(f10?.buyShare))}
            />
            <MetricTile
              label="20일 레인지 위치"
              value={
                d?.rangePosition20 == null
                  ? "—"
                  : `${d.rangePosition20.toFixed(0)}%`
              }
              tone={
                d?.rangePosition20 == null
                  ? undefined
                  : d.rangePosition20 >= 70
                    ? "up"
                    : d.rangePosition20 <= 30
                      ? "down"
                      : undefined
              }
              help="0% 저점 · 100% 고점 · 70%↑ 강세권"
              history={past(
                optInt(d5?.rangePosition20),
                optInt(d10?.rangePosition20),
              )}
            />
            <MetricTile
              label="시가 갭"
              value={percent(d?.gapPercent)}
              tone={pctTone(d?.gapPercent)}
              help="전일 종가 대비 시가"
            />
            <MetricTile
              label="전일 종가 대비"
              value={percent(d?.changeFromPrevClose)}
              tone={pctTone(d?.changeFromPrevClose)}
              history={past(
                optPct(d5?.changeFromPrevClose),
                optPct(d10?.changeFromPrevClose),
              )}
            />
            <MetricTile
              label="전일까지 MA5 이격"
              value={percent(d?.maGap5)}
              tone={pctTone(d?.maGap5)}
              help="5일 이동평균 대비"
              history={past(optPct(d5?.maGap5), optPct(d10?.maGap5))}
            />
            <MetricTile
              label="전일까지 MA20 이격"
              value={percent(d?.maGap20)}
              tone={pctTone(d?.maGap20)}
              help="20일 이동평균 대비"
              history={past(optPct(d5?.maGap20), optPct(d10?.maGap20))}
            />
            <MetricTile
              label="오늘 거래량"
              value={compact(d?.todayVolume)}
              help={
                d ? `세션 ${d.sessionElapsedPercent.toFixed(0)}% 경과` : undefined
              }
            />
            <MetricTile
              label="회전율"
              value={turnoverText(metrics?.turnoverPercent)}
              help="당일 누적 거래량 / 상장주식수"
            />
          </div>
          <p className="metrics-note">
            체결강도·매수 비중은 수집된 체결 표본을 업틱/다운틱으로 분류한
            값입니다(체결강도 100% 초과 = 매수 우위). 웹소켓 틱이 60초 이상
            끊기면 체결 내역(REST)으로 보정하며 원천을 함께 표시합니다. 블록
            체결은 수량만 보는 추정치라 매수/매도 방향을 알 수 없습니다.
            공매도 잔량 · 기관/외인
            수급 · 풋콜 비율 · 감마 데이터는 Toss Open API가 제공하지 않아
            표시하지 않습니다.
          </p>
        </div>
      )}
    </section>
  );
}
function MetricTile({
  label,
  value,
  help,
  tone,
  history,
}: {
  label: string;
  value: string;
  help?: string;
  tone?: "up" | "down";
  history?: string;
}) {
  return (
    <div className="metric-tile">
      <small>{label}</small>
      <b className={tone || ""}>{value}</b>
      {history && <span className="hist">{history}</span>}
      {help && <span>{help}</span>}
    </div>
  );
}
function Metric({
  label,
  value,
  help,
  tone,
}: {
  label: string;
  value?: string;
  help: string;
  tone?: "up" | "down";
}) {
  return (
    <div>
      <small>{label}</small>
      <b className={value ? tone || "" : ""}>{value || "—"}</b>
      <span>{help}</span>
    </div>
  );
}
function PositionForm({
  form,
  setForm,
  submit,
  busy,
}: {
  form: { entryPrice: string; quantity: string };
  setForm: (v: { entryPrice: string; quantity: string }) => void;
  submit: (e: FormEvent) => void;
  busy: boolean;
}) {
  return (
    <form onSubmit={submit}>
      <label>
        매수가
        <input
          type="number"
          required
          min="0.02"
          step="0.01"
          inputMode="decimal"
          value={form.entryPrice}
          onChange={(e) => setForm({ ...form, entryPrice: e.target.value })}
          placeholder="0.00"
        />
      </label>
      <label>
        수량
        <input
          type="number"
          required
          min="0.0001"
          step="any"
          inputMode="decimal"
          value={form.quantity}
          onChange={(e) => setForm({ ...form, quantity: e.target.value })}
          placeholder="0"
        />
      </label>
      <button disabled={busy}>추적 시작</button>
    </form>
  );
}
function PendingStock({
  stock,
  position,
  form,
  setForm,
  submit,
  busy,
  onDelete,
}: {
  stock: { symbol: string; name: string };
  position?: Position;
  form: { entryPrice: string; quantity: string };
  setForm: (v: { entryPrice: string; quantity: string }) => void;
  submit: (e: FormEvent) => void;
  busy: boolean;
  onDelete: () => void;
}) {
  return (
    <>
      <div className="stock-head">
        <div>
          <h2>{stock.symbol}</h2>
        </div>
        <div className="muted">시세 대기 중</div>
      </div>
      <div className="empty pending">
        <Activity size={21} />
        <b>분석 데이터가 아직 없습니다</b>
        <p>
          엔진 시작 후 유효한 실시간 시세가 도착하면 점수와 지표가 표시됩니다.
        </p>
      </div>
      <div className="position">
        <div>
          <h3>포지션 추적</h3>
          {position ? (
            <PositionStatus status={position.status} waiting />
          ) : (
            <p>
              지금 입력할 수 있습니다. 목표가와 손절가는 ATR 확보 후 산정됩니다.
            </p>
          )}
        </div>
        {position ? (
          <div className="position-live">
            <div>
              <small>진입가</small>
              <b>{money(position.entryPrice)}</b>
            </div>
            <div>
              <small>목표가</small>
              <b className="up">
                {position.target == null ? "산정 대기" : money(position.target)}
              </b>
              {position.targetBasis && (
                <span className="basis">{position.targetBasis}</span>
              )}
            </div>
            <div>
              <small>손절가</small>
              <b className="down">
                {position.stop == null ? "산정 대기" : money(position.stop)}
              </b>
              {position.stopBasis && (
                <span className="basis">{position.stopBasis}</span>
              )}
            </div>
            <div>
              <small>실질 손익</small>
              <b>시세 대기</b>
              <span className="basis">왕복 수수료 0.2% 차감</span>
            </div>
            <button onClick={onDelete}>추적 종료</button>
          </div>
        ) : (
          <PositionForm
            form={form}
            setForm={setForm}
            submit={submit}
            busy={busy}
          />
        )}
      </div>
    </>
  );
}
function PositionStatus({
  status,
  waiting = false,
}: {
  status: string;
  waiting?: boolean;
}) {
  const label = waiting
    ? "판단 대기"
    : status === "STOP"
      ? "손절가 도달"
      : status === "TARGET"
        ? "목표가 도달"
        : status === "HOLD"
          ? "보유 추적"
          : "시세 대기";
  const tone = waiting || status === "WAIT" ? "wait" : status.toLowerCase();
  return <span className={`position-status ${tone}`}>{label}</span>;
}
function Loading() {
  return (
    <div className="loading">
      <i />
      <i />
      <i />
    </div>
  );
}
function Empty({
  title,
  text,
  icon,
}: {
  title: string;
  text: string;
  icon?: string;
}) {
  return (
    <div className="empty">
      {icon === "play" ? <Play size={21} /> : <Wifi size={21} />}
      <b>{title}</b>
      <p>{text}</p>
    </div>
  );
}
