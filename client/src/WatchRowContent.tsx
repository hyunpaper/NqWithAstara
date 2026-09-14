import type { ReactNode } from "react";

export type WatchChange = { label: string; tone: "up" | "down" };

/// <summary>관심종목 행 2줄 레이아웃 — 1행 티커·등락률, 2행 배지 (§UI, #224)</summary>
export function WatchRowContent({
  symbol,
  name,
  change,
  badges,
}: {
  symbol: string;
  name: string;
  change?: WatchChange | null;
  badges?: ReactNode[];
}) {
  return (
    <>
      <div className="watch-identity" title={name}>
        <b>{symbol}</b>
        {change && <span className={change.tone}>{change.label}</span>}
      </div>
      {badges && badges.length > 0 && <div className="watch-metrics">{badges}</div>}
    </>
  );
}
