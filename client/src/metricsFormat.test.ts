import { describe, expect, it } from "vitest";
import { turnoverText } from "./metricsFormat";

describe("turnoverText", () => {
  it("formats a percent with two decimals", () => {
    expect(turnoverText(1.234)).toBe("1.23%");
    expect(turnoverText(0)).toBe("0.00%");
  });

  it("shows a dash when the metadata is missing", () => {
    expect(turnoverText(null)).toBe("—");
    expect(turnoverText(undefined)).toBe("—");
    expect(turnoverText(Number.NaN)).toBe("—");
  });
});
