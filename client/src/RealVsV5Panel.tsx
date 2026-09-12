// 이슈 #131 — 대시보드 "실매매 대조" 탭.
// 서버(/api/validation/real-vs-v5)가 낸 대조 결과만 그린다 — 여기서 매칭·집계를 다시 하지 않는다.
// 실체결이 수집되지 않은 날은 0건 성과가 아니라 "수집된 실체결 없음"으로 적는다.
import { useCallback, useEffect, useState } from "react";
import { ClipboardList } from "lucide-react";
import {
  limitationLabel,
  summaryTiles,
  tableRows,
  type RealVsV5Data,
} from "./realVsV5";

const dt = (value: string) =>
  new Date(value).toLocaleString("ko-KR", {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });

export default function RealVsV5Panel() {
  const [data, setData] = useState<RealVsV5Data | null>(null);
  const [error, setError] = useState("");
  const load = useCallback(async () => {
    try {
      const response = await fetch("/api/validation/real-vs-v5");
      if (!response.ok) throw new Error(`요청 실패 (${response.status})`);
      setData((await response.json()) as RealVsV5Data);
      setError("");
    } catch (e) {
      setError(e instanceof Error ? e.message : "실매매 대조를 불러오지 못했습니다.");
    }
  }, []);
  useEffect(() => {
    void load();
  }, [load]);

  if (error) return <div className="error">{error}</div>;
  if (!data) return <div className="loading"><i /><i /><i /></div>;

  const rows = tableRows(data.report.rows, dt);
  return (
    <section className="panel metrics-panel">
      <div className="panel-head">
        <div>
          <h2>실매매 대조 · {data.report.tradingDate}</h2>
          <p>
            실계좌 체결(읽기 전용)과 v5 관측을 ±{data.report.windowMinutes}분 창으로 맞춘 결과입니다.
          </p>
        </div>
        <ClipboardList size={18} />
      </div>
      <div className="metrics-body">
        <div className="metrics-grid">
          {summaryTiles(data).map((tile) => (
            <div className="metric-tile" key={tile.label}>
              <small>{tile.label}</small>
              <b>{tile.value}</b>
              <span>{tile.help}</span>
            </div>
          ))}
        </div>
        {data.limitations.map((code) => (
          <div className="sample-warning" key={code}>
            {limitationLabel(code)}
          </div>
        ))}
        {data.report.topUserOnlyRejections.length > 0 && (
          <ul className="insights">
            {data.report.topUserOnlyRejections.map((x) => (
              <li key={x.code}>
                사용자 단독 진입의 v5 거절 사유 {x.code} · {x.count}건
              </li>
            ))}
          </ul>
        )}
        {rows.length === 0 ? (
          <div className="sample-warning">수집된 실체결 없음</div>
        ) : (
          <div className="table-wrap">
            <table className="sim-table">
              <thead>
                <tr>
                  <th>시각</th>
                  <th>심볼</th>
                  <th>구분</th>
                  <th>체결가</th>
                  <th>v5 상태</th>
                  <th>괴리</th>
                  <th>비고</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.key} className={row.muted ? "muted" : ""}>
                    <td>{row.at}</td>
                    <td>{row.symbol}</td>
                    <td>{row.side}</td>
                    <td>{row.price}</td>
                    <td>{row.state}</td>
                    <td>{row.deviation}</td>
                    <td>{row.note}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}
