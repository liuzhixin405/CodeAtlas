import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CursorSource, FollowDebounceMs, type CursorPos, type ResolveResult } from "../src/outlineFollow";
import { PanelFollow, PanelFollowDebounceMs, PanelNavigationGraceMs } from "../src/panelFollowModel";
import type { SymbolDto } from "../webview/types";

function sym(id: number): SymbolDto {
  return { id, kind: "method", name: `m${id}`, qualifiedName: `Acme.Shop.C.m${id}`, signature: null, path: "src/C.cs", startLine: 1, startCol: 1, endLine: 9, endCol: 1, visibility: null };
}

const at = (line: number): CursorPos => ({ filePath: "/repo/src/C.cs", line, col: 5 });

/**
 * 第 1–9 行方法 1，10–19 行方法 2，20 行以后类体。move() 等于 panelFollow.ts 收到一次光标事件：
 * 先 touch 面板的防抖，再喂给共享来源。时钟用 vitest 的假时钟（Date.now 跟着走）。
 */
function setup(panelRepo: string | null = "/repo") {
  const calls: CursorPos[] = [];
  const src = new CursorSource({
    resolve: async (pos): Promise<ResolveResult> => {
      calls.push(pos);
      if (pos.line < 10) return { state: "method", symbol: sym(1), repoRoot: "/repo" };
      if (pos.line < 20) return { state: "method", symbol: sym(2), repoRoot: "/repo" };
      return { state: "noMethod" };
    },
    isSupported: (p) => p.endsWith(".cs"),
  });
  const sent: number[] = [];
  const model = new PanelFollow({ send: (_r, id) => sent.push(id), panelRepo: () => panelRepo });
  const sub = src.subscribe((r) => model.accept(r));
  const move = (line: number, user = true) => { model.touch(); src.notify(at(line), user); };
  return { src, sub, model, sent, calls, move };
}

describe("面板跟随光标：防抖与去重", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("从最后一次光标移动算起 400ms 才发", async () => {
    const { sub, sent, move } = setup();
    sub.setActive(true);
    move(2);
    await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs - 1);
    expect(sent).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(sent).toEqual([1]);
  });

  it("连续移动只发最后一个方法，只请求一次", async () => {
    const { sub, sent, calls, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(100);
    move(12); await vi.advanceTimersByTimeAsync(100);
    move(3); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1]);
    expect(calls).toHaveLength(1);
  });

  it("共享来源已解析出方法、面板还在等时又换了方法：只发后一个", async () => {
    const { sub, sent, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(FollowDebounceMs + 10); // 来源已分出方法 1，面板还在等
    move(12); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([2]);
  });

  it("在同一方法里继续移动：待发的往后推（从最后一次移动算起）", async () => {
    const { sub, sent, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(FollowDebounceMs + 10);
    move(4); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs - 1);
    expect(sent).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(sent).toEqual([1]);
  });

  it("同一方法不重复发；A → B → A 在防抖内来回也不重发 A", async () => {
    const { sub, sent, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    move(5); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1]);
    move(12); await vi.advanceTimersByTimeAsync(FollowDebounceMs + 10);
    move(3); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1]);
    move(12); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1, 2]);
  });

  it("光标移出方法（类体）：面板保持现状，不发", async () => {
    const { sub, sent, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    move(25); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1]);
  });

  it("没勾选（订阅不活跃）：不请求、不发", async () => {
    const { sent, calls, move } = setup();
    move(2); await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toEqual([]);
    expect(sent).toEqual([]);
  });

  it("面板连着别的仓库：不发", async () => {
    const { sub, sent, move } = setup("/other");
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([]);
  });

  it("重新勾选：当前方法哪怕和上次一样也再发一次", async () => {
    const { sub, model, sent, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    sub.setActive(false); model.reset();
    // 同 PanelCursorFollow.setEnabled(true)
    model.reset(); sub.reset(); sub.setActive(true, at(2));
    await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1, 1]);
  });

  it("面板自己跳转编辑器之后的宽限期内不发；过后照常", async () => {
    const { sub, model, sent, move } = setup();
    sub.setActive(true);
    move(2); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    model.beginNavigation();
    move(12, false); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1]);
    await vi.advanceTimersByTimeAsync(PanelNavigationGraceMs);
    // 回到方法 1 不重发（面板上一次就是它），再到方法 2 才发
    move(3); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1]);
    move(13); await vi.advanceTimersByTimeAsync(PanelFollowDebounceMs);
    expect(sent).toEqual([1, 2]);
  });
});

describe("CursorSource.resolveFor（命令用的「光标 → 当前方法」）", () => {
  it("同一行有缓存直接用，不再请求；换行才请求", async () => {
    const { src, calls } = setup();
    const r1 = await src.resolveFor(at(2));
    const r2 = await src.resolveFor(at(2));
    expect(r1.state === "method" && r1.symbol.id).toBe(1);
    expect(r2).toBe(r1);
    expect(calls).toHaveLength(1);
    await src.resolveFor(at(12));
    expect(calls).toHaveLength(2);
  });

  it("不是支持的文件：ignore，不请求", async () => {
    const { src, calls } = setup();
    expect((await src.resolveFor({ filePath: "/repo/readme.md", line: 1, col: 1 })).state).toBe("ignore");
    expect(calls).toHaveLength(0);
  });

  it("跟随时解析过的行，命令直接复用", async () => {
    vi.useFakeTimers();
    try {
      const { src, sub, calls } = setup();
      sub.setActive(true);
      src.notify(at(2), true); await vi.advanceTimersByTimeAsync(FollowDebounceMs);
      expect(calls).toHaveLength(1);
      await src.resolveFor(at(2));
      expect(calls).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });
});
