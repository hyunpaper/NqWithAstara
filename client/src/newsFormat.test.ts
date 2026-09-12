import { describe, expect, it } from "vitest";
import {
  absoluteTimeKst,
  inputKindLabel,
  relativeTimeKo,
  scoreBadge,
  scoreBadgeTitle,
  sentimentBadge,
} from "./newsFormat";

describe("scoreBadge", () => {
  it("+2 이상은 호재(positive)다", () => {
    expect(scoreBadge(2)?.className).toBe("news-badge positive");
    expect(scoreBadge(4.9)?.className).toBe("news-badge positive");
  });
  it("-2 이하는 악재(negative)다", () => {
    expect(scoreBadge(-2)?.className).toBe("news-badge negative");
    expect(scoreBadge(-4.9)?.className).toBe("news-badge negative");
  });
  it("경계 안쪽(-2 초과 +2 미만)은 중립이다", () => {
    expect(scoreBadge(1.9)?.className).toBe("news-badge neutral");
    expect(scoreBadge(-1.9)?.className).toBe("news-badge neutral");
    expect(scoreBadge(0)?.className).toBe("news-badge neutral");
  });
  it("데이터 없음(null/undefined)은 배지가 없다", () => {
    expect(scoreBadge(null)).toBeNull();
    expect(scoreBadge(undefined)).toBeNull();
    expect(scoreBadge(NaN)).toBeNull();
  });
});

describe("scoreBadgeTitle", () => {
  const now = new Date("2026-09-12T10:00:00Z").getTime();
  it("건수·경과분을 함께 표기한다", () => {
    expect(scoreBadgeTitle(4, "2026-09-12T09:55:00Z", now)).toBe("4건 · 최근 5분");
  });
  it("건수가 없으면 기본 문구다", () => {
    expect(scoreBadgeTitle(null, null, now)).toBe("뉴스 감성");
    expect(scoreBadgeTitle(0, "2026-09-12T09:55:00Z", now)).toBe("뉴스 감성");
  });
  it("건수는 있는데 시각이 없으면 경과분을 생략한다", () => {
    expect(scoreBadgeTitle(3, null, now)).toBe("3건");
    expect(scoreBadgeTitle(3, "garbage", now)).toBe("3건");
  });
  it("미래 시각은 경과분 0으로 표기한다(음수 방지)", () => {
    expect(scoreBadgeTitle(1, "2026-09-12T10:05:00Z", now)).toBe("1건 · 최근 0분");
  });
});

describe("sentimentBadge", () => {
  it("positive/negative/neutral은 각각 배지를 가진다", () => {
    expect(sentimentBadge("positive")?.label).toBe("호재");
    expect(sentimentBadge("negative")?.label).toBe("악재");
    expect(sentimentBadge("neutral")?.label).toBe("중립");
  });
  it("unclassified는 배지가 없다", () => {
    expect(sentimentBadge("unclassified")).toBeNull();
  });
});

describe("relativeTimeKo", () => {
  const now = new Date("2026-09-12T10:00:00Z").getTime();
  it("1분 미만은 방금 전이다", () => {
    expect(relativeTimeKo("2026-09-12T09:59:30Z", now)).toBe("방금 전");
  });
  it("분 단위로 표기한다", () => {
    expect(relativeTimeKo("2026-09-12T09:57:00Z", now)).toBe("3분 전");
  });
  it("시간 단위로 표기한다", () => {
    expect(relativeTimeKo("2026-09-12T07:00:00Z", now)).toBe("3시간 전");
  });
  it("일 단위로 표기한다", () => {
    expect(relativeTimeKo("2026-09-09T10:00:00Z", now)).toBe("3일 전");
  });
  it("결측·잘못된 시각은 '시각 없음'이다", () => {
    expect(relativeTimeKo(null, now)).toBe("시각 없음");
    expect(relativeTimeKo("garbage", now)).toBe("시각 없음");
  });
});

describe("absoluteTimeKst / inputKindLabel", () => {
  it("결측 시각은 '시각 없음'이다", () => {
    expect(absoluteTimeKst(null)).toBe("시각 없음");
  });
  it("KST 표기가 붙는다", () => {
    expect(absoluteTimeKst("2026-09-12T00:00:00Z")).toContain("KST");
  });
  it("inputKind 라벨을 한국어로 변환한다", () => {
    expect(inputKindLabel("summary")).toBe("요약 기반");
    expect(inputKindLabel("body")).toBe("본문 기반");
    expect(inputKindLabel("headline")).toBe("제목 기반");
    expect(inputKindLabel(null)).toBe("");
  });
});
