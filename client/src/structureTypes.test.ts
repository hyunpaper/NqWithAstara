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
  statusLabel,
  trendStateLabel,
  zoneRoleGroup,
  zoneRoleLabel,
} from "./structureTypes";

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
