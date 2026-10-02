import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Bot, Minus, MessageCircle, Send, Square, Trash2, X } from "lucide-react";
import {
  applyStreamEvent,
  CHAT_INPUT_LIMIT,
  clearChatState,
  createSseParser,
  formatLatency,
  formatModelSize,
  historyForRequest,
  loadChatState,
  newTurnId,
  parseMarkdownLite,
  resourceNotice,
  saveChatState,
  type ChatCheckResponse,
  type ChatModelItem,
  type ChatModelsResponse,
  type ChatResourceNotice,
  type ChatTurn,
  type MarkdownBlock,
  type MarkdownInline,
} from "./chatTypes";

// 이슈 #338: 우측 하단 플로팅 채팅창. 서버 프록시(/api/chat*)만 호출하며 Ollama 주소는 브라우저가 모른다.

type CheckState =
  | { state: "idle" }
  | { state: "checking" }
  | { state: "ok"; latencyMs: number; loadMs: number }
  | { state: "fail"; error: string };

type Props = {
  fetchImpl?: typeof fetch;
};

export default function ChatWidget({ fetchImpl }: Props) {
  const doFetch = fetchImpl ?? ((input: RequestInfo | URL, init?: RequestInit) => fetch(input, init));
  const stored = useMemo(() => loadChatState(), []);
  const [open, setOpen] = useState(false);
  const [minimized, setMinimized] = useState(false);
  const [models, setModels] = useState<ChatModelItem[]>([]);
  const [modelsStatus, setModelsStatus] = useState<"idle" | "loading" | "ok" | "error" | "disabled">("idle");
  const [modelsError, setModelsError] = useState<string | null>(null);
  const [model, setModel] = useState<string | null>(stored.model);
  const [resources, setResources] = useState<ChatResourceNotice | null>(null);
  const [warning, setWarning] = useState<string | null>(null);
  const [check, setCheck] = useState<CheckState>({ state: "idle" });
  const [turns, setTurns] = useState<ChatTurn[]>(stored.turns);
  const [input, setInput] = useState("");
  const [streaming, setStreaming] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const checkSeq = useRef(0);
  const logRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);

  useEffect(() => {
    saveChatState({ model, turns });
  }, [model, turns]);

  useEffect(() => {
    const log = logRef.current;
    if (log) log.scrollTop = log.scrollHeight;
  }, [turns, open, minimized]);

  const runCheck = useCallback(
    async (target: string) => {
      const seq = ++checkSeq.current;
      setCheck({ state: "checking" });
      try {
        const response = await doFetch("/api/chat/check", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ model: target }),
        });
        const body = (await response.json()) as ChatCheckResponse & { error?: string };
        if (seq !== checkSeq.current) return;
        if (!response.ok) {
          setCheck({ state: "fail", error: body?.error ?? `HTTP ${response.status}` });
          return;
        }
        setResources(body.resources ?? null);
        setWarning(body.warning ?? null);
        setCheck(body.ok ? { state: "ok", latencyMs: body.latencyMs, loadMs: body.loadMs } : { state: "fail", error: body.error ?? "연결 실패" });
      } catch (e) {
        if (seq !== checkSeq.current) return;
        setCheck({ state: "fail", error: e instanceof Error ? e.message : "연결 실패" });
      }
    },
    [doFetch],
  );

  const loadModels = useCallback(async () => {
    setModelsStatus("loading");
    setModelsError(null);
    try {
      const response = await doFetch("/api/chat/models");
      const body = (await response.json()) as ChatModelsResponse;
      setResources(body.resources ?? null);
      if (!body.enabled) {
        setModelsStatus("disabled");
        setModels([]);
        return;
      }
      setModels(body.models ?? []);
      if (body.status !== "ok") {
        setModelsStatus("error");
        setModelsError(body.error ?? "모델 목록을 가져오지 못했습니다.");
        setCheck({ state: "fail", error: body.error ?? "Ollama 연결 실패" });
        return;
      }
      setModelsStatus("ok");
      const names = new Set((body.models ?? []).map((m) => m.name));
      const chosen = model && names.has(model) ? model : body.defaultModel ?? body.models?.[0]?.name ?? null;
      setModel(chosen);
      if (chosen) void runCheck(chosen);
      else setCheck({ state: "fail", error: "설치된 모델이 없습니다. `ollama pull <모델>` 후 새로고침하세요." });
    } catch (e) {
      setModelsStatus("error");
      setModelsError(e instanceof Error ? e.message : "모델 목록을 가져오지 못했습니다.");
    }
  }, [doFetch, model, runCheck]);

  useEffect(() => {
    if (open && modelsStatus === "idle") void loadModels();
  }, [open, modelsStatus, loadModels]);

  const selectModel = (next: string) => {
    setModel(next);
    void runCheck(next);
  };

  const stop = () => {
    abortRef.current?.abort();
  };

  const send = async () => {
    const text = input.trim();
    if (!text || streaming || !model) return;
    const userTurn: ChatTurn = { id: newTurnId(), role: "user", content: text };
    const assistantTurn: ChatTurn = { id: newTurnId(), role: "assistant", content: "", pending: true };
    const history = [...historyForRequest(turns), { role: "user" as const, content: text }];
    setTurns((prev) => [...prev, userTurn, assistantTurn]);
    setInput("");
    setStreaming(true);
    const controller = new AbortController();
    abortRef.current = controller;
    const update = (fn: (turn: ChatTurn) => ChatTurn) => setTurns((prev) => prev.map((t) => (t.id === assistantTurn.id ? fn(t) : t)));
    try {
      const response = await doFetch("/api/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
        body: JSON.stringify({ model, messages: history }),
        signal: controller.signal,
      });
      if (!response.ok || !response.body) {
        let message = `HTTP ${response.status}`;
        try {
          const body = (await response.json()) as { error?: string };
          if (body?.error) message = body.error;
        } catch {
          /* 본문이 JSON이 아니면 상태 코드만 보여준다 */
        }
        update((t) => ({ ...t, pending: false, error: message }));
        return;
      }
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      const parser = createSseParser();
      let finished = false;
      while (!finished) {
        const { value, done } = await reader.read();
        const events = done ? parser.flush() : parser.push(decoder.decode(value, { stream: true }));
        for (const event of events) {
          update((t) => applyStreamEvent(t, event));
          if (event.type === "done" || event.type === "error") finished = true;
        }
        if (done) break;
      }
      update((t) => (t.pending ? { ...t, pending: false } : t));
    } catch (e) {
      const aborted = controller.signal.aborted;
      update((t) => ({ ...t, pending: false, error: aborted ? (t.content ? undefined : "중단되었습니다.") : e instanceof Error ? e.message : "요청 실패" }));
    } finally {
      setStreaming(false);
      abortRef.current = null;
    }
  };

  const clear = () => {
    stop();
    setTurns([]);
    clearChatState();
    saveChatState({ model, turns: [] });
  };

  const notices = resourceNotice(resources, model, warning);
  const canSend = !!model && check.state !== "checking" && check.state !== "fail" && !streaming && input.trim().length > 0;

  if (!open) {
    return (
      <button type="button" className="chat-fab" aria-label="AI 채팅 열기" title="AI 채팅" onClick={() => { setOpen(true); setMinimized(false); }}>
        <MessageCircle size={22} />
      </button>
    );
  }

  return (
    <section className={`chat-window ${minimized ? "chat-window--min" : ""}`} role="dialog" aria-label="AI 채팅" aria-modal="false">
      <header className="chat-head">
        <Bot size={16} />
        <span className="chat-title">AI 채팅</span>
        <ConnectionBadge check={check} />
        <div className="chat-head-actions">
          <button type="button" aria-label={minimized ? "채팅 펼치기" : "채팅 최소화"} onClick={() => setMinimized((v) => !v)}>
            <Minus size={15} />
          </button>
          <button type="button" aria-label="채팅 닫기" onClick={() => setOpen(false)}>
            <X size={15} />
          </button>
        </div>
      </header>
      {!minimized && (
        <>
          <div className="chat-toolbar">
            <label className="chat-model">
              <span>모델</span>
              <select
                aria-label="모델 선택"
                value={model ?? ""}
                disabled={modelsStatus !== "ok" || streaming}
                onChange={(e) => selectModel(e.target.value)}
              >
                {models.length === 0 && <option value="">{modelsStatus === "loading" ? "불러오는 중…" : "모델 없음"}</option>}
                {models.map((m) => (
                  <option key={m.name} value={m.name}>
                    {m.name}
                    {m.parameterSize ? ` · ${m.parameterSize}` : ""}
                    {m.sizeBytes ? ` · ${formatModelSize(m.sizeBytes)}` : ""}
                    {m.isNewsModel ? " · 뉴스" : ""}
                  </option>
                ))}
              </select>
            </label>
            <button type="button" className="chat-icon-btn" aria-label="대화 지우기" title="대화 지우기" onClick={clear} disabled={turns.length === 0 && !streaming}>
              <Trash2 size={15} />
            </button>
          </div>
          {modelsStatus === "disabled" && <div className="chat-notice chat-notice--error">채팅 기능이 서버 설정에서 꺼져 있습니다(Chat:Enabled).</div>}
          {modelsStatus === "error" && (
            <div className="chat-notice chat-notice--error">
              {modelsError}
              <button type="button" onClick={() => void loadModels()}>다시 시도</button>
            </div>
          )}
          {check.state === "fail" && modelsStatus === "ok" && <div className="chat-notice chat-notice--error">{check.error}</div>}
          {notices.map((note) => (
            <div key={note} className="chat-notice">{note}</div>
          ))}
          <div className="chat-log" ref={logRef} aria-live="polite">
            {turns.length === 0 && <div className="chat-empty">모델을 고르고 질문을 입력하세요. 답변은 한국어로 옵니다.</div>}
            {turns.map((turn) => (
              <ChatBubble key={turn.id} turn={turn} />
            ))}
          </div>
          <form
            className="chat-compose"
            onSubmit={(e) => {
              e.preventDefault();
              void send();
            }}
          >
            <textarea
              ref={inputRef}
              aria-label="메시지 입력"
              placeholder={model ? "메시지 입력 (Enter 전송 · Shift+Enter 줄바꿈)" : "모델을 먼저 선택하세요"}
              value={input}
              maxLength={CHAT_INPUT_LIMIT}
              rows={2}
              disabled={!model || modelsStatus !== "ok"}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
                  e.preventDefault();
                  if (canSend) void send();
                }
              }}
            />
            {streaming ? (
              <button type="button" className="chat-send" aria-label="생성 중단" onClick={stop}>
                <Square size={16} />
              </button>
            ) : (
              <button type="submit" className="chat-send" aria-label="전송" disabled={!canSend}>
                <Send size={16} />
              </button>
            )}
          </form>
        </>
      )}
    </section>
  );
}

function ConnectionBadge({ check }: { check: CheckState }) {
  switch (check.state) {
    case "idle":
      return <span className="chat-badge chat-badge--idle">대기</span>;
    case "checking":
      return <span className="chat-badge chat-badge--checking" role="status">연결 확인 중…</span>;
    case "ok":
      return (
        <span className="chat-badge chat-badge--ok" role="status" title={check.loadMs > 0 ? `모델 로딩 ${formatLatency(check.loadMs)} 포함` : undefined}>
          연결됨 {formatLatency(check.latencyMs)}
        </span>
      );
    case "fail":
      return (
        <span className="chat-badge chat-badge--fail" role="status" title={check.error}>
          연결 실패
        </span>
      );
  }
}

function ChatBubble({ turn }: { turn: ChatTurn }) {
  const blocks = useMemo(() => (turn.role === "assistant" ? parseMarkdownLite(turn.content) : []), [turn.role, turn.content]);
  return (
    <div className={`chat-turn chat-turn--${turn.role}`}>
      {turn.role === "user" ? (
        <div className="chat-bubble">{turn.content}</div>
      ) : (
        <div className="chat-bubble">
          {turn.thinking && (
            <details className="chat-think">
              <summary>추론 과정 {turn.pending && !turn.content ? "(진행 중)" : ""}</summary>
              <pre>{turn.thinking}</pre>
            </details>
          )}
          {turn.content ? <Markdown blocks={blocks} /> : turn.pending && !turn.thinking ? <span className="chat-typing">생각 중…</span> : null}
          {turn.pending && turn.content && <span className="chat-cursor" aria-hidden="true" />}
          {turn.error && <div className="chat-turn-error" role="alert">{turn.error}</div>}
          {!turn.pending && !turn.error && typeof turn.totalMs === "number" && turn.totalMs > 0 && (
            <div className="chat-turn-meta">{formatLatency(turn.totalMs)}</div>
          )}
        </div>
      )}
    </div>
  );
}

function Markdown({ blocks }: { blocks: MarkdownBlock[] }) {
  return (
    <>
      {blocks.map((block, i) => {
        switch (block.kind) {
          case "code":
            return (
              <pre key={i} className="chat-code" data-lang={block.lang || undefined}>
                <code>{block.text}</code>
              </pre>
            );
          case "heading":
            return (
              <p key={i} className={`chat-h chat-h${block.level}`}>
                <Inline parts={block.inline} />
              </p>
            );
          case "list":
            return block.ordered ? (
              <ol key={i}>
                {block.items.map((item, j) => (
                  <li key={j}>
                    <Inline parts={item} />
                  </li>
                ))}
              </ol>
            ) : (
              <ul key={i}>
                {block.items.map((item, j) => (
                  <li key={j}>
                    <Inline parts={item} />
                  </li>
                ))}
              </ul>
            );
          case "paragraph":
            return (
              <p key={i}>
                {block.lines.map((line, j) => (
                  <span key={j}>
                    {j > 0 && <br />}
                    <Inline parts={line} />
                  </span>
                ))}
              </p>
            );
        }
      })}
    </>
  );
}

function Inline({ parts }: { parts: MarkdownInline[] }) {
  return (
    <>
      {parts.map((part, i) =>
        part.kind === "code" ? <code key={i}>{part.text}</code> : part.kind === "bold" ? <strong key={i}>{part.text}</strong> : <span key={i}>{part.text}</span>,
      )}
    </>
  );
}
