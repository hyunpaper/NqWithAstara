// 이슈 #27 — 시뮬레이션 대시보드의 v5 구조 코호트 섹션.
// 서버(Domain SimulationCohorts)가 집계한 결과만 그린다 — 여기서 집계·추정을 하지 않는다.
// 원칙: 승률에는 항상 분모(표본)를 붙이고, 미수집(컨텍스트 누락·EntryQuality 없음·비용 결측)은
// 0이나 "검증 완료"처럼 표기하지 않으며, 계획 netR(R 단위)과 실현 손익(%)을 절대 섞지 않는다.
import { Layers } from "lucide-react";
import type { StructureCohortReport, SimulationCohortGroup, CohortStats } from "./dashboardTypes";
import { plannedNetRText, winRateText } from "./dashboardTypes";

const pct = (v: number | null | undefined) =>
  v == null ? "—" : `${v >= 0 ? "+" : ""}${v.toFixed(2)}%`;

function Tile({ label, value, help, tone }: { label: string; value: string; help?: string; tone?: "up" | "down" }) {
  return (
    <div className="metric-tile">
      <small>{label}</small>
      <b className={tone || ""}>{value}</b>
      {help && <span>{help}</span>}
    </div>
  );
}

function CohortTable({ group }: { group: SimulationCohortGroup }) {
  return (
    <div className="table-wrap">
      <table className="sim-table">
        <caption className="muted" style={{ captionSide: "top", textAlign: "left", padding: "6px 2px" }}>
          {group.title}
        </caption>
        <thead>
          <tr>
            <th>코호트</th>
            <th>매매</th>
            <th>진행 중</th>
            <th>청산(손익 유효)</th>
            <th>승률 (표본)</th>
            <th>평균 손익 (비용 차감)</th>
            <th>계획 netR (계획값)</th>
          </tr>
        </thead>
        <tbody>
          {group.cohorts.map((c) => (
            <tr key={c.key} className={c.collected ? "" : "muted"}>
              <td>
                {c.label}
                {!c.collected && <small className="estimated-exit">미수집</small>}
              </td>
              <td>{c.stats.total}</td>
              <td>{c.stats.open}</td>
              <td>
                {c.stats.closed}({c.stats.validClosed})
                {c.stats.estimatedExits > 0 && (
                  <small className="estimated-exit">추정 청산 {c.stats.estimatedExits}건 포함</small>
                )}
              </td>
              <td>{winRateText(c.stats)}</td>
              <td
                className={
                  c.stats.avgPnl == null ? "" : c.stats.avgPnl >= 0 ? "up" : "down"
                }
              >
                {c.stats.avgPnl == null ? "표본 없음" : pct(c.stats.avgPnl)}
              </td>
              <td>{plannedNetRText(c.stats)}</td>
            </tr>
          ))}
          {!group.cohorts.length && (
            <tr>
              <td colSpan={7} className="muted">
                해당 축의 v5 거래가 없습니다.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

export default function SimStructurePanel({ report }: { report: StructureCohortReport | null | undefined }) {
  const s: CohortStats | null = report?.v5Stats ?? null;
  return (
    <section className="panel metrics-panel">
      <div className="panel-head">
        <div>
          <h2>v5 구조 거래 · 동결 근거 코호트</h2>
          <p>
            진입 시점 FrozenStructureContext 기준 · 현재 재계산 값으로 채우지 않음 · 승률 분모 =
            손익 유효 청산 건 · 평균 손익은 왕복 수수료 0.2% 차감 실현값, 계획 netR은 동결 비용 모델의
            계획값(EOD 등 추정 청산 포함 여부는 각 행에 표기)
          </p>
        </div>
        <Layers size={18} />
      </div>
      {!report || !s || s.total === 0 ? (
        <div className="muted pad">
          v5 구조 거래가 아직 없습니다. 구조 엔진이 active 모드로 진입을 기록하면 이곳에 동결 근거
          기준 코호트가 표시됩니다.
        </div>
      ) : (
        <div className="metrics-body">
          <div className="metrics-grid">
            <Tile label="v5 매매" value={String(s.total)} />
            <Tile label="진행 중 (OPEN)" value={String(s.open)} />
            <Tile
              label="완료 거래"
              value={String(s.closed)}
              help={
                s.missingPnl > 0
                  ? `손익 유효 ${s.validClosed}건 · 손익 결측 ${s.missingPnl}건 제외`
                  : `손익 유효 ${s.validClosed}건`
              }
            />
            <Tile
              label="승률"
              value={winRateText(s)}
              tone={s.winRate == null ? undefined : s.winRate >= 50 ? "up" : "down"}
              help={`분모 = 손익 유효 청산 ${s.validClosed}건${s.estimatedExits > 0 ? ` · 추정 청산 ${s.estimatedExits}건 포함` : ""}`}
            />
            <Tile
              label="평균 손익 (실현)"
              value={s.avgPnl == null ? "표본 없음" : pct(s.avgPnl)}
              tone={s.avgPnl == null ? undefined : s.avgPnl >= 0 ? "up" : "down"}
              help="왕복 수수료 0.2% 차감 후"
            />
            <Tile
              label="계획 netR 평균"
              value={plannedNetRText(s)}
              help="동결 계획의 비용 모델 기준 계획값 — 실현 손익이 아닙니다"
            />
          </div>
          {s.validClosed < 10 && s.total > 0 && (
            <div className="sample-warning">
              손익 유효 청산 {s.validClosed}건의 소표본입니다. 현재 수치는 잠정 관찰값이며 규칙 검증
              결과가 아닙니다.
            </div>
          )}
          {(report.contextMissing ?? 0) > 0 && (
            <div className="sample-warning">
              동결 컨텍스트가 없는 v5 거래 {report.contextMissing}건은 재계산으로 채우지 않고 각
              표에서 &ldquo;동결 컨텍스트 누락 (미수집)&rdquo;으로 구분합니다.
            </div>
          )}
          {report.groups.map((g) => (
            <CohortTable key={g.dimension} group={g} />
          ))}
        </div>
      )}
    </section>
  );
}
