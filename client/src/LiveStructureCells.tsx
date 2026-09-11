// 이슈 #26 — 라이브 목록 행의 v5 평가 열(승인 설계안 §2).
//
// 데이터 원천은 `/api/state`의 additive structureSummary 행 하나뿐이다 — 화면에서 새 계산을 하지 않는다.
// 오인 방지 제약(스냅샷 테스트로 고정):
// - SignedTrend는 전용 렌더러(부호+상태)로만 표시하고 v4 scoreStyle·`/100`·매수/매도 문구에 연결하지 않는다.
// - EntryQuality는 대표 후보 없으면 "미평가" 고정이며 %·승률·확률·성공 단어를 쓰지 않는다.
// - 결측(워밍업·off·정규장 외)은 상태 문구로 보여주고 0점·회색 정상으로 위장하지 않는다.
import {
  StructureSummaryRow,
  arr,
  candidateGroup,
  candidateStateLabel,
  codeTexts,
  entryQualityText,
  setupKindLabel,
  signedTrendText,
  statusLabel,
} from "./structureTypes";

type Props = {
  /** 이 종목의 structureSummary 행. 없으면(구버전 서버·미관측) 결측으로 표시한다. */
  row: StructureSummaryRow | null | undefined;
  /** structureSummary.mode. null이면(summary 부재) v5 열 자체를 그리지 않는다(§3 fallback). */
  mode: string | null | undefined;
};

export default function LiveStructureCells({ row, mode }: Props) {
  // summary 부재(구버전 서버)와 off 모드에서는 행 단위 v5 열을 그리지 않는다 —
  // off는 목록 상단의 "구조 엔진 꺼짐" 한 줄이 대신한다(§3).
  if (mode !== "active" && mode !== "shadow") return null;

  const warnings = arr(row?.warnings);
  const available = row?.status === "available";
  return (
    <div className="v5-cells">
      <div className="v5-cell">
        <small>v5 추세</small>
        <b>{signedTrendText(row?.trendState, row?.signedTrend)}</b>
      </div>
      <div className="v5-cell" title="후보 간 비교용 순위 지표 (0~100)">
        <small>진입 품질</small>
        <b>{entryQualityText(row?.preferredCandidateId, row?.entryQuality)}</b>
        {row?.preferredKind ? (
          <span className="v5-kind">{setupKindLabel(row.preferredKind)}</span>
        ) : null}
      </div>
      <div className="v5-cell">
        <span className={`se-state ${candidateGroup(row?.candidateState)}`}>
          {candidateStateLabel(row?.candidateState)}
        </span>
        {!available && <small>{statusLabel(row?.status)}</small>}
      </div>
      {warnings.length > 0 && (
        <span className="v5-warnings" title={codeTexts(warnings).join("\n")}>
          ⚠ {warnings.length}
        </span>
      )}
    </div>
  );
}
