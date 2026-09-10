import { useEffect, useRef } from "react";

export function useVisiblePolling(
  task: () => Promise<void>,
  intervalMs: number,
  enabled = true,
) {
  const taskRef = useRef(task);
  taskRef.current = task;

  useEffect(() => {
    if (!enabled) return;
    let active = true;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const schedule = () => {
      if (timer) clearTimeout(timer);
      if (active && document.visibilityState === "visible")
        timer = setTimeout(run, intervalMs);
    };
    const run = async () => {
      if (!active || inFlight || document.visibilityState !== "visible") return;
      inFlight = true;
      try {
        await taskRef.current();
      } catch {
        /* a later poll retries */
      } finally {
        inFlight = false;
        schedule();
      }
    };
    const onVisibility = () => {
      if (timer) clearTimeout(timer);
      timer = undefined;
      if (document.visibilityState === "visible" && !inFlight) void run();
    };

    if (document.visibilityState === "visible") void run();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      active = false;
      if (timer) clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [enabled, intervalMs]);
}
