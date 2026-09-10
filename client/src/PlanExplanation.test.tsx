// 이슈 #6 클라이언트 1차 — PlanExplanation 렌더 검증(RTL).
// §13 관점: 근거가 없으면 "무엇이 없는지" 문장으로 적고, 없는 근거(선·계획)를 그리지 않는다.
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

// 텍스트가 부모·자식 요소에 중복 매칭될 수 있어(전체 textContent 기준),
// "적어도 한 요소에 존재"를 검증 기준으로 쓴다.
const expectText = (matcher: string | RegExp) =>
  expect(screen.getAllByText(matcher).length).toBeGreaterThan(0);
import PlanExplanation from "./PlanExplanation";
import type { StructureAnalysis, StructureCandidate } from "./structureTypes";

const baseAnalysis: StructureAnalysis = {
  symbol: "TSLA",
  status: "available",
  candidateSummary: "WAIT",
  trend: {
    state: "UP",
    signedTrend: 62.4,
    barCount: 45,
    analysisCutoff: "2026-09-10T14:30:00Z",
    structureEvidenceMissing: false,
  },
  zones: [
    {
      id: "z-support",
      lower: 99.2,
      upper: 99.4,
      role: "SUPPORT",
      independentFamilies: 2,
      completedEpisodes: 2,
      successEpisodes: 2,
      failedEpisodes: 0,
      strength: 0.71,
      sourceKinds: ["pivot5m", "volumeProfile"],
    },
    {
      id: "z-target",
      lower: 101.0,
      upper: 101.3,
      role: "RESISTANCE",
      independentFamilies: 3,
      completedEpisodes: 1,
      successEpisodes: 0,
      failedEpisodes: 1,
      strength: 0.55,
    },
  ],
  quality: {
    sources: [
      {
        source: "bars1m",
        status: "available",
        count: 45,
        expectedCount: 45,
        coverageRatio: 1,
        first: "2026-09-10T13:30:00Z",
        last: "2026-09-10T14:15:00Z",
      },
      { source: "quote", status: "missing", count: 0, gaps: ["g1"], conflicts: ["c1"] },
    ],
  },
};

const candidateWithPlan: StructureCandidate = {
  eventId: "evt-1",
  kind: "PULLBACK",
  zoneId: "z-support",
  state: "READY",
  entryQuality: 74.2,
  triggerBarStart: "2026-09-10T14:14:00Z",
  triggerConfirmedAt: "2026-09-10T14:15:00Z",
  counterTrend: false,
  retestConfirmed: true,
  components: [
    { name: "invalidationQuality", raw: 0.71, value: 0.82, required: true },
    { name: "room", raw: null, value: null, required: false },
  ],
  plan: {
    planId: "plan-1",
    entryReference: 99.55,
    invalidationAnchor: 99.2,
    stop: 99.05,
    target: 100.9,
    invalidationZoneId: "z-support",
    targetZoneId: "z-target",
    buffer: 0.15,
    bufferBasis: "ATR_NOISE",
    frontRunBuffer: 0.1,
    netR: 2.412,
    netReward: 1.21,
    netRisk: 0.5,
    riskPercent: 0.5,
    feePerShare: 0.0035,
    validSpread: null,
    missingLiquidity: true,
    reasonCodes: ["SPREAD"],
    engineVersion: "v5-structure.1",
    policyHash: "abcdef1234567890",
  },
};

describe("PlanExplanation — 스냅샷 없음", () => {
  it("분석이 없으면 없다고 적는다 (임의 렌더 없음)", () => {
    render(<PlanExplanation analysis={null} candidate={null} />);
    expectText(/표시할 구조 분석 스냅샷이 없습니다/);
  });
});

describe("PlanExplanation — 후보 없음", () => {
  it("차단 사유가 없으면 '트리거 대기'를 문장으로 적는다", () => {
    render(<PlanExplanation analysis={baseAnalysis} candidate={null} />);
    expectText(/이번 스냅샷에는 진입 후보가 없습니다/);
    expectText(/요약: 대기/);
    expectText(/트리거 조건을 만족한 완료 봉이 아직 없습니다/);
  });

  it("차단 코드가 있으면 코드 사전을 거친 한국어 사유를 나열한다", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      quality: {
        ...baseAnalysis.quality,
        blockersForCandidate: ["NO_TARGET_STRUCTURE"],
        blockersForZone: ["STRENGTH_BELOW_MINIMUM"],
      },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText(/목표 구조 없음/);
    expectText(/구간 강도가 최소 기준 미만입니다/);
    expect(screen.queryByText(/트리거 조건을 만족한 완료 봉이 아직 없습니다/)).toBeNull();
  });
});

describe("PlanExplanation — 후보 + 계획", () => {
  it("추세·진입 품질·계획 가격을 렌더한다", () => {
    render(<PlanExplanation analysis={baseAnalysis} candidate={candidateWithPlan} />);
    // 추세 블록
    expectText("상승");
    expectText("62.4");
    // 후보 헤더
    expectText("READY");
    expectText(/눌림\(PULLBACK\)/);
    expectText("74.2");
    // 계획 가격 3종
    expectText(/진입 \$99\.55/);
    expectText(/손절 \$99\.05/);
    expectText(/목표 \$100\.90/);
    // 손익비는 소수 3자리
    expectText("2.412");
  });

  it("구간 문장은 zoneId로 실제 구간을 찾아 증거 수치를 적는다", () => {
    render(<PlanExplanation analysis={baseAnalysis} candidate={candidateWithPlan} />);
    // 무효화 구간(z-support) 문장: 역할 + 범위 + 독립 증거
    expect(screen.getAllByText(/지지 99\.20~99\.40/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/독립 증거 2계열/).length).toBeGreaterThan(0);
    // 목표 구간(z-target)
    expectText(/저항 101\.00~101\.30/);
  });

  it("진입 품질 구성요소 표 — 결측은 '결측'으로 적는다", () => {
    render(<PlanExplanation analysis={baseAnalysis} candidate={candidateWithPlan} />);
    expectText("무효화 구조 품질");
    expectText("목표까지 남은 공간");
    expect(screen.getAllByText("결측").length).toBe(2);
    expectText("필수");
    expectText("보조");
  });

  it("호가 결측이면 보수적 가정을 명시한다", () => {
    render(<PlanExplanation analysis={baseAnalysis} candidate={candidateWithPlan} />);
    expectText(/확인 불가 \(보수적으로 가정\)/);
    expectText(/호가 없음 표시/);
  });
});

describe("PlanExplanation — 후보는 있으나 계획 불성립", () => {
  it("계획 없음을 문장으로 적고 거절 코드를 나열한다 (선을 그리지 않는다)", () => {
    const rejected: StructureCandidate = {
      eventId: "evt-2",
      kind: "BREAKOUT",
      state: "REJECTED",
      entryQuality: null,
      plan: null,
      rejectionCodes: ["INSUFFICIENT_REWARD_TO_RISK"],
    };
    render(<PlanExplanation analysis={baseAnalysis} candidate={rejected} />);
    expectText(/성립한 계획이 없습니다/);
    expectText(/비용 반영 손익비가 기준에 못 미칩니다/);
    expectText("부적합");
    expect(screen.getAllByText(/산출 불가/).length).toBeGreaterThan(0);
  });

  it("거절 코드가 없으면 데이터 품질 차단 사유로 안내한다", () => {
    const rejected: StructureCandidate = {
      eventId: "evt-3",
      state: "REJECTED",
      plan: null,
    };
    render(<PlanExplanation analysis={baseAnalysis} candidate={rejected} />);
    expectText(/서버가 별도 거절 코드를 보내지 않았습니다/);
  });
});

describe("PlanExplanation — READY 차단·데이터 품질·경고", () => {
  it("READY 차단 사유 블록은 코드가 있을 때만 나온다", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      quality: { ...baseAnalysis.quality, blockersForReady: ["MISSING_QUOTE"] },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText("READY 차단 사유");
    expectText("실시간 호가가 없습니다");
  });

  it("원천별 데이터 품질 행 — 커버리지·결손·충돌을 적는다", () => {
    render(<PlanExplanation analysis={baseAnalysis} candidate={null} />);
    expectText(/완료 1분봉: 사용 가능 · 45건/);
    expectText(/커버리지 100\.0%/);
    expectText(/결손 1건/);
    expectText(/충돌 1건/);
  });

  it("추정 프로파일 전용 구간은 근사임을 경고한다", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      zones: [
        { id: "z-p", lower: 98, upper: 98.4, profileOnly: true, approximationFlags: ["PROFILE_ONLY"] },
      ],
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText(/프로파일만으로 만들어진 구간 1개/);
  });

  it("스냅샷 경고·메모 블록", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      warnings: ["STALE_LATEST_BAR"],
      notes: ["WATERMARK_SEEDED_NO_NEW_TRIGGER"],
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText("스냅샷 경고 · 메모");
    expectText(/가장 최근 완료 봉이 오래되었습니다/);
    expectText(/재시작 직후라 이번 봉에서는 신규 트리거를 만들지 않습니다/);
  });

  it("5분 피벗 부족이면 추세 구조 근거 없음을 명시한다", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      trend: { ...baseAnalysis.trend, structureEvidenceMissing: true, signedTrend: null },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText(/피벗 확인 대기 \(추세 구조 근거 없음\)/);
    expect(screen.getAllByText(/산출 불가/).length).toBeGreaterThan(0);
  });
});

// 이슈 #25 — missingComponents 렌더 계약: 결측 컴포넌트 이름은 코드 사전이 아니라
// 결측 전용 사전으로 "계산 근거 부족" 문장이 된다.
describe("PlanExplanation — missingComponents 표시 계약 (이슈 #25)", () => {
  it("structureDirection 결측이 사람 읽을 수 있는 문장으로 나온다 (코드 fallback 아님)", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      trend: {
        ...baseAnalysis.trend,
        structureEvidenceMissing: true,
        missingComponents: ["structureDirection"],
      },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText(/구조 방향을 계산할 확정 5분 피벗 구조가 아직 부족합니다/);
    // 결측은 근거 부족이지 오류·0점이 아님을 안내한다.
    expectText(/계산 근거가 아직 부족해 생략한 요소/);
    // 운영 버그였던 코드 사전 fallback 문구가 더는 나오지 않는다.
    expect(screen.queryByText(/설명이 등록되지 않은 코드입니다/)).toBeNull();
  });

  it("알려진 결측 키 여러 개가 각각 한국어 문장으로 나열된다", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      trend: {
        ...baseAnalysis.trend,
        missingComponents: ["atr1m", "vwap", "efficiency"],
      },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText(/1분 ATR — 유효한 ATR\(>0\)이 아직 없어/);
    expectText(/VWAP — 세션 거래량이 아직 없어/);
    expectText(/추세 효율 — 경로 효율을 계산할 완료 봉이 아직 부족합니다/);
  });

  it("미등록 새 키는 감추지 않고 원문을 보존한다", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      trend: { ...baseAnalysis.trend, missingComponents: ["brandNewComponent"] },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText(/brandNewComponent — 설명이 등록되지 않은 결측 요소입니다 \(원문 표시\)/);
  });

  it("값 0은 결측으로 처리하지 않는다 — signedTrend=0은 0.0으로 표시", () => {
    const analysis: StructureAnalysis = {
      ...baseAnalysis,
      trend: { ...baseAnalysis.trend, signedTrend: 0, missingComponents: [] },
    };
    render(<PlanExplanation analysis={analysis} candidate={null} />);
    expectText("0.0");
    // 추세 블록에 결측 안내가 나오지 않는다 (missingComponents가 비어 있으므로).
    expect(screen.queryByText(/계산 근거가 아직 부족해 생략한 요소/)).toBeNull();
    expect(screen.queryByText(/설명이 등록되지 않은/)).toBeNull();
  });
});
