export type View = "live" | "dash" | "structure";

const SYMBOL_RE = /^[A-Z0-9.-]+$/;

export function parseViewHash(hash: string): { view: View; symbol?: string } {
  const raw = hash.replace(/^#/, "");
  if (raw === "dash") return { view: "dash" };
  if (raw === "structure") return { view: "structure" };
  if (raw.startsWith("structure/")) {
    const symbol = raw.slice("structure/".length);
    return SYMBOL_RE.test(symbol) ? { view: "structure", symbol } : { view: "structure" };
  }
  return { view: "live" };
}

export function formatViewHash(view: View, symbol?: string): string {
  if (view === "dash") return "#dash";
  if (view === "structure")
    return symbol && SYMBOL_RE.test(symbol) ? `#structure/${symbol}` : "#structure";
  return "#";
}
