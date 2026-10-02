import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChatWidget from "./ChatWidget";

const modelsBody = {
  enabled: true,
  status: "ok",
  error: null,
  models: [
    { name: "deepseek-r1:14b", sizeBytes: 9_000_000_000, parameterSize: "14.8B", family: "qwen2", isNewsModel: false },
    { name: "qwen2.5:7b-instruct", sizeBytes: 4_600_000_000, parameterSize: "7.6B", family: "qwen2", isNewsModel: true },
  ],
  defaultModel: "qwen2.5:7b-instruct",
  resources: { newsEnabled: true, newsModel: "qwen2.5:7b-instruct", newsQueue: 0, chatBusy: false },
};

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function sseResponse(blocks: string[]) {
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const block of blocks) controller.enqueue(encoder.encode(block));
      controller.close();
    },
  });
  return new Response(stream, { status: 200, headers: { "Content-Type": "text/event-stream" } });
}

type Call = { url: string; init?: RequestInit };

function makeFetch(handlers: { check?: (body: { model: string }) => unknown; chat?: (body: { model: string; messages: { role: string; content: string }[] }) => Response; models?: unknown }) {
  const calls: Call[] = [];
  const impl = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    calls.push({ url, init });
    if (url === "/api/chat/models") return jsonResponse(handlers.models ?? modelsBody);
    if (url === "/api/chat/check") {
      const body = JSON.parse(String(init?.body)) as { model: string };
      return jsonResponse(handlers.check?.(body) ?? { ok: true, model: body.model, latencyMs: 230, loadMs: 0, error: null, errorKind: null, warning: null, resources: modelsBody.resources });
    }
    if (url === "/api/chat") {
      const body = JSON.parse(String(init?.body)) as { model: string; messages: { role: string; content: string }[] };
      return handlers.chat?.(body) ?? sseResponse(['data: {"type":"delta","content":"안녕"}\n\n', 'data: {"type":"done","totalMs":40,"outputTokens":2}\n\n']);
    }
    throw new Error(`unexpected ${url}`);
  });
  return { impl: impl as unknown as typeof fetch, calls };
}

describe("ChatWidget", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("말풍선 버튼을 누르면 창이 열리고 모델 목록·연결 확인을 호출한다", async () => {
    const { impl, calls } = makeFetch({});
    render(<ChatWidget fetchImpl={impl} />);

    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));

    expect(screen.getByRole("dialog", { name: "AI 채팅" })).toBeTruthy();
    await waitFor(() => expect(screen.getByText("연결됨 230 ms")).toBeTruthy());
    expect(calls.map((c) => c.url)).toEqual(["/api/chat/models", "/api/chat/check"]);
    expect(JSON.parse(String(calls[1].init?.body))).toEqual({ model: "qwen2.5:7b-instruct" });
    const select = screen.getByRole("combobox", { name: "모델 선택" }) as HTMLSelectElement;
    expect(select.value).toBe("qwen2.5:7b-instruct");
    expect(select.options.length).toBe(2);
  });

  it("모델을 바꾸면 check를 다시 호출하고 실패 사유와 뉴스 모델 불일치 경고를 보여준다", async () => {
    const { impl, calls } = makeFetch({
      check: ({ model }) =>
        model === "deepseek-r1:14b"
          ? { ok: false, model, latencyMs: 0, loadMs: 0, error: "Ollama 대기열이 가득 찼습니다(503). 잠시 후 다시 시도하세요.", errorKind: "busy", warning: "뉴스 분류 모델(qwen2.5:7b-instruct)과 다릅니다.", resources: modelsBody.resources }
          : undefined,
    });
    render(<ChatWidget fetchImpl={impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));
    await waitFor(() => expect(screen.getByText("연결됨 230 ms")).toBeTruthy());

    fireEvent.change(screen.getByRole("combobox", { name: "모델 선택" }), { target: { value: "deepseek-r1:14b" } });

    await waitFor(() => expect(screen.getByText("연결 실패")).toBeTruthy());
    expect(screen.getByText("Ollama 대기열이 가득 찼습니다(503). 잠시 후 다시 시도하세요.")).toBeTruthy();
    expect(screen.getByText("뉴스 분류 모델(qwen2.5:7b-instruct)과 다릅니다.")).toBeTruthy();
    expect(calls.filter((c) => c.url === "/api/chat/check").map((c) => JSON.parse(String(c.init?.body)).model)).toEqual(["qwen2.5:7b-instruct", "deepseek-r1:14b"]);
    expect(JSON.parse(localStorage.getItem("astra-chat-v1")!).model).toBe("deepseek-r1:14b");
    expect((screen.getByRole("textbox", { name: "메시지 입력" }) as HTMLTextAreaElement).disabled).toBe(false);
  });

  it("메시지를 보내면 스트림 조각이 차례로 렌더되고 추론은 접힌 블록에 들어가며 로컬에 보존된다", async () => {
    const { impl, calls } = makeFetch({
      chat: () =>
        sseResponse([
          'data: {"type":"thinking","thinking":"먼저 생각"}\n\n',
          'data: {"type":"delta","content":"**안녕"}\n\ndata: {"type":"delta","content":"하세요**\\n- 항목"}\n\n',
          'data: {"type":"done","totalMs":1500,"outputTokens":5}\n\n',
        ]),
    });
    render(<ChatWidget fetchImpl={impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));
    await waitFor(() => expect(screen.getByText("연결됨 230 ms")).toBeTruthy());

    fireEvent.change(screen.getByRole("textbox", { name: "메시지 입력" }), { target: { value: "질문입니다" } });
    fireEvent.click(screen.getByRole("button", { name: "전송" }));

    await waitFor(() => expect(screen.getByText("1.5 s")).toBeTruthy());
    expect(screen.getByText("질문입니다")).toBeTruthy();
    expect(screen.getByText("안녕하세요").tagName).toBe("STRONG");
    expect(screen.getByRole("listitem").textContent).toBe("항목");
    expect(screen.getByText(/추론 과정/)).toBeTruthy();
    expect(screen.getByText("먼저 생각")).toBeTruthy();
    const chatCall = calls.find((c) => c.url === "/api/chat")!;
    expect(JSON.parse(String(chatCall.init?.body))).toEqual({ model: "qwen2.5:7b-instruct", messages: [{ role: "user", content: "질문입니다" }] });
    const stored = JSON.parse(localStorage.getItem("astra-chat-v1")!);
    expect(stored.turns.map((t: { role: string; content: string }) => [t.role, t.content])).toEqual([
      ["user", "질문입니다"],
      ["assistant", "**안녕하세요**\n- 항목"],
    ]);
  });

  it("스트림 error 이벤트와 429 응답을 말풍선 오류로 보여준다", async () => {
    let attempt = 0;
    const { impl } = makeFetch({
      chat: () => (attempt++ === 0 ? sseResponse(['data: {"type":"error","error":"Ollama 대기열이 가득 찼습니다(503).","errorKind":"busy"}\n\n']) : jsonResponse({ error: "다른 대화가 진행 중입니다." }, 429)),
    });
    render(<ChatWidget fetchImpl={impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));
    await waitFor(() => expect(screen.getByText("연결됨 230 ms")).toBeTruthy());

    fireEvent.change(screen.getByRole("textbox", { name: "메시지 입력" }), { target: { value: "첫 질문" } });
    fireEvent.keyDown(screen.getByRole("textbox", { name: "메시지 입력" }), { key: "Enter" });
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("Ollama 대기열이 가득 찼습니다(503)."));

    fireEvent.change(screen.getByRole("textbox", { name: "메시지 입력" }), { target: { value: "둘째 질문" } });
    fireEvent.click(screen.getByRole("button", { name: "전송" }));
    await waitFor(() => expect(screen.getAllByRole("alert").map((a) => a.textContent)).toEqual(["Ollama 대기열이 가득 찼습니다(503).", "다른 대화가 진행 중입니다."]));
  });

  it("저장된 대화를 복원하고 지우기·최소화·닫기가 동작한다", async () => {
    localStorage.setItem("astra-chat-v1", JSON.stringify({ model: "deepseek-r1:14b", turns: [{ id: "1", role: "user", content: "이전 질문" }, { id: "2", role: "assistant", content: "이전 답변" }] }));
    const { impl, calls } = makeFetch({});
    render(<ChatWidget fetchImpl={impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));

    expect(screen.getByText("이전 질문")).toBeTruthy();
    expect(screen.getByText("이전 답변")).toBeTruthy();
    await waitFor(() => expect(screen.getByText("연결됨 230 ms")).toBeTruthy());
    expect(JSON.parse(String(calls[1].init?.body))).toEqual({ model: "deepseek-r1:14b" });

    fireEvent.click(screen.getByRole("button", { name: "대화 지우기" }));
    expect(screen.queryByText("이전 질문")).toBeNull();
    expect(JSON.parse(localStorage.getItem("astra-chat-v1")!).turns).toEqual([]);

    fireEvent.click(screen.getByRole("button", { name: "채팅 최소화" }));
    expect(screen.queryByRole("combobox", { name: "모델 선택" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "채팅 펼치기" }));
    expect(screen.getByRole("combobox", { name: "모델 선택" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "채팅 닫기" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByRole("button", { name: "AI 채팅 열기" })).toBeTruthy();
  });

  it("모델 목록 오류와 비활성 상태를 안내한다", async () => {
    const errorFetch = makeFetch({ models: { ...modelsBody, status: "error", error: "Ollama에 연결할 수 없습니다(http://localhost:11434).", models: [], defaultModel: null } });
    const { unmount } = render(<ChatWidget fetchImpl={errorFetch.impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));
    await waitFor(() => expect(screen.getByText(/Ollama에 연결할 수 없습니다/)).toBeTruthy());
    expect(screen.getByRole("button", { name: "다시 시도" })).toBeTruthy();
    expect(errorFetch.calls.map((c) => c.url)).toEqual(["/api/chat/models"]);
    unmount();

    const disabledFetch = makeFetch({ models: { ...modelsBody, enabled: false, status: "disabled", models: [] } });
    render(<ChatWidget fetchImpl={disabledFetch.impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));
    await waitFor(() => expect(screen.getByText(/채팅 기능이 서버 설정에서 꺼져 있습니다/)).toBeTruthy());
  });

  it("생성 중에는 중단 버튼이 보이고 누르면 요청이 취소된다", async () => {
    let release: (() => void) | null = null;
    const { impl } = makeFetch({
      chat: () =>
        new Response(
          new ReadableStream<Uint8Array>({
            start(controller) {
              controller.enqueue(new TextEncoder().encode('data: {"type":"delta","content":"부분"}\n\n'));
              release = () => controller.close();
            },
          }),
          { status: 200 },
        ),
    });
    render(<ChatWidget fetchImpl={impl} />);
    fireEvent.click(screen.getByRole("button", { name: "AI 채팅 열기" }));
    await waitFor(() => expect(screen.getByText("연결됨 230 ms")).toBeTruthy());

    fireEvent.change(screen.getByRole("textbox", { name: "메시지 입력" }), { target: { value: "긴 질문" } });
    fireEvent.click(screen.getByRole("button", { name: "전송" }));
    await waitFor(() => expect(screen.getByText("부분")).toBeTruthy());
    expect(screen.getByRole("button", { name: "생성 중단" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "생성 중단" }));
    await act(async () => {
      release?.();
    });
    await waitFor(() => expect(screen.getByRole("button", { name: "전송" })).toBeTruthy());
    expect(screen.getByText("부분")).toBeTruthy();
  });
});
