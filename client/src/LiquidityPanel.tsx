import { useEffect, useState } from "react";
import { BarChart3 } from "lucide-react";

type Liquidity = {
  bestBid: number;
  bestAsk: number;
  spread: number;
  spreadBps: number;
  displayedBidVolume: number;
  displayedAskVolume: number;
  displayedDepthImbalancePercent: number | null;
};
type Response = {
  symbol: string;
  status: "ready" | "stopped" | "marketClosed" | "invalid" | "unavailable";
  liquidity: Liquidity | null;
  observedAt: string | null;
  updatedAt: string;
  source: string;
  depthLabel: string;
  message: string;
};

const usd = (value: number | null | undefined) => value == null ? "—" : `$${value.toFixed(2)}`;
const usdSpread = (value: number | null | undefined) => value == null ? "—" : `$${value.toFixed(4)}`;
const shares = (value: number | null | undefined) => value == null ? "—" : new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 }).format(value);
const observed = (value: string | null) => {
  if (!value) return "관측 시각 없음";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "관측 시각 없음";
  const age = Math.max(0, Math.round((Date.now() - date.getTime()) / 1000));
  return `${date.toLocaleTimeString("ko-KR")} · ${age}초 전`;
};

export default function LiquidityPanel({ symbol }: { symbol: string }) {
  const [data, setData] = useState<Response | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    setData(null);
    setError("");
    let active = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;

    const schedule = () => {
      if (timer) clearTimeout(timer);
      if (active && document.visibilityState === "visible") timer = setTimeout(run, 5000);
    };
    const run = async () => {
      if (!active || inFlight || document.visibilityState !== "visible") return;
      inFlight = true;
      request = new AbortController();
      try {
        const response = await fetch(`/api/liquidity/${encodeURIComponent(symbol)}`, { signal: request.signal });
        if (!response.ok) {
          let message = `호가 조회 실패 (${response.status})`;
          try { const body = await response.json(); message = body.message || body.detail || body.title || message; } catch { /* non-JSON */ }
          throw new Error(message);
        }
        const next = await response.json() as Response;
        if (active && next.symbol === symbol) { setData(next); setError(""); }
      } catch (e) {
        if (active && (e as Error).name !== "AbortError") {
          setData(null);
          setError(e instanceof Error ? e.message : "호가를 불러오지 못했습니다.");
        }
      } finally {
        inFlight = false;
        request = undefined;
        schedule();
      }
    };
    const visibility = () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
      if (document.visibilityState === "hidden") {
        request?.abort();
        setData(null);
      }
      else if (!inFlight) void run();
    };
    void run();
    document.addEventListener("visibilitychange", visibility);
    return () => {
      active = false;
      if (timer) clearTimeout(timer);
      request?.abort();
      document.removeEventListener("visibilitychange", visibility);
    };
  }, [symbol]);

  const book = data?.status === "ready" ? data.liquidity : null;
  return (
    <section className="panel liquidity-panel">
      <div className="panel-head">
        <div><h2>{symbol} 호가 유동성</h2><p>표시 호가 스냅샷 · 매매 체결강도와 다른 지표</p></div>
        <BarChart3 size={18} />
      </div>
      {error ? <div className="liquidity-state bad">{error} · 5초 후 재시도</div> : !data ? <div className="liquidity-state">호가 불러오는 중…</div> : !book ? <div className="liquidity-state">{data.message}</div> : (
        <div className="liquidity-body">
          <div><small>최우선 매수호가</small><b>{usd(book.bestBid)}</b></div>
          <div><small>최우선 매도호가</small><b>{usd(book.bestAsk)}</b></div>
          <div><small>스프레드</small><b>{usdSpread(book.spread)}</b><span>{book.spreadBps.toFixed(2)} bps · {(book.spreadBps / 100).toFixed(4)}%</span></div>
          <div><small>표시 매수 잔량</small><b>{shares(book.displayedBidVolume)}</b><span>주</span></div>
          <div><small>표시 매도 잔량</small><b>{shares(book.displayedAskVolume)}</b><span>주</span></div>
          <div title="취소 주문과 화면 밖 호가는 포함되지 않습니다. 실제 매수·매도 체결 흐름을 뜻하지 않습니다."><small>표시 잔량 차이</small><b>{book.displayedDepthImbalancePercent == null ? "—" : `${book.displayedDepthImbalancePercent > 0 ? "+" : ""}${book.displayedDepthImbalancePercent.toFixed(1)}%`}</b><span>호가창 표본 추정</span></div>
        </div>
      )}
      {data && <p className="liquidity-meta">{data.source} · {observed(data.observedAt)} · {data.depthLabel}</p>}
    </section>
  );
}
