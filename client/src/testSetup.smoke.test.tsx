// 테스트 러너 도입 스모크 — vitest(jsdom) + Testing Library + JSX 변환 파이프라인이
// 동작하는지 자체 컴포넌트로 확인한다. 제품 코드 테스트는 별도 파일에서 다룬다.
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

function Smoke({ label }: { label: string }) {
  return <p>스모크: {label}</p>;
}

describe("테스트 러너 스모크", () => {
  it("jsdom 환경에서 컴포넌트를 렌더하고 조회한다", () => {
    render(<Smoke label="정상" />);
    expect(screen.getByText("스모크: 정상").textContent).toBe("스모크: 정상");
  });
});
