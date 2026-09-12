// 이슈 #6 클라이언트 1차 — structureTypes.ts 순수 로직 (표기 helper·라벨·코드 사전).
// 규칙 검증 관점: 결측은 "—"로, 모르는 값은 감추지 않고 "원문 + (미등록)"으로 노출한다(§19-9).
import { describe, expect, it } from "vitest";
import {
  ageSeconds,
  arr,
  candidateGroup,
  candidateStateLabel,
  clock,
  codeText,
  codeTexts,
  missingComponentText,
  missingComponentTexts,
  num1,
  num2,
  num3,
  price,
  priceRange,
  qualityComponentLabel,
  setupKindLabel,
  sidebarConfluenceScore,
  sourceKindLabel,
  sourceNameLabel,
  sourceStatusLabel,
  statusLabel,
  trendStateLabel,
  zoneRoleGroup,
  zoneRoleLabel,
} from "./structureTypes";
import type { StructureSummaryRow } from "./structureTypes";

describe("arr", () => {
  it("배열이 아니면 빈 배열을 돌려준다", () => {
    expect(arr(null)).toEqual([]);
    expect(arr(undefined)).toEqual([]);
  });
  it("배열은 그대로 돌려준다", () => {
    const value = [1, 2];
    expect(arr(value)).toBe(value);
  });
});

describe("숫자 표기 (표시만 반올림, 결측은 —)", () => {
  it("null·NaN·Infinity는 —", () => {
    for (const format of [num1, num2, num3]) {
      expect(format(null)).toBe("—");
      expect(format(undefined)).toBe("—");
      expect(format(Number.NaN)).toBe("—");
      expect(format(Number.POSITIVE_INFINITY)).toBe("—");
    }
  });
  it("자릿수 고정", () => {
    expect(num1(12.34)).toBe("12.3");
    expect(num2(12.345)).toBe("12.35");
    expect(num3(1)).toBe("1.000");
  });
});

describe("price / priceRange", () => {
  it("결측은 —", () => {
    expect(price(null)).toBe("—");
    expect(price(Number.NaN)).toBe("—");
    expect(priceRange(null, 3)).toBe("—");
    expect(priceRange(1, Number.NaN)).toBe("—");
  });
  it("달러 표기와 범위 표기", () => {
    expect(price(99.2)).toBe("$99.20");
    expect(priceRange(99.2, 99.4)).toBe("99.20~99.40");
  });
});

describe("clock / ageSeconds", () => {
  it("결측·비정상 시각은 — / null", () => {
    expect(clock(null)).toBe("—");
    expect(clock("")).toBe("—");
    expect(clock("not-a-date")).toBe("—");
    expect(ageSeconds(null)).toBeNull();
    expect(ageSeconds("not-a-date")).toBeNull();
  });
  it("유효한 시각은 시:분:초를 포함한다", () => {
    const text = clock(new Date().toISOString());
    expect(text).not.toBe("—");
    expect(text).toContain(":");
  });
  it("경과 초는 0 이상으로 클램프된다", () => {
    const future = new Date(Date.now() + 60_000).toISOString();
    expect(ageSeconds(future)).toBe(0);
    const past = new Date(Date.now() - 5_000).toISOString();
    const age = ageSeconds(past);
    expect(age).toBeGreaterThanOrEqual(4);
    expect(age).toBeLessThanOrEqual(6);
  });
});

describe("statusLabel", () => {
  it.each([
    ["available", "분석 표시 중"],
    ["warmup", "워밍업 — 완료 봉 수집 중"],
    ["marketClosed", "정규장 외 — 갱신하지 않음"],
    ["stopped", "모니터링 중지됨"],
    ["disabled", "구조 엔진 off"],
    ["unavailable", "분석 불가"],
  ])("%s → %s", (input, expected) => {
    expect(statusLabel(input)).toBe(expected);
  });
  it("모르는 상태는 원문 + 미등록, 결측은 상태 미상", () => {
    expect(statusLabel("weird")).toBe("상태 weird (미등록)");
    expect(statusLabel(null)).toBe("상태 미상");
  });
});

describe("trendStateLabel / candidateStateLabel / candidateGroup", () => {
  it("대소문자 무관 매핑", () => {
    expect(trendStateLabel("up")).toBe("상승");
    expect(trendStateLabel("DOWN")).toBe("하락");
    expect(trendStateLabel("Range")).toBe("횡보");
    expect(trendStateLabel("TRANSITION")).toBe("전환");
    expect(trendStateLabel("UNKNOWN")).toBe("판단 불가");
    expect(trendStateLabel(null)).toBe("판단 불가");
    expect(trendStateLabel("odd")).toBe("odd (미등록)");
  });
  it("후보 상태 라벨", () => {
    expect(candidateStateLabel("ready")).toBe("READY");
    expect(candidateStateLabel("WAIT")).toBe("대기");
    expect(candidateStateLabel("REJECTED")).toBe("부적합");
    expect(candidateStateLabel("INVALIDATED")).toBe("무효화");
    expect(candidateStateLabel("EXPIRED")).toBe("만료");
    expect(candidateStateLabel("ENTERED")).toBe("진입 처리됨");
    expect(candidateStateLabel(null)).toBe("—");
    expect(candidateStateLabel("odd")).toBe("odd (미등록)");
  });
  it("그룹 분류 — ENTERED는 wait, 종결류는 reject", () => {
    expect(candidateGroup("READY")).toBe("ready");
    expect(candidateGroup("WAIT")).toBe("wait");
    expect(candidateGroup("ENTERED")).toBe("wait");
    expect(candidateGroup("REJECTED")).toBe("reject");
    expect(candidateGroup("INVALIDATED")).toBe("reject");
    expect(candidateGroup("EXPIRED")).toBe("reject");
    expect(candidateGroup(null)).toBe("none");
  });
});

describe("setupKindLabel", () => {
  it("종류 라벨과 미등록 처리", () => {
    expect(setupKindLabel("pullback")).toBe("눌림(PULLBACK)");
    expect(setupKindLabel("BREAKOUT")).toBe("돌파(BREAKOUT)");
    expect(setupKindLabel("REBOUND")).toBe("반등(REBOUND)");
    expect(setupKindLabel(null)).toBe("—");
    expect(setupKindLabel("odd")).toBe("odd (미등록)");
  });
});

describe("zoneRoleLabel / zoneRoleGroup — FLIPPED_* 표기 양쪽 수용", () => {
  it("계약 문서(FLIPPED_SUPPORT)와 실제 직렬화(FLIPPEDSUPPORT)를 같은 라벨로 받는다", () => {
    expect(zoneRoleLabel("FLIPPED_SUPPORT")).toBe("지지로 전환");
    expect(zoneRoleLabel("FLIPPEDSUPPORT")).toBe("지지로 전환");
    expect(zoneRoleLabel("flipped_resistance")).toBe("저항으로 전환");
  });
  it("기본 역할 라벨", () => {
    expect(zoneRoleLabel("SUPPORT")).toBe("지지");
    expect(zoneRoleLabel("RESISTANCE")).toBe("저항");
    expect(zoneRoleLabel("BROKEN")).toBe("붕괴");
    expect(zoneRoleLabel("UNRESOLVED")).toBe("역할 미확정");
    expect(zoneRoleLabel(null)).toBe("역할 미상");
    expect(zoneRoleLabel("odd")).toBe("odd (미등록)");
  });
  it("그룹 분류", () => {
    expect(zoneRoleGroup("SUPPORT")).toBe("support");
    expect(zoneRoleGroup("FLIPPED_SUPPORT")).toBe("support");
    expect(zoneRoleGroup("RESISTANCE")).toBe("resistance");
    expect(zoneRoleGroup("FLIPPEDRESISTANCE")).toBe("resistance");
    expect(zoneRoleGroup("BROKEN")).toBe("neutral");
    expect(zoneRoleGroup(null)).toBe("neutral");
  });
});

describe("sourceKindLabel — 추정 프로파일과 실제 데이터를 구분해 부른다", () => {
  it("profile은 근사임을 명시한다", () => {
    expect(sourceKindLabel("volumeProfile")).toContain("실제 체결 분포 아님");
  });
  it("피벗·ORB·일봉 라벨", () => {
    expect(sourceKindLabel("pivot5m")).toBe("5분 확정 피벗");
    expect(sourceKindLabel("FIVEMINUTE_pivot")).toBe("5분 확정 피벗");
    expect(sourceKindLabel("pivot1m")).toBe("1분 확정 피벗");
    expect(sourceKindLabel("pivotDaily")).toBe("확정 피벗");
    expect(sourceKindLabel("orb15")).toBe("개장 15분 범위(ORB15)");
    expect(sourceKindLabel("prevDay")).toBe("직전 일봉 컨텍스트");
    expect(sourceKindLabel(null)).toBe("원천 미상");
    expect(sourceKindLabel("odd")).toBe("odd");
  });
});

describe("qualityComponentLabel — Quality 접미사 양쪽 수용", () => {
  it("invalidationQuality(실제 상수)와 invalidation(문서 예시)을 같은 라벨로", () => {
    expect(qualityComponentLabel("invalidationQuality")).toBe("무효화 구조 품질");
    expect(qualityComponentLabel("invalidation")).toBe("무효화 구조 품질");
  });
  it("나머지 구성요소", () => {
    expect(qualityComponentLabel("targetQuality")).toBe("목표 구조 품질");
    expect(qualityComponentLabel("room")).toBe("목표까지 남은 공간");
    expect(qualityComponentLabel("extension")).toBe("진입가의 구조 대비 이격");
    expect(qualityComponentLabel("triggerVolume")).toBe("트리거 봉 거래량");
    expect(qualityComponentLabel("alignment")).toBe("추세 정합");
    expect(qualityComponentLabel("reclaim")).toBe("지지 회복 강도");
    expect(qualityComponentLabel("odd")).toBe("odd");
    expect(qualityComponentLabel(null)).toBe("—");
  });
});

describe("sourceNameLabel / sourceStatusLabel", () => {
  it("§16B 원천 이름", () => {
    expect(sourceNameLabel("bars1m")).toBe("완료 1분봉");
    expect(sourceNameLabel("bars5m")).toBe("완료 5분봉");
    expect(sourceNameLabel("daily")).toBe("완료 일봉");
    expect(sourceNameLabel("quote")).toBe("실시간 호가(체결가)");
    expect(sourceNameLabel("liquidity")).toBe("호가 스프레드");
    expect(sourceNameLabel("volumeProfile")).toContain("근사");
    expect(sourceNameLabel("indicators")).toBe("세션 지표(EMA·VWAP·ATR)");
    expect(sourceNameLabel("pivots5m")).toBe("5분 확정 피벗");
    expect(sourceNameLabel("odd")).toBe("odd");
    expect(sourceNameLabel(null)).toBe("원천 미상");
  });
  it("원천 상태", () => {
    expect(sourceStatusLabel("available")).toBe("사용 가능");
    expect(sourceStatusLabel("APPROXIMATE")).toBe("근사값");
    expect(sourceStatusLabel("stale")).toBe("오래됨");
    expect(sourceStatusLabel("missing")).toBe("없음");
    expect(sourceStatusLabel("odd")).toBe("odd (미등록)");
    expect(sourceStatusLabel(null)).toBe("상태 미상");
  });
});

describe("codeText — 코드를 한국어 문장으로", () => {
  it("등록된 코드", () => {
    expect(codeText("NO_TARGET_STRUCTURE")).toContain("목표 구조 없음");
    expect(codeText("MISSING_QUOTE")).toBe("실시간 호가가 없습니다");
  });
  it("반복 접미(xN)", () => {
    expect(codeText("BAR_GAPx3")).toBe("봉 결손 구간이 있습니다 (3회)");
  });
  it("CODE:detail 형태", () => {
    expect(codeText("STALE_QUOTE:41s")).toBe("호가가 오래되었습니다 (30초 만료) (상세: 41s)");
  });
  it("V5_ENTRY_* 관측 note 4종 (이슈 #21 — D6 active 배선)", () => {
    expect(codeText("V5_ENTRY_COMMITTED")).toBe("v5 구조 계획으로 진입을 생성했습니다");
    expect(codeText("V5_ENTRY_BLOCKED_BY_OPEN_TRADE")).toBe(
      "이 종목에 OPEN 거래가 있어 신규 진입을 보류했습니다",
    );
    expect(codeText("V5_ENTRY_PLAN_INVALID")).toBe(
      "동결 계획의 가격 순서가 성립하지 않아 진입을 거절했습니다",
    );
    expect(codeText("V5_ENTRY_PORT_UNAVAILABLE")).toBe(
      "진입 포트가 배선되지 않아 진입을 보류했습니다 (설정 문제)",
    );
  });
  it("2026-09-11 라운드 신설 거절·경고 코드 (이슈 #53)", () => {
    expect(codeText("TREND_DIRECTION_OPPOSES_LONG")).toBe(
      "추세 방향이 롱 진입과 반대입니다 (역방향 진입은 거절합니다)",
    );
    expect(codeText("STOP_INSIDE_COST")).toBe("손절 폭이 왕복 수수료보다 좁습니다");
    expect(codeText("STOP_INSIDE_NOISE")).toBe(
      "손절 폭이 1분 ATR 절반보다 좁습니다 (체결 잡음 구간)",
    );
    expect(codeText("BREAKOUT_ZONE_COOLDOWN")).toBe(
      "같은 저항 구간의 돌파 재발동을 30분 동안 억제합니다",
    );
    expect(codeText("ObservationStorageLimited")).toBe(
      "관측 저장 한도로 주기 요약 일부가 축약되었습니다",
    );
    expect(codeText("ObservationCoreStorageLimited")).toBe(
      "관측 저장 한도로 핵심 관측까지 누락되어 전체 검증이 불가합니다",
    );
  });
  it("V5_READY_WITHOUT_5M_STRUCTURE는 이미 등록되어 있다 (이슈 #29)", () => {
    expect(codeText("V5_READY_WITHOUT_5M_STRUCTURE")).toBe(
      "5분 구조 확인 전 반등 진입 — 구조 결측 상태 표식",
    );
  });
  it("V5_ENTRY 계열이라도 등록되지 않은 코드는 fallback으로 원문을 노출한다", () => {
    expect(codeText("V5_ENTRY_SOMETHING_NEW")).toBe(
      "V5_ENTRY_SOMETHING_NEW — 설명이 등록되지 않은 코드입니다 (원문 표시)",
    );
  });
  it("모르는 코드는 감추지 않고 원문을 노출한다(§19-9)", () => {
    expect(codeText("TOTALLY_UNKNOWN")).toBe(
      "TOTALLY_UNKNOWN — 설명이 등록되지 않은 코드입니다 (원문 표시)",
    );
  });
  it("빈 문자열·공백은 빈 문자열", () => {
    expect(codeText("")).toBe("");
    expect(codeText("   ")).toBe("");
  });
});

describe("codeTexts", () => {
  it("null은 빈 배열, 빈 코드는 걸러낸다", () => {
    expect(codeTexts(null)).toEqual([]);
    expect(codeTexts(["MISSING_QUOTE", ""])).toEqual(["실시간 호가가 없습니다"]);
  });
});

// 이슈 #25 — missingComponents 표시 계약.
// missingComponents는 경고/이벤트 코드가 아니라 계산 구성요소 이름이며,
// "계산에 필요한 근거 부족"으로 설명한다. 코드 사전(codeText)과 분리된 경로다.
describe("missingComponentText — 결측 컴포넌트 전용 사전 (이슈 #25)", () => {
  // 서버 전수 대조: TrendEvaluator.cs가 trend.missingComponents에 넣는 7종.
  const TREND_KEYS = [
    "structureDirection",
    "atr1m",
    "vwap",
    "efficiency",
    "emaDirection",
    "slopeDirection",
    "vwapDirection",
  ];
  // ZoneEvaluator.cs가 구간 강도 missingComponents(DTO: missingEvidence)에 넣는 4종.
  const ZONE_KEYS = ["touchEvidence", "reactionEvidence", "recency", "confluence"];

  it("서버가 내보내는 알려진 결측 키 전부가 사전에 등록되어 fallback을 타지 않는다", () => {
    for (const key of [...TREND_KEYS, ...ZONE_KEYS]) {
      const text = missingComponentText(key);
      expect(text, key).not.toContain("설명이 등록되지 않은");
      expect(text, key).not.toContain("원문 표시");
      // 한국어 설명이어야 한다 (키 이름만 되돌려주지 않는다).
      expect(text, key).toMatch(/[가-힣]/);
    }
  });

  it("structureDirection은 확정 5분 피벗 구조 부족으로 설명한다", () => {
    const text = missingComponentText("structureDirection");
    expect(text).toContain("확정 5분 피벗");
    expect(text).toContain("부족");
    // 코드 fallback 문구("설명이 등록되지 않은 코드")로 새지 않는다.
    expect(text).not.toContain("코드");
  });

  it("결측은 근거 부족이지 계산 오류가 아니다 — 오류/실패 단어를 쓰지 않는다", () => {
    for (const key of [...TREND_KEYS, ...ZONE_KEYS]) {
      const text = missingComponentText(key);
      expect(text, key).not.toContain("오류");
      expect(text, key).not.toContain("실패");
    }
  });

  it("모르는 새 키는 감추지 않고 원문을 보존한다(§19-9)", () => {
    expect(missingComponentText("brandNewComponent")).toBe(
      "brandNewComponent — 설명이 등록되지 않은 결측 요소입니다 (원문 표시)",
    );
  });

  it("빈 문자열·공백은 빈 문자열", () => {
    expect(missingComponentText("")).toBe("");
    expect(missingComponentText("   ")).toBe("");
  });
});

describe("missingComponentTexts", () => {
  it("null은 빈 배열, 빈 이름은 걸러낸다", () => {
    expect(missingComponentTexts(null)).toEqual([]);
    expect(missingComponentTexts(["vwap", ""])).toEqual([
      "VWAP — 세션 거래량이 아직 없어 VWAP을 계산하지 못했습니다",
    ]);
  });
});

// 이슈 #181: 사이드바 전 종목 컨플루언스 배지 — K3 선택 종목 최신값이 structureSummary 캐시보다 우선한다.
describe("sidebarConfluenceScore", () => {
  const rowWithConfluence = (score: number | null): StructureSummaryRow => ({
    symbol: "AAPL",
    confluence: { score, warmupCount: 0, weightsVersion: "uniform.1", barEnd: "2026-09-12T00:31:00Z" },
  });

  it("선택 종목이고 pin 값이 있으면 pin을 우선한다", () => {
    expect(sidebarConfluenceScore({ symbol: "AAPL", score: 0.5 }, "AAPL", rowWithConfluence(-0.2))).toBe(0.5);
  });

  it("다른 종목 행이면 pin을 무시하고 structureSummary 캐시값을 쓴다", () => {
    expect(sidebarConfluenceScore({ symbol: "AAPL", score: 0.5 }, "MSFT", rowWithConfluence(-0.2))).toBe(-0.2);
  });

  it("pin이 없으면(다른 종목이거나 아직 폴링 전) structureSummary 캐시값을 쓴다", () => {
    expect(sidebarConfluenceScore(null, "AAPL", rowWithConfluence(0.7))).toBe(0.7);
  });

  it("캐시도 없으면(워밍업·미보유) null이고 배지가 숨겨진다", () => {
    expect(sidebarConfluenceScore(null, "AAPL", { symbol: "AAPL" })).toBeNull();
    expect(sidebarConfluenceScore(null, "AAPL", { symbol: "AAPL", confluence: null })).toBeNull();
    expect(sidebarConfluenceScore(null, "AAPL", undefined)).toBeNull();
  });

  it("pin.score가 null이면(폴링 중) structureSummary 캐시값으로 대체한다", () => {
    expect(sidebarConfluenceScore({ symbol: "AAPL", score: null }, "AAPL", rowWithConfluence(0.3))).toBe(0.3);
  });
});
