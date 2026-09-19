import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import FeeWarningBadge from "./FeeWarningBadge";

describe("FeeWarningBadge", () => {
  it("warnings가 없으면 아무것도 렌더하지 않는다", () => {
    const { container } = render(<FeeWarningBadge warnings={[]} />);
    expect(container.firstChild).toBeNull();
  });

  it("warnings가 undefined여도 아무것도 렌더하지 않는다", () => {
    const { container } = render(<FeeWarningBadge />);
    expect(container.firstChild).toBeNull();
  });

  it("불일치 경고가 있으면 불일치 배지를 보여준다", () => {
    render(<FeeWarningBadge warnings={["V5_FEE_RATE_MISMATCH:policy=0.2;account=0.3"]} />);
    expect(screen.getByText(/수수료 불일치/)).toBeTruthy();
  });

  it("레거시 만료 경고만 있으면 배지를 보여주지 않는다", () => {
    render(<FeeWarningBadge warnings={["V5_FEE_RATE_EXPIRING:2026-09-13"]} />);
    expect(screen.queryByText(/수수료 만료 임박/)).toBeNull();
  });

  it("불일치와 레거시 만료 경고가 섞여도 만료 코드를 배지 설명에 노출하지 않는다", () => {
    render(
      <FeeWarningBadge
        warnings={["V5_FEE_RATE_MISMATCH:policy=0.2;account=0.3", "V5_FEE_RATE_EXPIRING:2026-09-13"]}
      />,
    );
    expect(screen.getByTitle("V5_FEE_RATE_MISMATCH:policy=0.2;account=0.3")).toBeTruthy();
  });
});
