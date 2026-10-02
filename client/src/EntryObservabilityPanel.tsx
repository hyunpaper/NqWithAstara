import { AlertTriangle } from "lucide-react";
import {
  dataDelayLabel,
  entryDispositionLabel,
  entryReasonLabel,
  type EntryObservabilityReport,
  type EntryObservationHistoryReport,
  type EntryObservationHistoryWindow,
} from "./entryObservability";

const clock = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleString("ko-KR", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" }) : "—";

function WindowSummary({ window, title }: { window: EntryObservationHistoryWindow; title: string }) {
  return (
    <div className="metric-tile">
      <small>{title}</small>
      <b>
        후보 {window.candidates}건 · READY {window.ready}건 · 진입 {window.entered}건 · 거절 {window.rejected}건
      </b>
      <span>
        관측 {window.observations}건 · 종목 {window.symbols}개 · 파일 {window.files}개
        {window.corruptLines ? ` · 손상 줄 ${window.corruptLines}` : ""}
      </span>
      <span>
        차단 사유 Top:{" "}
        {window.topReasons.length
          ? window.topReasons.map((r) => `${entryReasonLabel(r.code)} ${r.count}`).join(" · ")
          : "없음"}
      </span>
    </div>
  );
}

export default function EntryObservabilityPanel({
  report,
  history,
}: {
  report: EntryObservabilityReport | null | undefined;
  history?: EntryObservationHistoryReport | null;
}) {
  if (!report && !history) return null;
  return (
    <section className="panel metrics-panel">
      <div className="panel-head">
        <div>
          <h2>진입 관측 — 현재 스냅샷(재기동 이후)</h2>
          {report && (
            <p>후보 {report.candidateCount}건 · 승인 {report.approvedCount}건 · 거절 {report.rejectedCount}건</p>
          )}
        </div>
        <AlertTriangle size={18} />
      </div>
      {report && (
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
      )}
      {history && (
        <div className="metrics-body" data-testid="entry-observation-history">
          <div className="panel-head">
            <div>
              <h2>누적 (관측 파일 기준)</h2>
              <p>
                화면 전환·새로고침·재기동과 무관하게 관측 파일에서 집계합니다. 현재 정책 {history.policyHash.slice(0, 8) || "—"} ·
                최근 {history.currentPolicy.files}일 파일
              </p>
            </div>
          </div>
          {history.warning && <div className="sample-warning">{history.warning}</div>}
          <div className="metrics-grid">
            <WindowSummary window={history.today} title={`오늘 누적 (${history.today.to})`} />
            <WindowSummary
              window={history.currentPolicy}
              title={`현재 정책 누적 (${history.currentPolicy.from} ~ ${history.currentPolicy.to})`}
            />
          </div>
          <div className="table-wrap">
            <table className="sim-table">
              <thead><tr><th>종목</th><th>셋업</th><th>상태</th><th>거절 사유</th><th>트리거 봉</th><th>마지막 관측</th></tr></thead>
              <tbody>
                {history.recentCandidates.map((row) => (
                  <tr key={row.eventId}>
                    <td><b>{row.symbol}</b></td>
                    <td>{row.kind}</td>
                    <td>{entryDispositionLabel(row.state)}</td>
                    <td>{row.rejectionCodes.length ? row.rejectionCodes.map(entryReasonLabel).join(" · ") : "—"}</td>
                    <td>{clock(row.triggerBarStart)}</td>
                    <td>{clock(row.lastObservedAt)}</td>
                  </tr>
                ))}
                {!history.recentCandidates.length && (
                  <tr><td colSpan={6} className="muted">관측 파일에 후보 기록이 없습니다.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </section>
  );
}
