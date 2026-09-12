import { useEffect, useState } from "react";
import { Gauge } from "lucide-react";
import { normalizeConfluenceResponse, type ConfluenceResponse } from "./confluenceTypes";
import { allWarmup, barOpacity, barWidthPercent, gaugeTone, scoreText2, techniqueLabel } from "./confluenceFormat";

type Props = {
  symbol: string;
  /** 사이드바 미니 배지가 추가 호출 없이 같은 폴링 결과를 쓸 수 있게 점수를 올려보낸다(§4). */
  onScore?: (symbol: string, score: number | null) => void;
};

/** 이슈 #168: 종목 상세 컨플루언스 게이지 + 기법별 막대. 선택 종목만 폴링한다(§4). */
export default function ConfluencePanel({ symbol, onScore }: Props) {
  const [data, setData] = useState<ConfluenceResponse | null>(null);

  useEffect(() => {
    setData(null);
    let active = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;

    const schedule = () => {
      if (timer) clearTimeout(timer);
      if (active && document.visibilityState === "visible") timer = setTimeout(run, 15000);
    };
    const run = async () => {
      if (!active || inFlight || document.visibilityState !== "visible") return;
      inFlight = true;
      request = new AbortController();
      try {
        const response = await fetch(`/api/confluence/${encodeURIComponent(symbol)}`, {
          signal: request.signal,
        });
        if (!response.ok) throw new Error(`컨플루언스 조회 실패 (${response.status})`);
        const next = normalizeConfluenceResponse(await response.json());
        if (active && next.symbol.toUpperCase() === symbol.toUpperCase()) {
          setData(next);
          onScore?.(symbol, next.status === "ready" ? next.score : null);
        }
      } catch {
        /* 실패는 조용히 무시하고 이전 값을 유지한다(§4) */
      } finally {
        inFlight = false;
        request = undefined;
        schedule();
      }
    };
    const visibility = () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
      if (document.visibilityState === "visible" && !inFlight) void run();
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

  const warmedUp = data ? allWarmup(data.status, data.techniques) : true;
  const tone = gaugeTone(data?.score);

  return (
    <section className="panel confluence-panel">
      <div className="panel-head">
        <div>
          <h2>{symbol} 컨플루언스</h2>
          <p>기법 1군 10개 합산 · 관측 전용(진입 판단에 쓰지 않음)</p>
        </div>
        <Gauge size={18} />
      </div>
      {!data ? (
        <div className="confluence-state">불러오는 중…</div>
      ) : warmedUp ? (
        <div className="confluence-state">워밍업 중</div>
      ) : (
        <>
          <div className="confluence-gauge">
            <div className="confluence-gauge-track">
              <div
                className={`confluence-gauge-fill ${tone}`}
                style={{ width: `${((data.score ?? 0) + 1) * 50}%` }}
              />
              <div className="confluence-gauge-mid" />
            </div>
            <b className={`confluence-score ${tone}`}>{scoreText2(data.score)}</b>
          </div>
          <ul className="confluence-techniques">
            {data.techniques.map((t) => {
              const barTone = gaugeTone(t.score);
              const width = barWidthPercent(t.score);
              const side = (t.score ?? 0) >= 0 ? "right" : "left";
              return (
                <li key={t.name} className={t.warmup ? "warmup" : ""}>
                  <span className="confluence-technique-name">{techniqueLabel(t.name)}</span>
                  <div className="confluence-technique-track">
                    <div
                      className={`confluence-technique-fill ${side} ${barTone} ${t.warmup ? "dashed" : ""}`}
                      style={{ width: `${width}%`, opacity: barOpacity(t.confidence) }}
                    />
                  </div>
                  <span className="confluence-technique-score">
                    {t.warmup ? "워밍업" : scoreText2(t.score)}
                  </span>
                </li>
              );
            })}
          </ul>
        </>
      )}
      {data && (
        <p className="confluence-meta">
          워밍업 {data.warmupCount}개 · 가중치 {data.weightsVersion ?? "—"}
        </p>
      )}
    </section>
  );
}
