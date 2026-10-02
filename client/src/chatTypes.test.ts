import { describe, expect, it } from "vitest";
import {
  applyStreamEvent,
  createSseParser,
  formatLatency,
  historyForRequest,
  loadChatState,
  parseInline,
  parseMarkdownLite,
  resourceNotice,
  saveChatState,
  type ChatTurn,
} from "./chatTypes";

function memoryStorage(initial: Record<string, string> = {}) {
  const map = new Map(Object.entries(initial));
  return {
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => void map.set(k, v),
    removeItem: (k: string) => void map.delete(k),
    map,
  };
}

describe("createSseParser", () => {
  it("조각 경계에 걸린 이벤트를 모아 순서대로 돌려준다", () => {
    const parser = createSseParser();
    const first = parser.push('data: {"type":"delta","content":"안');
    const second = parser.push('녕"}\n\ndata: {"type":"thinking","thinking":"생각"}\n\ndata: {"type":"do');
    const third = parser.push('ne","totalMs":120,"outputTokens":3}\n\n');

    expect(first).toEqual([]);
    expect(second).toEqual([
      { type: "delta", content: "안녕" },
      { type: "thinking", thinking: "생각" },
    ]);
    expect(third).toEqual([{ type: "done", totalMs: 120, outputTokens: 3 }]);
  });

  it("알 수 없는 타입과 깨진 JSON은 건너뛰고 error 이벤트는 메시지를 보존한다", () => {
    const parser = createSseParser();
    const events = parser.push('data: {"type":"ping"}\n\ndata: {broken\n\ndata: {"type":"error","error":"대기열이 가득 찼습니다","errorKind":"busy"}\n\n');

    expect(events).toEqual([{ type: "error", error: "대기열이 가득 찼습니다", errorKind: "busy" }]);
  });

  it("flush는 마지막 블록을 비운다", () => {
    const parser = createSseParser();
    parser.push('data: {"type":"delta","content":"끝"}');

    expect(parser.flush()).toEqual([{ type: "delta", content: "끝" }]);
    expect(parser.flush()).toEqual([]);
  });
});

describe("applyStreamEvent", () => {
  const base: ChatTurn = { id: "a", role: "assistant", content: "", pending: true };

  it("delta와 thinking을 누적하고 done에서 pending을 끈다", () => {
    let turn = applyStreamEvent(base, { type: "thinking", thinking: "먼저" });
    turn = applyStreamEvent(turn, { type: "delta", content: "안녕" });
    turn = applyStreamEvent(turn, { type: "delta", content: "하세요" });
    turn = applyStreamEvent(turn, { type: "done", totalMs: 50, outputTokens: 2 });

    expect(turn).toEqual({ id: "a", role: "assistant", content: "안녕하세요", thinking: "먼저", pending: false, totalMs: 50 });
  });

  it("error는 pending을 끄고 오류를 남긴다", () => {
    const turn = applyStreamEvent(base, { type: "error", error: "실패", errorKind: null });

    expect(turn.pending).toBe(false);
    expect(turn.error).toBe("실패");
  });
});

describe("historyForRequest", () => {
  it("오류·진행 중·빈 턴을 빼고 최근 N턴만 role/content로 보낸다", () => {
    const turns: ChatTurn[] = [];
    for (let i = 0; i < 12; i++) turns.push({ id: `u${i}`, role: "user", content: `q${i}` }, { id: `a${i}`, role: "assistant", content: `a${i}` });
    turns.push({ id: "err", role: "assistant", content: "", error: "실패" });
    turns.push({ id: "pending", role: "assistant", content: "x", pending: true });

    const history = historyForRequest(turns, 3);

    expect(history).toEqual([
      { role: "user", content: "q9" },
      { role: "assistant", content: "a9" },
      { role: "user", content: "q10" },
      { role: "assistant", content: "a10" },
      { role: "user", content: "q11" },
      { role: "assistant", content: "a11" },
    ]);
  });
});

describe("loadChatState / saveChatState", () => {
  it("저장과 복원이 왕복하며 pending 턴은 저장하지 않는다", () => {
    const storage = memoryStorage();
    const saved = saveChatState(
      { model: "qwen3:14b", turns: [{ id: "1", role: "user", content: "hi" }, { id: "2", role: "assistant", content: "응", thinking: "t", totalMs: 10 }, { id: "3", role: "assistant", content: "", pending: true }] },
      storage,
    );
    const loaded = loadChatState(storage);

    expect(saved).toBe(true);
    expect(loaded.model).toBe("qwen3:14b");
    expect(loaded.turns.map((t) => t.id)).toEqual(["1", "2"]);
    expect(loaded.turns[1].thinking).toBe("t");
  });

  it("깨진 JSON·잘못된 역할·저장소 예외에도 빈 상태로 복원한다", () => {
    expect(loadChatState(memoryStorage({ "astra-chat-v1": "{bad" }))).toEqual({ model: null, turns: [] });
    expect(loadChatState(memoryStorage({ "astra-chat-v1": JSON.stringify({ model: 1, turns: [{ role: "system", content: "x" }, { role: "user", content: "ok" }] }) })).turns.map((t) => t.role)).toEqual(["user"]);
    expect(loadChatState({ getItem: () => { throw new Error("blocked"); } })).toEqual({ model: null, turns: [] });
    expect(saveChatState({ model: null, turns: [] }, { setItem: () => { throw new Error("quota"); } })).toBe(false);
    expect(loadChatState(null)).toEqual({ model: null, turns: [] });
  });
});

describe("resourceNotice", () => {
  const resources = { newsEnabled: true, newsModel: "qwen2.5:7b-instruct", newsQueue: 0, chatBusy: false };

  it("서버 경고를 우선 쓰고 뉴스 큐가 있으면 지연 안내를 덧붙인다", () => {
    expect(resourceNotice({ ...resources, newsQueue: 3 }, "qwen3:14b", "서버 경고")).toEqual(["서버 경고", "뉴스 분류 대기 3건 — 같은 Ollama를 쓰므로 응답이 지연될 수 있습니다."]);
  });

  it("서버 경고가 없고 모델이 다르면 클라이언트가 불일치 안내를 만든다", () => {
    expect(resourceNotice(resources, "qwen3:14b", null)[0]).toContain("뉴스 분류 모델(qwen2.5:7b-instruct)과 다릅니다");
    expect(resourceNotice(resources, "qwen2.5:7b-instruct", null)).toEqual([]);
    expect(resourceNotice({ ...resources, newsEnabled: false }, "qwen3:14b", null)).toEqual([]);
  });
});

describe("parseMarkdownLite", () => {
  it("코드 펜스·목록·제목·문단·인라인 서식을 분리한다", () => {
    const blocks = parseMarkdownLite("# 제목\n첫 줄 **굵게** 그리고 `code`\n둘째 줄\n\n- 하나\n- 둘\n\n1. 가\n2. 나\n```ts\nconst a = 1;\n```");

    expect(blocks[0]).toEqual({ kind: "heading", level: 1, inline: [{ kind: "text", text: "제목" }] });
    expect(blocks[1]).toEqual({
      kind: "paragraph",
      lines: [
        [{ kind: "text", text: "첫 줄 " }, { kind: "bold", text: "굵게" }, { kind: "text", text: " 그리고 " }, { kind: "code", text: "code" }],
        [{ kind: "text", text: "둘째 줄" }],
      ],
    });
    expect(blocks[2]).toEqual({ kind: "list", ordered: false, items: [[{ kind: "text", text: "하나" }], [{ kind: "text", text: "둘" }]] });
    expect(blocks[3]).toEqual({ kind: "list", ordered: true, items: [[{ kind: "text", text: "가" }], [{ kind: "text", text: "나" }]] });
    expect(blocks[4]).toEqual({ kind: "code", lang: "ts", text: "const a = 1;" });
  });

  it("닫히지 않은 코드 펜스는 끝까지 코드로 본다", () => {
    expect(parseMarkdownLite("```\nabc\ndef")).toEqual([{ kind: "code", lang: "", text: "abc\ndef" }]);
  });

  it("인라인에 서식이 없으면 텍스트 하나다", () => {
    expect(parseInline("그냥 글")).toEqual([{ kind: "text", text: "그냥 글" }]);
  });
});

describe("formatLatency", () => {
  it("1초 미만은 ms, 그 이상은 초로 표시한다", () => {
    expect(formatLatency(321)).toBe("321 ms");
    expect(formatLatency(1530)).toBe("1.5 s");
    expect(formatLatency(12_400)).toBe("12 s");
  });
});
