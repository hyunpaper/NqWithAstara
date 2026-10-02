import { AlertTriangle } from "lucide-react";
import type {
  EntryObservabilityReport,
  EntryObservationCandidateRow,
  EntryObservationHistoryReport,
} from "./entryObservability";

const clock = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleString("ko-KR", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—";

const ENTERED = "ENTERED";

type EnteredRow = Pick<EntryObservationCandidateRow, "symbol" | "kind" | "triggerBarStart" | "lastObservedAt"> & { key: string };

function enteredRows(
  report: EntryObservabilityReport | null | undefined,
  history: EntryObservationHistoryReport | null | undefined,
): EnteredRow[] {
  if (history) {
    return history.recentCandidates
      .filter((row) => row.state === ENTERED)
      .map((row) => ({ key: row.eventId, symbol: row.symbol, kind: row.kind, triggerBarStart: row.triggerBarStart, lastObservedAt: row.lastObservedAt }));
  }
  return (report?.symbols ?? [])
    .filter((row) => row.finalDisposition === ENTERED)
    .map((row) => ({ key: row.symbol, symbol: row.symbol, kind: "—", triggerBarStart: null, lastObservedAt: row.evaluatedAt }));
}

export default function EntryObservabilityPanel({
  report,
  history,
}: {
  report: EntryObservabilityReport | null | undefined;
  history?: EntryObservationHistoryReport | null;
}) {
  if (!report && !history) return null;
  const rows = enteredRows(report, history);
  const count = history ? history.today.entered : rows.length;
  return (
    <section className="panel metrics-panel" data-testid="entry-observability">
      <div className="panel-head">
        <div>
          <h2>진입 관측</h2>
          <p>{count ? `오늘 진입 ${count}건` : "오늘 진입 없음"}</p>
        </div>
        <AlertTriangle size={18} />
      </div>
      {history?.warning && <div className="sample-warning">{history.warning}</div>}
      {rows.length > 0 && (
        <div className="table-wrap">
          <table className="sim-table">
            <thead><tr><th>종목</th><th>셋업</th><th>트리거 봉</th><th>진입 확인</th></tr></thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.key}>
                  <td><b>{row.symbol}</b></td>
                  <td>{row.kind}</td>
                  <td>{clock(row.triggerBarStart)}</td>
                  <td>{clock(row.lastObservedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
