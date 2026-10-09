// 이슈 #26 — 라이브 목록 v5 평가 열 렌더 검증(승인 설계안 §2·§6 FE 테스트 9·10·13).
// 관점: 결측을 0으로 위장하지 않고("미평가"/"추세 미산정"), v5 셀에 확률·승률·%·/100·매수 문구가
// 절대 나타나지 않는다(금지 표현 스냅샷). SignedTrend는 부호+상태 전용 렌더러로만 표시한다.
import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import LiveStructureCells from "./LiveStructureCells";
import { entryQualityText, signedTrendText } from "./structureTypes";
import type { StructureSummaryRow } from "./structureTypes";

const row = (over: Partial<StructureSummaryRow> = {}): StructureSummaryRow => ({
  symbol: "TEST",
  status: "available",
  ...over,
});

/** v5 셀에 금지된 표현(§2 오인 방지): 텍스트와 title 속성 어디에도 없어야 한다. */
const FORBIDDEN = ["%", "승률", "확률", "성공", "/100", "/ 100", "매수", "매도", "점 만점"];

const allText = (container: HTMLElement): string => {
  const titles = [...container.querySelectorAll("[title]")]
    .map((el) => el.getAttribute("title") ?? "")
    .join(" ");
  return `${container.textContent ?? ""} ${titles}`;
};

describe("LiveStructureCells — 표시 규칙", () => {
  it("READY 행: 부호 있는 추세 + 진입 품질 + 후보 종류 배지 + 상태 칩", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({
          trendState: "UP",
          signedTrend: 42.34,
          candidateState: "READY",
          preferredCandidateId: "TEST|event-1",
          entryQuality: 61.24,
          preferredKind: "PULLBACK",
        })}
      />,
    );
    const text = allText(container);
    expect(text).toContain("▲ +42.3 상승");
    expect(text).toContain("61.2");
    expect(text).toContain("눌림(PULLBACK)");
    expect(text).toContain("READY");
  });

  it("음수 추세는 ▼와 −부호로 그대로 표시한다 (매수 점수로 변환하지 않음)", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({ trendState: "TRANSITION", signedTrend: -18.0, candidateState: "WAIT" })}
      />,
    );
    expect(allText(container)).toContain("▼ −18.0 전환");
  });

  it("후보가 하나도 없으면 진입 품질은 '후보 없음'이고 툴팁에 현재 경고를 적는다 — 0으로 렌더하지 않는다(§2-4)", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({
          candidateState: "WAIT",
          preferredCandidateId: null,
          entryQuality: null,
          latestCandidate: null,
          warnings: ["MISSING_5M_STRUCTURE"],
        })}
      />,
    );
    const text = allText(container);
    expect(text).toContain("후보 없음");
    expect(text).not.toContain("미평가");
    expect(text).not.toMatch(/품질\s*0(\.0)?/);
    const cell = [...container.querySelectorAll(".v5-cell")].find((el) => el.textContent?.includes("진입 품질"));
    expect(cell?.getAttribute("title")).toContain("구조 후보가 없습니다");
    expect(cell?.getAttribute("title")).toContain("5분 확정 피벗이 부족합니다");
  });

  it("대표 후보가 없어도 최근 후보가 있으면 그 품질을 상태와 함께 보여 준다", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({
          candidateState: "REJECTED",
          preferredCandidateId: null,
          entryQuality: null,
          latestCandidate: {
            eventId: "TEST|e9",
            kind: "BREAKOUT",
            state: "REJECTED",
            entryQuality: 48.13,
            rejectionCodes: ["COUNTER_TREND_SETUP"],
          },
        })}
      />,
    );
    const text = allText(container);
    expect(text).toContain("48.1");
    expect(text).toContain("부적합");
    expect(text).toContain("돌파(BREAKOUT)");
    expect(text).not.toContain("후보 없음");
    const cell = [...container.querySelectorAll(".v5-cell")].find((el) => el.textContent?.includes("진입 품질"));
    expect(cell?.getAttribute("title")).toContain("최근 후보");
    expect(cell?.getAttribute("title")).toContain("추세와 반대 방향의 후보입니다");
  });

  it("대표 후보가 있으면 최근 후보보다 대표 후보의 품질을 우선한다", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({
          candidateState: "READY",
          preferredCandidateId: "TEST|e1",
          entryQuality: 61.24,
          preferredKind: "PULLBACK",
          latestCandidate: { eventId: "TEST|e2", kind: "BREAKOUT", state: "REJECTED", entryQuality: 20.0 },
        })}
      />,
    );
    const text = allText(container);
    expect(text).toContain("61.2");
    expect(text).not.toContain("20.0");
    expect(text).not.toContain("부적합");
  });

  it("데이터 결측: signedTrend null은 '추세 미산정'이고 warmup 상태 문구가 붙는다", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({ status: "warmup", trendState: null, signedTrend: null })}
      />,
    );
    const text = allText(container);
    expect(text).toContain("추세 미산정");
    expect(text).toContain("워밍업");
  });

  it("warnings는 개수 칩과 한국어 사전 툴팁으로 노출한다 (결측을 정상으로 위장하지 않음)", () => {
    const { container } = render(
      <LiveStructureCells
        mode="active"
        row={row({ warnings: ["MISSING_QUOTE", "INSUFFICIENT_1M_BARS"] })}
      />,
    );
    const chip = container.querySelector(".v5-warnings");
    expect(chip?.textContent).toBe("2");
    expect(chip?.getAttribute("aria-label")).toBe("경고 2건");
    expect(chip?.querySelector("svg")).not.toBeNull();
    expect(chip?.getAttribute("title")).toContain("경고 2건");
    expect(chip?.getAttribute("title")).toContain("진입 판정 결과가 아님");
    expect(chip?.getAttribute("title")).toContain("실시간 호가가 없습니다");
    expect(chip?.getAttribute("title")).toContain("완료된 1분봉이 부족합니다");
  });

  it("off·summary 부재에서는 v5 열을 그리지 않는다 (off는 목록 상단 한 줄이 대신함)", () => {
    expect(render(<LiveStructureCells mode="off" row={row()} />).container.textContent).toBe("");
    expect(render(<LiveStructureCells mode={null} row={row()} />).container.textContent).toBe("");
  });

  it("shadow에서도 같은 열을 그린다 (관측 검증용)", () => {
    const { container } = render(
      <LiveStructureCells mode="shadow" row={row({ candidateState: "READY" })} />,
    );
    expect(container.textContent).toContain("READY");
  });
});

describe("금지 표현 스냅샷 (§6 테스트 13)", () => {
  const cases: [string, StructureSummaryRow][] = [
    [
      "READY 최대 채움",
      row({
        trendState: "UP",
        signedTrend: 99.9,
        candidateState: "READY",
        preferredCandidateId: "TEST|e1",
        entryQuality: 88.8,
        preferredKind: "BREAKOUT",
        warnings: ["MISSING_QUOTE"],
      }),
    ],
    ["결측 행", row({ status: "warmup" })],
    [
      "v4/v5 상반 — v5 거절",
      row({ candidateState: "REJECTED", trendState: "DOWN", signedTrend: -66.6 }),
    ],
    ["진입 처리됨", row({ candidateState: "ENTERED", preferredCandidateId: "TEST|e2", entryQuality: 12.3 })],
    [
      "최근 후보만 있음",
      row({
        candidateState: "REJECTED",
        latestCandidate: { eventId: "TEST|e3", kind: "REBOUND", state: "REJECTED", entryQuality: 58.6, rejectionCodes: ["COUNTER_TREND_SETUP"] },
        warnings: ["MISSING_5M_STRUCTURE", "CURRENT_DAILY_BAR_REMOVED"],
      }),
    ],
  ];
  it.each(cases)("v5 셀(%s)에 확률·승률·%%·/100·매수 문구가 없다", (_name, fixture) => {
    const { container } = render(<LiveStructureCells mode="active" row={fixture} />);
    const text = allText(container);
    for (const word of FORBIDDEN) expect(text).not.toContain(word);
  });
});

describe("표기 helper — v5 전용 렌더러", () => {
  it("signedTrendText: 부호+소수 1자리+상태 라벨, null은 추세 미산정", () => {
    expect(signedTrendText("UP", 42.34)).toBe("▲ +42.3 상승");
    expect(signedTrendText("TRANSITION", -18)).toBe("▼ −18.0 전환");
    expect(signedTrendText("RANGE", 0)).toBe("— 0.0 횡보");
    expect(signedTrendText("UP", null)).toBe("추세 미산정");
    expect(signedTrendText(null, Number.NaN)).toBe("추세 미산정");
  });
  it("entryQualityText(알림 문구): 대표 후보와 값이 모두 있어야 숫자, 아니면 미평가 고정", () => {
    expect(entryQualityText("TEST|e1", 61.24)).toBe("61.2");
    expect(entryQualityText(null, 61.2)).toBe("미평가");
    expect(entryQualityText("TEST|e1", null)).toBe("미평가");
    expect(entryQualityText("TEST|e1", Number.NaN)).toBe("미평가");
  });
  it("helper 출력에 금지 표현이 없다", () => {
    for (const value of [
      signedTrendText("UP", 55.5),
      signedTrendText("DOWN", -55.5),
      signedTrendText(null, null),
      entryQualityText("id", 42),
      entryQualityText(null, null),
    ])
      for (const word of FORBIDDEN) expect(value).not.toContain(word);
  });
});
