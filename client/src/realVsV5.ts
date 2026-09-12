// 이슈 #131 — 실매매 대조 탭 전용 타입/헬퍼.
// /api/validation/real-vs-v5 응답만 다룬다. 여기서 집계·추정을 하지 않고 서버가 낸 값을 표시 문자열로만 옮긴다.
// 없는 값은 0이 아니라 "—"로 적는다(미수집과 0은 다른 사실이다).

export type RealFillClass = "MATCHED" | "ENGINE_ONLY" | "USER_ONLY" | "SELL";

export type RealVsV5Row = {
  classification: RealFillClass | string;
  symbol: string;
  side: string;
  at: string;
  fillPrice: number | null;
  fillQuantity: number | null;
  eventId: string | null;
  v5State: string | null;
  kind: string | null;
  trendState: string | null;
  entryQuality: number | null;
  entryReference: number | null;
  deviationAtr: number | null;
  sellPosition: string | null;
  rejectionCodes: string[];
};

export type RealVsV5Summary = {
  tradingDate: string;
  windowMinutes: number;
  realBuys: number;
  realSells: number;
  matched: number;
  engineOnly: number;
  userOnly: number;
  matchRatePercent: number | null;
  deviationSamples: number;
  deviationMedianAtr: number | null;
  deviationQ1Atr: number | null;
  deviationQ3Atr: number | null;
  topUserOnlyRejections: { code: string; count: number }[];
  rows: RealVsV5Row[];
};

export type RealVsV5Data = {
  tradingDate: string;
  fillsCollected: boolean;
  collectedAt: string | null;
  observationLines: number;
  observationFileFound: boolean;
  report: RealVsV5Summary;
  limitations: string[];
};

export type Tile = { label: string; value: string; help: string };

/** 서버가 낸 분류 코드의 한국어 표기. 모르는 코드는 그대로 보여준다(조용히 삼키지 않는다). */
export function classLabel(value: string): string {
  if (value === "MATCHED") return "일치";
  if (value === "ENGINE_ONLY") return "엔진 단독";
  if (value === "USER_ONLY") return "사용자 단독";
  if (value === "SELL") return "매도";
  return value;
}

/** 실매도 체결가의 v5 stop/target 대비 위치. 열린 v5 거래가 없으면 미수집이다. */
export function sellPositionLabel(value: string | null | undefined): string {
  if (value === "ABOVE_TARGET") return "목표가 위";
  if (value === "BETWEEN") return "손절~목표 사이";
  if (value === "BELOW_STOP") return "손절가 아래";
  return "—";
}

export function limitationLabel(code: string): string {
  if (code === "REAL_FILLS_NOT_COLLECTED") return "그날 실체결이 아직 수집되지 않았습니다.";
  if (code === "REAL_FILLS_NO_OBSERVATIONS") return "그날 v5 관측 파일이 없어 대조할 근거가 없습니다.";
  return code;
}

export function atrText(value: number | null | undefined): string {
  return value == null ? "—" : `${value >= 0 ? "+" : ""}${value.toFixed(2)} ATR`;
}

export function priceText(value: number | null | undefined): string {
  return value == null ? "—" : value.toFixed(2);
}

/** 매칭률. 실매수가 0건이면 0%가 아니라 표본 없음이다. */
export function matchRateText(report: RealVsV5Summary): string {
  return report.matchRatePercent == null ? "—" : `${report.matchRatePercent.toFixed(1)}%`;
}

/** 요약 4칸. 분모(표본)를 항상 함께 적는다. */
export function summaryTiles(data: RealVsV5Data): Tile[] {
  const r = data.report;
  const quartiles =
    r.deviationQ1Atr == null || r.deviationQ3Atr == null
      ? "사분위 표본 없음"
      : `1사분위 ${atrText(r.deviationQ1Atr)} · 3사분위 ${atrText(r.deviationQ3Atr)}`;
  return [
    {
      label: "매칭률",
      value: matchRateText(r),
      help: `실매수 ${r.realBuys}건 중 ${r.matched}건이 v5 READY/ENTERED 동반 (±${r.windowMinutes}분)`,
    },
    { label: "엔진 단독", value: `${r.engineOnly}건`, help: "v5는 진입했는데 실매수가 없던 이벤트" },
    { label: "사용자 단독", value: `${r.userOnly}건`, help: "실매수했는데 v5 후보가 없거나 거절된 건" },
    {
      label: "중앙 괴리",
      value: atrText(r.deviationMedianAtr),
      help: `괴리 표본 ${r.deviationSamples}건 · ${quartiles}`,
    },
  ];
}

/** 표에 그릴 셀 값. 정렬·필터는 하지 않는다 — 서버가 낸 행 순서를 그대로 쓴다. */
export type RealVsV5TableRow = {
  key: string;
  at: string;
  symbol: string;
  side: string;
  price: string;
  state: string;
  deviation: string;
  note: string;
  muted: boolean;
};

export function tableRows(rows: RealVsV5Row[], formatAt: (value: string) => string): RealVsV5TableRow[] {
  return rows.map((row, index) => ({
    key: `${row.at}-${row.symbol}-${row.classification}-${index}`,
    at: formatAt(row.at),
    symbol: row.symbol,
    side: row.side === "SELL" ? "매도" : "매수",
    price: priceText(row.fillPrice),
    state: row.v5State ?? classLabel(row.classification),
    deviation: atrText(row.deviationAtr),
    note:
      row.classification === "SELL"
        ? sellPositionLabel(row.sellPosition)
        : row.rejectionCodes.length > 0
          ? row.rejectionCodes.join(", ")
          : (row.kind ?? "—"),
    muted: row.classification === "ENGINE_ONLY",
  }));
}
