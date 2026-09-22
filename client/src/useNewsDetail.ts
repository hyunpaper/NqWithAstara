import { useCallback, useEffect, useRef, useState } from "react";
import { normalizeArticle, type NewsArticle } from "./newsTypes";

export type NewsDetailState = "idle" | "loading" | "error" | "not-found";

export function useNewsDetail() {
  const [selected, setSelected] = useState<NewsArticle | null>(null);
  const [detail, setDetail] = useState<NewsArticle | null>(null);
  const [state, setState] = useState<NewsDetailState>("idle");
  const request = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const opener = useRef<HTMLElement | null>(null);

  const open = useCallback((article: NewsArticle, trigger?: HTMLElement | null, symbol?: string | null) => {
    request.current?.abort();
    const current = ++generation.current;
    const controller = new AbortController();
    request.current = controller;
    opener.current = trigger ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    setSelected(article);
    setDetail(article);
    setState("loading");

    const query = new URLSearchParams({ id: article.id });
    if (symbol) query.set("symbol", symbol);
    void fetch(`/api/news/detail?${query.toString()}`, { signal: controller.signal })
      .then(async (response) => {
        if (response.status === 404) {
          if (generation.current === current && !controller.signal.aborted) setState("not-found");
          return null;
        }
        if (!response.ok) throw new Error(`뉴스 상세 조회 실패 (${response.status})`);
        return response.json();
      })
      .then((raw) => {
        if (raw === null || generation.current !== current || controller.signal.aborted) return;
        const normalized = normalizeArticle(raw);
        if (!normalized) throw new Error("뉴스 상세 응답 형식이 올바르지 않습니다.");
        setDetail({ ...article, ...normalized });
        setState("idle");
      })
      .catch((error: unknown) => {
        if (generation.current !== current || controller.signal.aborted || (error as Error).name === "AbortError") return;
        setState("error");
      });
  }, []);

  const close = useCallback(() => {
    request.current?.abort();
    request.current = null;
    generation.current += 1;
    setSelected(null);
    setDetail(null);
    setState("idle");
    const target = opener.current;
    opener.current = null;
    requestAnimationFrame(() => target?.focus());
  }, []);

  useEffect(() => () => {
    request.current?.abort();
    generation.current += 1;
  }, []);

  return { selected, detail, state, open, close };
}
