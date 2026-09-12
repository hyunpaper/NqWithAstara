import { FormEvent, useEffect, useState } from "react";
import { HistoricalReplayRun, replayStatusLabel, replayValue } from "./replayTypes";

const iso = (date: Date) => date.toISOString().slice(0, 10);

export default function HistoricalReplayPanel() {
  const today = new Date();
  const fourWeeksAgo = new Date(today);
  fourWeeksAgo.setDate(today.getDate() - 27);
  const [from, setFrom] = useState(iso(fourWeeksAgo));
  const [to, setTo] = useState(iso(today));
  const [run, setRun] = useState<HistoricalReplayRun | null>(null);
  const [message, setMessage] = useState("");

  async function load(url = "/api/replays/latest") {
    const response = await fetch(url);
    if (response.status === 404) return;
    if (!response.ok) throw new Error("과거 replay 조회에 실패했습니다.");
    setRun(await response.json());
  }

  useEffect(() => { load().catch((error) => setMessage(error.message)); }, []);
  useEffect(() => {
    if (!run || (run.status !== "queued" && run.status !== "running")) return;
    const timer = window.setInterval(() => load(`/api/replays/${run.id}`).catch((error) =>
      setMessage(error.message)), 1500);
    return () => window.clearInterval(timer);
  }, [run?.id, run?.status]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setMessage("");
    const response = await fetch("/api/replays", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ from, to }),
    });
    const body = await response.json();
    if (!response.ok) { setMessage(body.message ?? "실행 요청에 실패했습니다."); return; }
    setRun(body);
  }

  const busy = run?.status === "queued" || run?.status === "running";
  return <section className="replay-view">
    <div className="panel replay-control">
      <div className="panel-head"><h3>기간 지정</h3><span>현재 관심종목 전체 · QQQ 벤치마크</span></div>
      <form onSubmit={submit}>
        <label>시작일<input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} /></label>
        <label>종료일<input type="date" value={to} min={from} max={iso(today)} onChange={(e) => setTo(e.target.value)} /></label>
        <button disabled={busy}>{busy ? "실행 중" : "과거 replay 실행"}</button>
      </form>
      <p className="replay-notice">과거 replay 가상 결과이며 실제 체결 성과가 아닙니다. 운영 시뮬레이션 거래와 분리해 저장합니다.</p>
      {message && <p className="replay-error">{message}</p>}
    </div>
    {run && <>
      <div className="panel replay-summary">
        <div className="panel-head"><h3>작업 상태</h3><b>{replayStatusLabel(run.status)}</b></div>
        <div className="replay-meta"><span>{run.from} ~ {run.to}</span><span>대상 {run.watchlist.length}종목</span><span>벤치마크 {run.benchmark}</span><span>출처 {run.source}</span></div>
        {run.completedAt && <p>완료: {new Date(run.completedAt).toLocaleString()}</p>}
        {run.failureReason && <p className="replay-error">{run.failureReason}</p>}
      </div>
      {run.aggregate && <div className="panel">
        <div className="panel-head"><h3>전체 합계</h3><span>{run.notice}</span></div>
        <div className="replay-totals"><b>신호 {run.aggregate.signals.toLocaleString()}</b><span>가상 진입 {replayValue(run.aggregate.virtualEntries)}</span><span>승/패 {replayValue(run.aggregate.wins)} / {replayValue(run.aggregate.losses)}</span><span>손익 {replayValue(run.aggregate.pnlPercent, "%")}</span></div>
      </div>}
      {run.symbols.length > 0 && <div className="panel table-wrap">
        <table className="sim-table"><thead><tr><th>종목</th><th>신호</th><th>가상 진입</th><th>승/패</th><th>손익</th><th>평균 보유</th><th>STOP/TARGET/EOD</th></tr></thead>
          <tbody>{run.symbols.map((row) => <tr key={row.symbol}><td><b>{row.symbol}</b><small title={row.unavailableReason ?? ""}>{row.tradeReplayStatus === "partial" ? "봉 기반" : row.tradeReplayStatus}</small></td><td>{row.signals}</td><td title={row.unavailableReason ?? ""}>{replayValue(row.virtualEntries)}</td><td>{replayValue(row.wins)} / {replayValue(row.losses)}</td><td>{replayValue(row.pnlPercent, "%")}</td><td>{replayValue(row.averageHoldingMinutes, "분")}</td><td>{row.exits ? `${row.exits.stop}/${row.exits.target}/${row.exits.eod}` : "unavailable"}</td></tr>)}</tbody>
        </table>
      </div>}
    </>}
  </section>;
}
