import { useEffect, useMemo, useState } from "react";
import { Layers } from "lucide-react";
import StructureChart, { ChartBar } from "./StructureChart";
import PlanExplanation from "./PlanExplanation";
// 이슈 #26: 라이브 목록(App)과 같은 비교 함수를 공용 모듈에서 가져와 두 화면의 정렬이 갈라지지 않게 한다(§3).
import { compareByTrendStrength, compareByV5State } from "./structureSort";
import {
  StructureResponse,
  StructureSummary,
  StructureSummaryRow,
  arr,
  candidateGroup,
  candidateStateLabel,
  clock,
  num1,
  price,
  setupKindLabel,
  statusLabel,
  trendStateLabel,
} from "./structureTypes";

// v5 구조 엔진 D4 — 분리된 구조 분석 뷰(설계 §13).
//
// 이 화면은 v4 점수/배지/알림과 **시각적으로 분리된 별도 탭**이다.
// - v5 숫자를 v4 scoreStyle 색상이나 BUY 의미에 연결하지 않는다(§19-10).
// - 여기서는 어떤 알림도 발생시키지 않는다. shadow 후보를 새 진입 알림으로 보내지 않는다(§13).
// - 조회는 마지막 공개 snapshot만 읽는다. 화면이 새 분석이나 거래를 유발하지 않는다(§12).

type Props = {
  /** 관심종목(이름 표시에 사용). */
  watchlist: { symbol: string; name: string }[];
  /** `/api/state`의 additive structureSummary. 없으면(구버전 서버) 목록은 상태 미상으로 표시한다. */
  summary?: StructureSummary | null;
  selected: string;
  onSelect: (symbol: string) => void;
  /** 선택 종목의 v4 차트 배열(최근 완료 1분봉). 구조 차트의 배경으로만 쓴다. */
  bars: ChartBar[];
};

type Fetched = {
  symbol: string;
  /** HTTP 계층 결과. 404/400은 본문이 없으므로 상태 코드로만 구분한다(§12). */
  http: "ok" | "notFound" | "badRequest" | "error";
  body: StructureResponse | null;
  errorText: string;
};

const POLL_MS = 15000;

const trendClass = (state: string | null | undefined) => {
  switch ((state ?? "").toUpperCase()) {
    case "UP":
      return "up";
    case "DOWN":
      return "down";
    case "RANGE":
      return "range";
    case "TRANSITION":
      return "transition";
    default:
      return "unknown";
  }
};

export default function StructurePanel({
  watchlist,
  summary,
  selected,
  onSelect,
  bars,
}: Props) {
  const [fetched, setFetched] = useState<Fetched | null>(null);
  const [sort, setSort] = useState<"ready" | "trend">("ready");
  const [pickedCandidate, setPickedCandidate] = useState<string | null>(null);

  // 종목이 바뀌면 이전 종목의 후보 선택을 끌고 오지 않는다.
  useEffect(() => setPickedCandidate(null), [selected]);

  useEffect(() => {
    if (!selected) {
      setFetched(null);
      return;
    }
    let active = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let request: AbortController | undefined;

    const schedule = () => {
      if (timer) clearTimeout(timer);
      if (active && document.visibilityState === "visible") timer = setTimeout(run, POLL_MS);
    };
    const run = async () => {
      if (!active || inFlight || document.visibilityState !== "visible") return;
      inFlight = true;
      request = new AbortController();
      try {
        const response = await fetch(`/api/structure/${encodeURIComponent(selected)}`, {
          signal: request.signal,
        });
        // 404 = 관심종목에 없는 종목, 400 = 형식 오류. 둘 다 본문이 없다(§12).
        if (response.status === 404) {
          if (active) setFetched({ symbol: selected, http: "notFound", body: null, errorText: "" });
          return;
        }
        if (response.status === 400) {
          if (active)
            setFetched({ symbol: selected, http: "badRequest", body: null, errorText: "" });
          return;
        }
        if (!response.ok) {
          if (active)
            setFetched({
              symbol: selected,
              http: "error",
              body: null,
              errorText: `구조 분석 조회 실패 (${response.status})`,
            });
          return;
        }
        const body = (await response.json()) as StructureResponse;
        // 응답 지연 중 종목이 바뀐 경우 이전 응답을 반영하지 않는다.
        if (active && body?.symbol?.toUpperCase() === selected.toUpperCase())
          setFetched({ symbol: selected, http: "ok", body, errorText: "" });
      } catch (e) {
        if (active && (e as Error).name !== "AbortError")
          setFetched({
            symbol: selected,
            http: "error",
            body: null,
            errorText:
              e instanceof Error ? e.message : "구조 분석을 불러오지 못했습니다.",
          });
      } finally {
        inFlight = false;
        request = undefined;
        schedule();
      }
    };
    const visibility = () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
      if (document.visibilityState === "hidden") request?.abort();
      else if (!inFlight) void run();
    };

    setFetched(null);
    void run();
    document.addEventListener("visibilitychange", visibility);
    return () => {
      active = false;
      if (timer) clearTimeout(timer);
      request?.abort();
      document.removeEventListener("visibilitychange", visibility);
    };
  }, [selected]);

  const rows = useMemo<StructureSummaryRow[]>(() => {
    const bySymbol = new Map<string, StructureSummaryRow>();
    arr(summary?.symbols).forEach((row) => {
      if (row?.symbol) bySymbol.set(row.symbol.toUpperCase(), row);
    });
    return watchlist.map(
      (item) =>
        bySymbol.get(item.symbol.toUpperCase()) ?? {
          symbol: item.symbol,
          status: null,
          candidateState: null,
        },
    );
  }, [summary, watchlist]);

  const nameOf = (symbol: string) =>
    watchlist.find((w) => w.symbol === symbol)?.name ?? "";

  const ordered = useMemo(() => {
    // 기본 정렬: v5 상태 우선순위 → EntryQuality ↓ → |SignedTrend| ↓ → symbol(§13·이슈 #26 §3).
    // 추세 강도 정렬은 방향이 아니라 |SignedTrend| 크기다. 비교 함수는 App 라이브 목록과 공용이다.
    const copy = [...rows];
    copy.sort(sort === "trend" ? compareByTrendStrength : compareByV5State);
    return copy;
  }, [rows, sort]);

  const groups: { key: "ready" | "wait" | "reject" | "none"; title: string; hint: string }[] = [
    { key: "ready", title: "READY", hint: "구조 계획이 성립한 후보" },
    { key: "wait", title: "대기", hint: "트리거·확인 대기 또는 진입 처리됨" },
    { key: "reject", title: "부적합", hint: "거절·무효화·만료" },
    { key: "none", title: "상태 없음", hint: "분석 미표시(워밍업·off·정규장 외 등)" },
  ];

  const body = fetched?.body ?? null;
  const analysis = body?.analysis ?? null;
  const candidates = arr(analysis?.candidates);
  const candidate =
    candidates.find((c) => c.eventId === pickedCandidate) ??
    candidates.find((c) => c.eventId === analysis?.preferredCandidateId) ??
    candidates[0] ??
    null;

  const detailNotice = (() => {
    if (!selected) return "왼쪽에서 종목을 선택하세요.";
    if (!fetched) return "구조 분석을 불러오는 중…";
    if (fetched.http === "notFound")
      return `${selected}은(는) 관심종목에 없습니다. 먼저 관심종목에 추가하세요. (HTTP 404)`;
    if (fetched.http === "badRequest")
      return `종목 코드 형식이 올바르지 않습니다. (HTTP 400)`;
    if (fetched.http === "error") return `${fetched.errorText} · ${POLL_MS / 1000}초 후 재시도`;
    if (!body) return "응답이 비어 있습니다.";
    if (!analysis)
      return `${statusLabel(body.status)}${body.message ? ` — ${body.message}` : ""}`;
    return null;
  })();

  return (
    <div className="structure-view">
      <section className="panel structure-head-panel">
        <div className="panel-head">
          <div>
            <h2>구조 분석 (v5)</h2>
          </div>
          <Layers size={18} />
        </div>
        <div className="structure-mode">
          <span>
            엔진 모드 <b>{summary?.mode ?? body?.mode ?? "확인 중"}</b>
          </span>
          <span>
            신규 진입 소유 <b>{summary?.entryOwner ?? body?.entryOwner ?? "—"}</b>
          </span>
          <span>
            버전 <b>{summary?.engineVersion ?? body?.engineVersion ?? "—"}</b>
          </span>
          <span>
            정책 <b>{(summary?.policyHash ?? body?.policyHash ?? "").slice(0, 8) || "—"}</b>
          </span>
          <span>
            갱신 <b>{clock(body?.updatedAt)}</b>
          </span>
        </div>
        {arr(summary?.notes).length > 0 && (
          <p className="structure-mode-note">{arr(summary?.notes).join(" · ")}</p>
        )}
        {/* 이슈 #29: `/api/state` 계약 설명 대신 짧은 상태만 남긴다. 결측을 정상처럼 보이게 하지 않는다. */}
        {!summary && <p className="structure-mode-note">목록 요약 없음 — 종목별 조회 상태만 표시</p>}
      </section>

      <div className="structure-body">
        <section className="panel structure-list">
          <div className="panel-head">
            <div>
              <h2>종목별 구조 상태</h2>
            </div>
            <div className="structure-sort">
              <button
                className={sort === "ready" ? "on" : ""}
                onClick={() => setSort("ready")}
                title="v5 상태 우선 → 진입 품질 → 추세 강도 → 심볼"
              >
                기본
              </button>
              <button
                className={sort === "trend" ? "on" : ""}
                onClick={() => setSort("trend")}
                title="추세 강도(절대값) 내림차순 → 심볼"
              >
                추세 강도
              </button>
            </div>
          </div>
          {rows.length === 0 ? (
            <p className="structure-none">관심종목이 없습니다.</p>
          ) : (
            groups.map((group) => {
              const items = ordered.filter(
                (row) => candidateGroup(row.candidateState) === group.key,
              );
              if (items.length === 0) return null;
              return (
                <div className="structure-group" key={group.key}>
                  <div className="structure-group-head">
                    <b>{group.title}</b>
                    <span>
                      {items.length}종목 · {group.hint}
                    </span>
                  </div>
                  {items.map((row) => (
                    <button
                      key={row.symbol}
                      className={`structure-row ${selected === row.symbol ? "selected" : ""}`}
                      onClick={() => onSelect(row.symbol)}
                    >
                      <div className="sr-symbol">
                        <b>{row.symbol}</b>
                        <small>{nameOf(row.symbol)}</small>
                      </div>
                      <div className={`sr-trend ${trendClass(row.trendState)}`}>
                        <small>추세</small>
                        <b>{trendStateLabel(row.trendState)}</b>
                        <span>
                          강도 {row.signedTrend == null ? "—" : num1(row.signedTrend)}
                        </span>
                      </div>
                      <div className="sr-quality">
                        <small>진입 품질</small>
                        <b>{row.entryQuality == null ? "—" : num1(row.entryQuality)}</b>
                        {/* 이슈 #29: 지표 범위(0~100)는 유지하고 반복 교육 문구만 뺀다. */}
                        <span>0~100</span>
                      </div>
                      <div className="sr-state">
                        <span className={`se-state ${candidateGroup(row.candidateState)}`}>
                          {candidateStateLabel(row.candidateState)}
                        </span>
                        <small>{statusLabel(row.status)}</small>
                      </div>
                    </button>
                  ))}
                </div>
              );
            })
          )}
        </section>

        <section className="panel structure-detail">
          <div className="panel-head">
            <div>
              <h2>{selected || "종목 미선택"} 구조 상세</h2>
              {analysis && (
                <p>
                  {statusLabel(analysis.status ?? body?.status)} · 세션{" "}
                  {clock(analysis.sessionStart)}~{clock(analysis.sessionEnd)}
                </p>
              )}
            </div>
          </div>

          {detailNotice ? (
            <p className="structure-none">{detailNotice}</p>
          ) : (
            <>
              {candidates.length > 1 && (
                <div className="structure-candidate-tabs">
                  {candidates.map((c) => (
                    <button
                      key={c.eventId}
                      className={c.eventId === candidate?.eventId ? "on" : ""}
                      onClick={() => setPickedCandidate(c.eventId)}
                      title={c.eventId}
                    >
                      {setupKindLabel(c.kind)} · {candidateStateLabel(c.state)}
                      {c.eventId === analysis?.preferredCandidateId && " · 대표"}
                    </button>
                  ))}
                </div>
              )}
              <div className="structure-quick">
                <div>
                  <small>추세</small>
                  <b>{trendStateLabel(analysis?.trend?.state)}</b>
                  <span>
                    강도{" "}
                    {analysis?.trend?.signedTrend == null
                      ? "산출 불가"
                      : num1(analysis.trend.signedTrend)}
                  </span>
                </div>
                <div>
                  <small>진입 품질</small>
                  <b>
                    {candidate?.entryQuality == null
                      ? "산출 불가"
                      : num1(candidate.entryQuality)}
                  </b>
                  {/* 이슈 #29: 지표 성격(순위 지표)·범위는 유지, '확률 아님' 반복 교육은 뺀다. */}
                  <span>0~100 순위 지표</span>
                </div>
                <div>
                  <small>후보 상태</small>
                  <b>{candidateStateLabel(candidate?.state ?? analysis?.candidateSummary)}</b>
                  <span>{candidate ? setupKindLabel(candidate.kind) : "후보 없음"}</span>
                </div>
                <div>
                  <small>실시간 호가</small>
                  <b>{price(analysis?.quotePrice)}</b>
                  <span>{clock(analysis?.quoteAt)}</span>
                </div>
                <div>
                  <small>완료 봉 AsOf</small>
                  <b>{clock(analysis?.analysisAsOf)}</b>
                  <span>후보 cutoff {clock(analysis?.lastCompletedBarStart)}</span>
                </div>
              </div>

              <StructureChart bars={bars} analysis={analysis} candidate={candidate} />
              <PlanExplanation analysis={analysis} candidate={candidate} />
            </>
          )}
        </section>
      </div>
      {/* 이슈 #29: 반복 교육 문단은 뺐지만 투자 권유 아님·알림 없음 고지는 한 줄로 남긴다. */}
      <p className="structure-footer">이 화면은 알림을 발생시키지 않으며 투자 권유가 아닙니다.</p>
    </div>
  );
}
