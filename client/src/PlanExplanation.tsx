import { useMemo } from "react";
import {
  StructureAnalysis,
  StructureCandidate,
  StructureZone,
  arr,
  candidateGroup,
  candidateStateLabel,
  clock,
  codeText,
  codeTexts,
  knownCodeTexts,
  missingComponentTexts,
  num1,
  num2,
  num3,
  price,
  priceRange,
  qualityComponentLabel,
  setupKindLabel,
  sourceKindLabel,
  sourceNameLabel,
  sourceStatusLabel,
  trendStateLabel,
  unknownCodes,
  zoneRoleLabel,
} from "./structureTypes";

// v5 구조 엔진 D4 — 이유·거절 사유 문장(설계 §13).
//
// 규칙:
// - 근거가 없으면 "목표 구조 없음", "피벗 확인 대기"처럼 **무엇이 없는지** 적는다. 빈칸/0점으로 숨기지 않는다(§19-9).
// - 추정 거래량 프로파일과 실제 호가를 구분해서 쓴다. '기관 매물대'처럼 근거 없는 명칭은 쓰지 않는다(§19-7).
// - 여기 숫자는 v5 구조 엔진 값이며 v4 점수/BUY/알림과 의미가 다르다(§19-10).
//
// 이슈 #29 — 기본 화면은 실제 값(추세·후보·계획 가격·손익비)만 그린다. 원천별 커버리지,
// 스냅샷 경고·메모, 근사 플래그 원문, 엔진 원문 설명, 계획 메타는 화면에서 지우지 않고
// 맨 아래 접힌 "진단 상세"로 옮긴다. 서버 데이터는 하나도 버리지 않는다.
// 코드 목록(차단 사유·거절 코드·메모·계획 근거)은 기본 화면에서 등록된 문장만
// 보여주고(knownCodeTexts), 미등록 원시 코드는 진단 상세의 "미등록 코드 원문"에 원문으로 남긴다.

type Props = {
  analysis: StructureAnalysis | null;
  candidate: StructureCandidate | null;
};

const findZone = (
  analysis: StructureAnalysis | null,
  id: string | null | undefined,
): StructureZone | null =>
  (id ? arr(analysis?.zones).find((z) => z.id === id) : undefined) ?? null;

/** 구간 근거 한 줄. 예: '지지 99.20~99.40 / 독립 증거 2계열 / 완료 반응 2회(성공 2)'. */
const zoneSentence = (
  zone: StructureZone | null,
  fallbackLower: number | null | undefined,
  fallbackUpper: number | null | undefined,
  fallbackRole: string,
): string => {
  if (!zone) {
    const range = priceRange(fallbackLower, fallbackUpper);
    return range === "—"
      ? `${fallbackRole} 구간 정보를 찾지 못했습니다`
      : `${fallbackRole} ${range} (구간 상세는 이번 스냅샷에 없음)`;
  }
  const parts = [
    `${zoneRoleLabel(zone.role)} ${priceRange(zone.lower, zone.upper)}`,
    `독립 증거 ${zone.independentFamilies ?? 0}계열`,
    `완료 반응 ${zone.completedEpisodes ?? 0}회(성공 ${zone.successEpisodes ?? 0}·실패 ${zone.failedEpisodes ?? 0})`,
    // 구간 강도는 0~1 척도(기하평균)다. EntryQuality(0~100)와 같은 축이 아니다.
    `강도 ${zone.strength == null ? "산출 불가" : `${num2(zone.strength)}(0~1)`}`,
  ];
  const sources = arr(zone.sourceKinds).map(sourceKindLabel);
  if (sources.length) parts.push(`원천: ${sources.join(" + ")}`);
  return parts.join(" / ");
};

export default function PlanExplanation({ analysis, candidate }: Props) {
  const plan = candidate?.plan ?? null;
  const trend = analysis?.trend ?? null;
  const quality = analysis?.quality ?? null;

  const invalidationZone = useMemo(
    () => findZone(analysis, plan?.invalidationZoneId),
    [analysis, plan],
  );
  const targetZone = useMemo(() => findZone(analysis, plan?.targetZoneId), [analysis, plan]);
  const candidateZone = useMemo(
    () => findZone(analysis, candidate?.zoneId),
    [analysis, candidate],
  );

  // 기본 화면은 등록된 문장만(knownCodeTexts). 미등록 원시 코드는 감추지 않고
  // 아래 진단 상세의 "미등록 코드 원문"에 그대로 남긴다(unknownCodes, §19-9).
  const readyBlockers = knownCodeTexts(quality?.blockersForReady);
  const candidateBlockers = knownCodeTexts(quality?.blockersForCandidate);
  const zoneBlockers = knownCodeTexts(quality?.blockersForZone);
  const trendBlockers = knownCodeTexts(quality?.blockersForTrend);

  const approximations = useMemo(() => {
    const flags = new Set<string>();
    arr(analysis?.zones).forEach((z) => arr(z.approximationFlags).forEach((f) => flags.add(f)));
    return [...flags];
  }, [analysis]);

  const profileOnlyCount = arr(analysis?.zones).filter((z) => z.profileOnly).length;

  const diagUnknown = useMemo(() => {
    const groups: { label: string; codes: string[] }[] = [
      { label: "추세 차단", codes: arr(quality?.blockersForTrend) },
      { label: "구간 차단", codes: arr(quality?.blockersForZone) },
      { label: "후보 차단", codes: arr(quality?.blockersForCandidate) },
      { label: "READY 차단", codes: arr(quality?.blockersForReady) },
      { label: "거절 코드", codes: arr(candidate?.rejectionCodes) },
      { label: "후보 메모", codes: arr(candidate?.notes) },
      { label: "계획 근거 코드", codes: arr(plan?.reasonCodes) },
    ];
    return groups
      .map((g) => ({ label: g.label, codes: unknownCodes(g.codes) }))
      .filter((g) => g.codes.length > 0);
  }, [quality, candidate, plan]);

  if (!analysis) {
    return (
      <section className="structure-explain">
        <p className="structure-none">
          표시할 구조 분석 스냅샷이 없습니다. 위 상태 표시를 확인하세요.
        </p>
      </section>
    );
  }

  const hasPlanMeta =
    !!plan &&
    (plan.explanation || plan.createdAt || plan.expiresAt || plan.engineVersion || plan.policyHash);

  return (
    <section className="structure-explain">
      {/* 추세 */}
      <div className="se-block">
        <h4>추세 (v5 구조 엔진)</h4>
        <p className="se-line">
          방향 <b>{trendStateLabel(trend?.state)}</b> · 강도{" "}
          <b>{trend?.signedTrend == null ? "산출 불가" : num1(trend.signedTrend)}</b>{" "}
          <span className="se-dim">(-100~+100)</span> · 완료 봉 {trend?.barCount ?? 0}개 · 기준
          시각 {clock(trend?.analysisCutoff)}
        </p>
        {trend?.structureEvidenceMissing === true && (
          <p className="se-line warn">
            5분 확정 피벗이 부족합니다 — 피벗 확인 대기 (추세 구조 근거 없음)
          </p>
        )}
        {/* missingComponents는 경고 코드가 아니라 "계산 근거가 부족해 생략한 요소" 이름이다.
            코드 사전(codeTexts)이 아닌 결측 전용 사전으로 설명한다. 값 0은 결측이 아니다(§16A). */}
        {arr(trend?.missingComponents).length > 0 && (
          <>
            <p className="se-line se-dim">
              계산 근거가 아직 부족해 생략한 요소 (계산 오류·0점이 아닙니다):
            </p>
            <ul className="se-list">
              {missingComponentTexts(trend?.missingComponents).map((text, i) => (
                <li key={i}>{text}</li>
              ))}
            </ul>
          </>
        )}
        {trendBlockers.length > 0 && (
          <ul className="se-list">
            {trendBlockers.map((text, i) => (
              <li key={i}>{text}</li>
            ))}
          </ul>
        )}
      </div>

      {/* 후보 / 계획 */}
      <div className="se-block">
        <h4>진입 후보와 계획</h4>
        {!candidate ? (
          <>
            <p className="se-line">
              진입 후보 없음 · 요약 {candidateStateLabel(analysis.candidateSummary)}
            </p>
            {candidateBlockers.length === 0 && zoneBlockers.length === 0 ? (
              <p className="se-line se-dim">차단 사유 없음 — 트리거 대기</p>
            ) : (
              <ul className="se-list">
                {[...candidateBlockers, ...zoneBlockers].map((text, i) => (
                  <li key={i}>{text}</li>
                ))}
              </ul>
            )}
          </>
        ) : (
          <>
            <p className="se-line">
              <span className={`se-state ${candidateGroup(candidate.state)}`}>
                {candidateStateLabel(candidate.state)}
              </span>{" "}
              {setupKindLabel(candidate.kind)} · 진입 품질{" "}
              <b>
                {candidate.entryQuality == null ? "산출 불가" : num1(candidate.entryQuality)}
              </b>{" "}
              <span className="se-dim">(0~100 순위 지표)</span>
            </p>
            <p className="se-line se-dim">
              트리거 봉 {clock(candidate.triggerBarStart)} · 확정 {clock(candidate.triggerConfirmedAt)}{" "}
              · 구조 기준 {clock(candidate.structureCutoff)} · 만료 {clock(candidate.expiresAt)}
              {candidate.counterTrend === true && " · 추세 역방향"}
              {candidate.retestConfirmed === true && " · 되돌림 확인됨"}
            </p>
            <p className="se-line">
              후보 구간: {zoneSentence(candidateZone, null, null, "근거")}
            </p>

            {plan ? (
              <div className="se-plan">
                <p className="se-line">
                  <b>진입 {price(plan.entryReference)}</b>: 후보 트리거 시점의 참고가입니다.
                </p>
                <p className="se-line">
                  <b>손절 {price(plan.stop)}</b>:{" "}
                  {zoneSentence(
                    invalidationZone,
                    plan.invalidationLower,
                    plan.invalidationUpper,
                    "무효화",
                  )}{" "}
                  하단과 무효화 기준선 {price(plan.invalidationAnchor)} 아래 · 여유폭{" "}
                  {plan.buffer == null ? "—" : plan.buffer.toFixed(2)}
                  {plan.bufferBasis ? ` (근거: ${codeText(plan.bufferBasis)})` : ""}
                </p>
                <p className="se-line">
                  <b>목표 {price(plan.target)}</b>:{" "}
                  {zoneSentence(targetZone, plan.targetLower, plan.targetUpper, "다음 저항")} 하단
                  앞
                  {plan.frontRunBuffer != null &&
                    ` · 하단에서 ${plan.frontRunBuffer.toFixed(2)} 앞`}
                </p>
                <p className="se-line">
                  비용 반영 손익비 <b>{num3(plan.netR)}</b> · 기대 폭{" "}
                  {plan.netReward == null ? "—" : plan.netReward.toFixed(2)} / 위험{" "}
                  {plan.netRisk == null ? "—" : plan.netRisk.toFixed(2)} · 위험{" "}
                  {plan.riskPercent == null ? "—" : `${num1(plan.riskPercent)}%`}
                </p>
                <p className="se-line se-dim">
                  비용 가정: 주당 수수료 {plan.feePerShare == null ? "—" : plan.feePerShare.toFixed(4)}{" "}
                  · 추가 비용{" "}
                  {plan.extraCostPerShare == null ? "—" : plan.extraCostPerShare.toFixed(4)} ·
                  실제 호가 스프레드{" "}
                  {/* 이슈 #29: spread=0 가정은 보수성 보장이 없다. "보수적 가정"으로 단정하지 않고 사실대로 적는다. */}
                  {plan.validSpread == null ? "호가 비용 미반영" : plan.validSpread.toFixed(4)}
                  {plan.missingLiquidity === true && " · 호가 없음 표시"}
                </p>
                {knownCodeTexts(plan.reasonCodes).length > 0 && (
                  <ul className="se-list">
                    {knownCodeTexts(plan.reasonCodes).map((text, i) => (
                      <li key={i}>{text}</li>
                    ))}
                  </ul>
                )}
              </div>
            ) : (
              <div className="se-plan reject">
                {/* 이슈 #29: 구현 설명("…선을 그리지 않습니다") 대신 짧은 상태만 남긴다. */}
                <p className="se-line">성립한 계획 없음</p>
                {knownCodeTexts(candidate.rejectionCodes).length > 0 ? (
                  <ul className="se-list">
                    {knownCodeTexts(candidate.rejectionCodes).map((text, i) => (
                      <li key={i}>{text}</li>
                    ))}
                  </ul>
                ) : (
                  <p className="se-line se-dim">거절 코드 없음</p>
                )}
              </div>
            )}

            {knownCodeTexts(candidate.notes).length > 0 && (
              <ul className="se-list">
                {knownCodeTexts(candidate.notes).map((text, i) => (
                  <li key={i}>{text}</li>
                ))}
              </ul>
            )}

            {arr(candidate.components).length > 0 && (
              <div className="se-components">
                <h5>진입 품질 구성요소 (기하평균)</h5>
                <table>
                  <thead>
                    <tr>
                      <th>요소</th>
                      <th>원값</th>
                      <th>변환값</th>
                      <th>필수</th>
                    </tr>
                  </thead>
                  <tbody>
                    {arr(candidate.components).map((c) => (
                      <tr key={c.name}>
                        <td>{qualityComponentLabel(c.name)}</td>
                        <td>{c.raw == null ? "결측" : num3(c.raw)}</td>
                        <td>{c.value == null ? "결측" : num3(c.value)}</td>
                        <td>{c.required === true ? "필수" : "보조"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </div>

      {/* READY 차단 사유 */}
      {readyBlockers.length > 0 && (
        <div className="se-block">
          <h4>READY 차단 사유</h4>
          <ul className="se-list">
            {readyBlockers.map((text, i) => (
              <li key={i}>{text}</li>
            ))}
          </ul>
        </div>
      )}

      {/* 진단 상세 — 원천별 데이터 품질, 스냅샷 경고·메모, 계획 메타, 미등록 코드 원문.
          기본 화면에서는 뺐지만 서버가 준 값은 하나도 버리지 않는다(이슈 #29). */}
      <details className="se-diagnostics">
        <summary>진단 상세</summary>

        <div className="se-block">
          <h4>데이터 품질</h4>
          {arr(quality?.sources).length === 0 ? (
            <p className="se-line se-dim">원천별 품질 정보가 아직 없습니다.</p>
          ) : (
            <ul className="se-list">
              {arr(quality?.sources).map((s) => (
                <li key={s.source}>
                  {sourceNameLabel(s.source)}: {sourceStatusLabel(s.status)} · {s.count ?? 0}건
                  {s.expectedCount != null && ` / 기대 ${s.expectedCount}건`}
                  {s.coverageRatio != null && ` · 커버리지 ${num1(s.coverageRatio * 100)}%`}
                  {s.first && ` · ${clock(s.first)}~${clock(s.last)}`}
                  {arr(s.gaps).length > 0 && ` · 결손 ${arr(s.gaps).length}건`}
                  {arr(s.conflicts).length > 0 && ` · 충돌 ${arr(s.conflicts).length}건`}
                </li>
              ))}
            </ul>
          )}
          {(profileOnlyCount > 0 || approximations.length > 0) && (
            <p className="se-line warn">
              추정 데이터 사용:{" "}
              {profileOnlyCount > 0 && `프로파일만으로 만들어진 구간 ${profileOnlyCount}개 · `}
              봉 기반 거래량 근사이며 실제 가격별 체결 분포나 수급 주체가 아닙니다.
              {approximations.length > 0 && ` (${approximations.map(codeText).join(" / ")})`}
            </p>
          )}
          {codeTexts(quality?.warnings).length > 0 && (
            <ul className="se-list">
              {codeTexts(quality?.warnings).map((text, i) => (
                <li key={i}>{text}</li>
              ))}
            </ul>
          )}
        </div>

        {(codeTexts(analysis.warnings).length > 0 || codeTexts(analysis.notes).length > 0) && (
          <div className="se-block">
            <h4>스냅샷 경고 · 메모</h4>
            <ul className="se-list">
              {codeTexts(analysis.warnings).map((text, i) => (
                <li key={`w${i}`}>{text}</li>
              ))}
              {codeTexts(analysis.notes).map((text, i) => (
                <li key={`n${i}`}>{text}</li>
              ))}
            </ul>
          </div>
        )}

        {hasPlanMeta && plan && (
          <div className="se-block">
            <h4>계획 진단</h4>
            {plan.explanation && <p className="se-line se-raw">엔진 원문: {plan.explanation}</p>}
            <p className="se-line se-dim">
              계획 생성 {clock(plan.createdAt)} · 만료 {clock(plan.expiresAt)} · 엔진{" "}
              {plan.engineVersion ?? "—"} · 정책 {(plan.policyHash ?? "").slice(0, 8) || "—"}
            </p>
          </div>
        )}

        {diagUnknown.length > 0 && (
          <div className="se-block">
            <h4>미등록 코드 원문</h4>
            <ul className="se-list">
              {diagUnknown.flatMap((g) =>
                g.codes.map((c, i) => <li key={`${g.label}-${i}`}>{`${g.label}: ${c}`}</li>),
              )}
            </ul>
          </div>
        )}
      </details>
    </section>
  );
}
