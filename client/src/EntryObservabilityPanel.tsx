import { AlertTriangle } from "lucide-react";
import {
  dataDelayLabel,
  entryDispositionLabel,
  entryReasonLabel,
  type EntryObservabilityReport,
} from "./entryObservability";

export default function EntryObservabilityPanel({ report }: { report: EntryObservabilityReport | null | undefined }) {
  if (!report) return null;
  return (
    <section className="panel metrics-panel">
      <div className="panel-head">
        <div>
          <h2>진입 관측</h2>
          <p>후보 {report.candidateCount}건 · 승인 {report.approvedCount}건 · 거절 {report.rejectedCount}건</p>
        </div>
        <AlertTriangle size={18} />
      </div>
      <div className="table-wrap">
        <table className="sim-table">
          <thead><tr><th>종목</th><th>최종 상태</th><th>후보</th><th>최초 gate</th><th>중복 사유</th><th>데이터 지연</th></tr></thead>
          <tbody>
            {report.symbols.map((row) => (
              <tr key={row.symbol}>
                <td><b>{row.symbol}</b></td>
                <td>{entryDispositionLabel(row.finalDisposition)}</td>
                <td>{row.candidateCount}건</td>
                <td title={row.rejectionReasons.join(" · ") || undefined}>{entryReasonLabel(row.firstGateReason)}</td>
                <td>{row.duplicateReasons.length ? row.duplicateReasons.map(entryReasonLabel).join(" · ") : "—"}</td>
                <td>{dataDelayLabel(row.dataDelaySeconds)}</td>
              </tr>
            ))}
            {!report.symbols.length && <tr><td colSpan={6} className="muted">아직 평가된 종목이 없습니다.</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}
