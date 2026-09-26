import { useCallback, useEffect, useRef, useState } from "react";
import { normalizeArticle, type NewsArticle } from "./newsTypes";

export type NewsDetailState = "idle" | "loading" | "error" | "not-found";
export type NewsTranslationRequestState = "idle" | "requesting" | "queued" | "error";

const hasKoreanBody = (article: NewsArticle) => Boolean(article.contentKo ?? article.summaryKo);
const translationStatus = (article: NewsArticle) => article.contentTranslationStatus ?? article.summaryTranslationStatus ?? article.translationStatus ?? "";
const shouldRequestTranslation = (article: NewsArticle) => !hasKoreanBody(article)
  && !["pending", "quota_wait", "not_configured", "not_available", "not_needed"].includes(translationStatus(article));

const wait = (milliseconds: number, signal: AbortSignal) => new Promise<void>((resolve, reject) => {
  const timer = window.setTimeout(resolve, milliseconds);
  signal.addEventListener("abort", () => {
    window.clearTimeout(timer);
    reject(new DOMException("요청이 취소되었습니다.", "AbortError"));
  }, { once: true });
});

export function useNewsDetail() {
  const [selected, setSelected] = useState<NewsArticle | null>(null);
  const [detail, setDetail] = useState<NewsArticle | null>(null);
  const [state, setState] = useState<NewsDetailState>("idle");
  const [translationState, setTranslationState] = useState<NewsTranslationRequestState>("idle");
  const request = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const opener = useRef<HTMLElement | null>(null);
  const selectedSymbol = useRef<string | null>(null);
  const translationInFlight = useRef<string | null>(null);

  const fetchDetail = useCallback(async (article: NewsArticle, symbol: string | null, current: number, controller: AbortController) => {
    const query = new URLSearchParams({ id: article.id });
    if (symbol) query.set("symbol", symbol);
    const response = await fetch(`/api/news/detail?${query.toString()}`, { signal: controller.signal });
    if (response.status === 404) return null;
    if (!response.ok) throw new Error(`뉴스 상세 조회 실패 (${response.status})`);
    const normalized = normalizeArticle(await response.json());
    if (!normalized) throw new Error("뉴스 상세 응답 형식이 올바르지 않습니다.");
    if (generation.current !== current || controller.signal.aborted) return null;
    const merged = { ...article, ...normalized };
    setDetail(merged);
    return merged;
  }, []);

  const requestTranslation = useCallback(async (article: NewsArticle, automatic = false, enqueue = true) => {
    if (translationInFlight.current === article.id) return;
    const current = generation.current;
    const controller = request.current;
    if (!controller || controller.signal.aborted) return;
    translationInFlight.current = article.id;
    setTranslationState(enqueue ? "requesting" : "queued");
    try {
      if (enqueue) {
        const response = await fetch(`/api/news/translation?id=${encodeURIComponent(article.id)}`, {
          method: "POST",
          signal: controller.signal,
        });
        if (!response.ok) throw new Error(`뉴스 번역 요청 실패 (${response.status})`);
        if (generation.current !== current || controller.signal.aborted) return;
        setTranslationState("queued");
      }
      for (const delay of [800, 1600, 3200]) {
        await wait(delay, controller.signal);
        const refreshed = await fetchDetail(article, selectedSymbol.current, current, controller);
        if (refreshed && hasKoreanBody(refreshed)) {
          setTranslationState("idle");
          return;
        }
      }
      if (generation.current === current && !controller.signal.aborted) setTranslationState("idle");
    } catch (error) {
      if (generation.current !== current || controller.signal.aborted || (error as Error).name === "AbortError") return;
      setTranslationState("error");
      if (!automatic) setState("idle");
    } finally {
      if (translationInFlight.current === article.id) translationInFlight.current = null;
    }
  }, [fetchDetail]);

  const open = useCallback((article: NewsArticle, trigger?: HTMLElement | null, symbol?: string | null) => {
    request.current?.abort();
    const current = ++generation.current;
    const controller = new AbortController();
    request.current = controller;
    translationInFlight.current = null;
    selectedSymbol.current = symbol ?? null;
    opener.current = trigger ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    setSelected(article);
    setDetail(article);
    setState("loading");
    setTranslationState("idle");

    void fetchDetail(article, symbol ?? null, current, controller)
      .then((normalized) => {
        if (generation.current !== current || controller.signal.aborted) return;
        if (normalized === null) {
          setState("not-found");
          return;
        }
        setState("idle");
        if (shouldRequestTranslation(normalized)) void requestTranslation(normalized, true);
        else if (!hasKoreanBody(normalized) && translationStatus(normalized) === "pending") void requestTranslation(normalized, true, false);
      })
      .catch((error: unknown) => {
        if (generation.current !== current || controller.signal.aborted || (error as Error).name === "AbortError") return;
        setState("error");
      });
  }, [fetchDetail, requestTranslation]);

  const close = useCallback(() => {
    request.current?.abort();
    request.current = null;
    translationInFlight.current = null;
    generation.current += 1;
    setSelected(null);
    setDetail(null);
    setState("idle");
    setTranslationState("idle");
    const target = opener.current;
    opener.current = null;
    requestAnimationFrame(() => target?.focus());
  }, []);

  useEffect(() => () => {
    request.current?.abort();
    generation.current += 1;
  }, []);

  const retryTranslation = useCallback(() => {
    const article = detail ?? selected;
    if (article) void requestTranslation(article);
  }, [detail, requestTranslation, selected]);

  return { selected, detail, state, translationState, open, close, retryTranslation };
}
