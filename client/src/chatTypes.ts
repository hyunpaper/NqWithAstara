// 이슈 #338: 우측 하단 Ollama 채팅창의 타입·SSE 파서·로컬 보존·마크다운 최소 토큰화(순수 함수).

export type ChatRole = "user" | "assistant";

export type ChatModelItem = {
  name: string;
  sizeBytes: number;
  parameterSize: string | null;
  family: string | null;
  isNewsModel: boolean;
};

export type ChatResourceNotice = {
  newsEnabled: boolean;
  newsModel: string;
  newsQueue: number;
  chatBusy: boolean;
};

export type ChatModelsResponse = {
  enabled: boolean;
  status: "ok" | "error" | "disabled";
  error: string | null;
  models: ChatModelItem[];
  defaultModel: string | null;
  resources: ChatResourceNotice;
};

export type ChatCheckResponse = {
  ok: boolean;
  model: string;
  latencyMs: number;
  loadMs: number;
  error: string | null;
  errorKind: string | null;
  warning: string | null;
  resources: ChatResourceNotice;
};

export type ChatStreamEvent =
  | { type: "delta"; content: string }
  | { type: "thinking"; thinking: string }
  | { type: "done"; totalMs: number | null; outputTokens: number | null }
  | { type: "error"; error: string; errorKind: string | null };

export type ChatTurn = {
  id: string;
  role: ChatRole;
  content: string;
  thinking?: string;
  error?: string;
  pending?: boolean;
  totalMs?: number | null;
};

export type ChatStoredState = {
  model: string | null;
  turns: ChatTurn[];
};

export const CHAT_STORAGE_KEY = "astra-chat-v1";
export const CHAT_HISTORY_TURNS = 8;
export const CHAT_INPUT_LIMIT = 2000;

export function createSseParser() {
  let buffer = "";
  return {
    push(chunk: string): ChatStreamEvent[] {
      buffer += chunk.replace(/\r\n/g, "\n");
      const events: ChatStreamEvent[] = [];
      let index = buffer.indexOf("\n\n");
      while (index >= 0) {
        const block = buffer.slice(0, index);
        buffer = buffer.slice(index + 2);
        const parsed = parseSseBlock(block);
        if (parsed) events.push(parsed);
        index = buffer.indexOf("\n\n");
      }
      return events;
    },
    flush(): ChatStreamEvent[] {
      const parsed = parseSseBlock(buffer);
      buffer = "";
      return parsed ? [parsed] : [];
    },
  };
}

function parseSseBlock(block: string): ChatStreamEvent | null {
  const data = block
    .split("\n")
    .filter((line) => line.startsWith("data:"))
    .map((line) => line.slice(5).trimStart())
    .join("\n");
  if (!data) return null;
  try {
    const raw = JSON.parse(data) as Record<string, unknown>;
    switch (raw.type) {
      case "delta":
        return { type: "delta", content: String(raw.content ?? "") };
      case "thinking":
        return { type: "thinking", thinking: String(raw.thinking ?? "") };
      case "done":
        return {
          type: "done",
          totalMs: typeof raw.totalMs === "number" ? raw.totalMs : null,
          outputTokens: typeof raw.outputTokens === "number" ? raw.outputTokens : null,
        };
      case "error":
        return { type: "error", error: String(raw.error ?? "알 수 없는 오류"), errorKind: typeof raw.errorKind === "string" ? raw.errorKind : null };
      default:
        return null;
    }
  } catch {
    return null;
  }
}

export function applyStreamEvent(turn: ChatTurn, event: ChatStreamEvent): ChatTurn {
  switch (event.type) {
    case "delta":
      return { ...turn, content: turn.content + event.content };
    case "thinking":
      return { ...turn, thinking: (turn.thinking ?? "") + event.thinking };
    case "done":
      return { ...turn, pending: false, totalMs: event.totalMs };
    case "error":
      return { ...turn, pending: false, error: event.error };
  }
}

export function historyForRequest(turns: ChatTurn[], maxTurns = CHAT_HISTORY_TURNS): { role: ChatRole; content: string }[] {
  const usable = turns.filter((t) => !t.pending && !t.error && t.content.trim().length > 0);
  return usable.slice(-maxTurns * 2).map((t) => ({ role: t.role, content: t.content }));
}

export function loadChatState(storage: Pick<Storage, "getItem"> | null = safeStorage()): ChatStoredState {
  const empty: ChatStoredState = { model: null, turns: [] };
  if (!storage) return empty;
  try {
    const raw = storage.getItem(CHAT_STORAGE_KEY);
    if (!raw) return empty;
    const parsed = JSON.parse(raw) as Partial<ChatStoredState>;
    const turns = Array.isArray(parsed.turns)
      ? parsed.turns
          .filter((t): t is ChatTurn => !!t && (t.role === "user" || t.role === "assistant") && typeof t.content === "string")
          .map((t) => ({ ...t, pending: false, id: typeof t.id === "string" ? t.id : newTurnId() }))
          .slice(-100)
      : [];
    return { model: typeof parsed.model === "string" ? parsed.model : null, turns };
  } catch {
    return empty;
  }
}

export function saveChatState(state: ChatStoredState, storage: Pick<Storage, "setItem"> | null = safeStorage()): boolean {
  if (!storage) return false;
  try {
    const turns = state.turns.filter((t) => !t.pending).slice(-100).map(({ id, role, content, thinking, error, totalMs }) => ({ id, role, content, thinking, error, totalMs }));
    storage.setItem(CHAT_STORAGE_KEY, JSON.stringify({ model: state.model, turns }));
    return true;
  } catch {
    return false;
  }
}

export function clearChatState(storage: Pick<Storage, "removeItem"> | null = safeStorage()) {
  try {
    storage?.removeItem(CHAT_STORAGE_KEY);
  } catch {
    /* 저장소 접근 실패는 무시한다 */
  }
}

function safeStorage(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    return null;
  }
}

export function newTurnId() {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export function formatLatency(ms: number) {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
}

export function formatModelSize(bytes: number) {
  if (!bytes || bytes <= 0) return "";
  const gb = bytes / 1024 ** 3;
  return gb >= 1 ? `${gb.toFixed(1)} GB` : `${Math.round(bytes / 1024 ** 2)} MB`;
}

export function resourceNotice(resources: ChatResourceNotice | null, model: string | null, warning: string | null) {
  const notes: string[] = [];
  if (warning) notes.push(warning);
  if (resources?.newsEnabled && resources.newsQueue > 0) notes.push(`뉴스 분류 대기 ${resources.newsQueue}건 — 같은 Ollama를 쓰므로 응답이 지연될 수 있습니다.`);
  if (!warning && resources?.newsEnabled && model && resources.newsModel !== model)
    notes.push(`뉴스 분류 모델(${resources.newsModel})과 다릅니다. 모델 교체 시 재로딩으로 느려질 수 있습니다(매매 판정 영향 없음).`);
  return notes;
}

export type MarkdownInline = { kind: "text"; text: string } | { kind: "code"; text: string } | { kind: "bold"; text: string };
export type MarkdownBlock =
  | { kind: "paragraph"; lines: MarkdownInline[][] }
  | { kind: "code"; lang: string; text: string }
  | { kind: "list"; ordered: boolean; items: MarkdownInline[][] }
  | { kind: "heading"; level: number; inline: MarkdownInline[] };

export function parseMarkdownLite(source: string): MarkdownBlock[] {
  const blocks: MarkdownBlock[] = [];
  const lines = source.replace(/\r\n/g, "\n").split("\n");
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    const fence = /^```(\w*)\s*$/.exec(line);
    if (fence) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !/^```\s*$/.test(lines[i])) code.push(lines[i++]);
      i++;
      blocks.push({ kind: "code", lang: fence[1] ?? "", text: code.join("\n") });
      continue;
    }
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    if (heading) {
      blocks.push({ kind: "heading", level: heading[1].length, inline: parseInline(heading[2]) });
      i++;
      continue;
    }
    const bullet = /^\s*([-*•]|\d+[.)])\s+(.*)$/.exec(line);
    if (bullet) {
      const ordered = /\d/.test(bullet[1]);
      const items: MarkdownInline[][] = [];
      while (i < lines.length) {
        const m = /^\s*([-*•]|\d+[.)])\s+(.*)$/.exec(lines[i]);
        if (!m || /\d/.test(m[1]) !== ordered) break;
        items.push(parseInline(m[2]));
        i++;
      }
      blocks.push({ kind: "list", ordered, items });
      continue;
    }
    if (line.trim() === "") {
      i++;
      continue;
    }
    const para: MarkdownInline[][] = [];
    while (i < lines.length && lines[i].trim() !== "" && !/^```/.test(lines[i]) && !/^\s*([-*•]|\d+[.)])\s+/.test(lines[i]) && !/^#{1,4}\s/.test(lines[i]))
      para.push(parseInline(lines[i++]));
    blocks.push({ kind: "paragraph", lines: para });
  }
  return blocks;
}

export function parseInline(text: string): MarkdownInline[] {
  const out: MarkdownInline[] = [];
  const pattern = /(`[^`]+`)|(\*\*[^*]+\*\*)/g;
  let last = 0;
  for (const match of text.matchAll(pattern)) {
    const index = match.index ?? 0;
    if (index > last) out.push({ kind: "text", text: text.slice(last, index) });
    if (match[1]) out.push({ kind: "code", text: match[1].slice(1, -1) });
    else if (match[2]) out.push({ kind: "bold", text: match[2].slice(2, -2) });
    last = index + match[0].length;
  }
  if (last < text.length) out.push({ kind: "text", text: text.slice(last) });
  return out;
}
