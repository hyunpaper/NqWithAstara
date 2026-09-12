import { describe, expect, it } from "vitest";
import { formatViewHash, parseViewHash } from "./viewRoute";

describe("parseViewHash", () => {
  it("maps #dash to dash", () => {
    expect(parseViewHash("#dash")).toEqual({ view: "dash" });
  });

  it("maps #structure to structure", () => {
    expect(parseViewHash("#structure")).toEqual({ view: "structure" });
  });

  it("maps #structure/SYMBOL to structure with symbol", () => {
    expect(parseViewHash("#structure/PLTR")).toEqual({
      view: "structure",
      symbol: "PLTR",
    });
  });

  it("maps empty hash to live", () => {
    expect(parseViewHash("")).toEqual({ view: "live" });
  });

  it("maps unknown hash to live", () => {
    expect(parseViewHash("#unknown")).toEqual({ view: "live" });
  });

  it("ignores invalid symbol characters and falls back to structure only", () => {
    expect(parseViewHash("#structure/pltr!")).toEqual({ view: "structure" });
  });

  it("ignores lowercase symbols", () => {
    expect(parseViewHash("#structure/pltr")).toEqual({ view: "structure" });
  });

  it("accepts symbols with dot and dash", () => {
    expect(parseViewHash("#structure/BRK.A")).toEqual({
      view: "structure",
      symbol: "BRK.A",
    });
    expect(parseViewHash("#structure/BF-B")).toEqual({
      view: "structure",
      symbol: "BF-B",
    });
  });
});

describe("formatViewHash", () => {
  it("round-trips dash", () => {
    expect(parseViewHash(formatViewHash("dash"))).toEqual({ view: "dash" });
  });

  it("round-trips structure without symbol", () => {
    expect(parseViewHash(formatViewHash("structure"))).toEqual({
      view: "structure",
    });
  });

  it("round-trips structure with symbol", () => {
    expect(parseViewHash(formatViewHash("structure", "PLTR"))).toEqual({
      view: "structure",
      symbol: "PLTR",
    });
  });

  it("round-trips live", () => {
    expect(parseViewHash(formatViewHash("live"))).toEqual({ view: "live" });
  });

  it("drops invalid symbol when formatting structure", () => {
    expect(formatViewHash("structure", "pltr!")).toBe("#structure");
  });
});
