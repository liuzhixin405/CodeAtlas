import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CursorSource, resolveCursor, type CursorPos, type OutlineClient, type ResolveDeps, type ResolveResult } from "../src/outlineFollow";
import type { SymbolDto } from "../webview/types";

function sym(id: number, kind = "method"): SymbolDto {
  return { id, kind, name: `m${id}`, qualifiedName: `Acme.Shop.C.m${id}`, signature: null, path: "src/C.cs", startLine: 1, startCol: 1, endLine: 9, endCol: 1, visibility: null };
}

const at = (line: number, filePath = "/repo/src/C.cs", col = 5): CursorPos => ({ filePath, line, col });

/** 假 resolve：第 1–9 行是方法 1，10–19 行是方法 2，20 行以后是类体。 */
function fakeResolve(calls: CursorPos[]) {
  return async (pos: CursorPos): Promise<ResolveResult> => {
    calls.push(pos);
    if (pos.line < 10) return { state: "method", symbol: sym(1), repoRoot: "/repo" };
    if (pos.line < 20) return { state: "method", symbol: sym(2), repoRoot: "/repo" };
    return { state: "noMethod" };
  };
}

function setup(opts: { resolve?: (p: CursorPos) => Promise<ResolveResult> } = {}) {
  const calls: CursorPos[] = [];
  const results: ResolveResult[] = [];
  // 原来的单视图 OutlineFollower 拆成了共享来源 + 每个视图一个订阅；这里是"只有一个视图"的情形，断言不变
  const src = new CursorSource({
    resolve: opts.resolve ?? fakeResolve(calls),
    isSupported: (p) => p.endsWith(".cs") || p.endsWith(".ts"),
    debounceMs: 300,
  });
  const sub = src.subscribe((r) => results.push(r));
  sub.setActive(true);
  return { src, sub, calls, results };
}

const roots = (rs: ResolveResult[]) => rs.map((r) => (r.state === "method" ? r.symbol.id : r.state));

describe("CursorSource：单个视图", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("防抖 300ms：一串光标移动只解析最后一次", async () => {
    const { src, sub, calls, results } = setup();
    for (let line = 1; line <= 5; line++) { src.notify(at(line), true); await vi.advanceTimersByTimeAsync(100); }
    expect(calls).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(300);
    expect(calls.map((c) => c.line)).toEqual([5]);
    expect(roots(results)).toEqual([1]);
  });

  it("同一方法里移动不重复回调；换方法才回调", async () => {
    const { src, sub, results } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(6), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(8), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([1]);
    src.notify(at(12), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([1, 2]);
  });

  it("同一行（只换列）不再请求 symbolAt", async () => {
    const { src, sub, calls } = setup();
    src.notify(at(2, undefined, 1), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(2, undefined, 9), true); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(1);
  });

  it("固定时不跟随；取消固定后按当前光标跟一次", async () => {
    const { src, sub, results } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    sub.setPinned(true);
    src.notify(at(12), true); await vi.advanceTimersByTimeAsync(1000);
    expect(roots(results)).toEqual([1]);
    sub.setPinned(false, at(12));
    await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([1, 2]);
  });

  it("固定发生在防抖期间：待处理的那次作废", async () => {
    const { src, sub, calls } = setup();
    src.notify(at(2), true);
    await vi.advanceTimersByTimeAsync(100);
    sub.setPinned(true);
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(0);
  });

  it("非支持文件忽略，不清空当前大纲", async () => {
    const { src, sub, calls, results } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(2, "/repo/README.md"), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(2, "/repo/data.json"), false); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(1);
    expect(roots(results)).toEqual([1]);
  });

  it("视图不可见时不跟随；变可见时按当前光标跟一次", async () => {
    const { src, sub, calls, results } = setup();
    sub.setActive(false);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(0);
    sub.setActive(true, at(12)); await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([2]);
  });

  it("光标到类体：提示放进方法；连续在类体里不重复回调", async () => {
    const { src, sub, results } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(21), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(25), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([1, "noMethod"]);
    // 回到同一个方法：根被清过，要重新回调
    src.notify(at(3), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([1, "noMethod", 1]);
  });

  it("视图自己跳转引起的光标移动不跟随，用户再点 / 打字就恢复", async () => {
    const { src, sub, calls, results } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    src.beginNavigation();
    src.notify(at(12, "/repo/src/D.cs"), false); // 打开文档 + 程序设光标
    await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(1);
    src.notify(at(13, "/repo/src/D.cs"), true); // 用户在那边点了一下
    await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([1, 2]);
  });

  it("跳转宽限期过后，非用户事件（切换编辑器）照常跟随", async () => {
    const { src, sub, results } = setup();
    src.beginNavigation();
    await vi.advanceTimersByTimeAsync(1500);
    src.notify(at(12), false); await vi.advanceTimersByTimeAsync(300);
    expect(roots(results)).toEqual([2]);
  });

  it("慢请求的旧结果不覆盖新结果", async () => {
    const pending: Array<{ pos: CursorPos; resolve: (r: ResolveResult) => void }> = [];
    const { src, sub, results } = setup({ resolve: (pos) => new Promise((resolve) => pending.push({ pos, resolve })) });
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(12), true); await vi.advanceTimersByTimeAsync(300);
    expect(pending).toHaveLength(2);
    pending[1]!.resolve({ state: "method", symbol: sym(2), repoRoot: "/repo" });
    await vi.advanceTimersByTimeAsync(0);
    pending[0]!.resolve({ state: "method", symbol: sym(1), repoRoot: "/repo" });
    await vi.advanceTimersByTimeAsync(0);
    expect(roots(results)).toEqual([2]);
  });

  it("出错不记住位置：同一行下次还会重试", async () => {
    let fail = true;
    const calls: CursorPos[] = [];
    const { src, sub, results } = setup({
      resolve: async (pos) => { calls.push(pos); return fail ? { state: "error", message: "boom" } : { state: "method", symbol: sym(1), repoRoot: "/repo" }; },
    });
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    fail = false;
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(2);
    expect(roots(results)).toEqual(["error", 1]);
  });

  it("reset 后同一方法也会重新回调（刷新 / 索引变化）", async () => {
    const { src, sub, results } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    await sub.resolveNow(at(2));
    expect(roots(results)).toEqual([1, 1]);
  });

  it("ignore（不在工作区里的文件）保持现状", async () => {
    const { src, sub, results } = setup({ resolve: async () => ({ state: "ignore" }) });
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    expect(results).toHaveLength(0);
  });
});

describe("resolveCursor", () => {
  function client(over: Partial<OutlineClient> = {}): OutlineClient & { calls: string[] } {
    const calls: string[] = [];
    return {
      calls,
      health: async () => { calls.push("health"); return { files: 10 }; },
      symbolAt: async (p, l, c) => { calls.push(`at:${l}:${c}`); return sym(1); },
      outline: async () => null,
      ...over,
    };
  }

  function deps(c: OutlineClient | null, over: Partial<ResolveDeps> = {}): ResolveDeps & { connects: number } {
    const d = {
      connects: 0,
      rootFor: (p: string) => (p.startsWith("/repo/") ? "/repo" : undefined),
      hasServer: () => true,
      indexExists: () => true,
      connect: async () => { d.connects++; return c; },
      ...over,
    };
    return d;
  }

  it("拿到方法就作为根", async () => {
    const r = await resolveCursor(at(3), deps(client()), new Set());
    expect(r).toEqual({ state: "method", symbol: sym(1), repoRoot: "/repo" });
  });

  it("光标处是类 / 属性 / 没有符号 → noMethod", async () => {
    expect((await resolveCursor(at(3), deps(client({ symbolAt: async () => sym(1, "class") })), new Set())).state).toBe("noMethod");
    expect((await resolveCursor(at(3), deps(client({ symbolAt: async () => sym(1, "property") })), new Set())).state).toBe("noMethod");
    expect((await resolveCursor(at(3), deps(client({ symbolAt: async () => null })), new Set())).state).toBe("noMethod");
  });

  it("不在工作区里 → ignore，不连服务", async () => {
    const d = deps(client());
    expect((await resolveCursor(at(3, "/elsewhere/x.cs"), d, new Set())).state).toBe("ignore");
    expect(d.connects).toBe(0);
  });

  it("服务没起来且仓库没有索引库：提示建立索引，不拉起服务", async () => {
    const d = deps(client(), { hasServer: () => false, indexExists: () => false });
    expect((await resolveCursor(at(3), d, new Set())).state).toBe("noIndex");
    expect(d.connects).toBe(0);
  });

  it("服务没起来但已有索引库：拉起（视图可见才会走到这里）", async () => {
    const d = deps(client(), { hasServer: () => false, indexExists: () => true });
    expect((await resolveCursor(at(3), d, new Set())).state).toBe("method");
    expect(d.connects).toBe(1);
  });

  it("库是空的（files = 0）→ noIndex；有索引后只问一次 health", async () => {
    expect((await resolveCursor(at(3), deps(client({ health: async () => ({ files: 0 }) })), new Set())).state).toBe("noIndex");
    const c = client();
    const indexed = new Set<string>();
    await resolveCursor(at(3), deps(c), indexed);
    await resolveCursor(at(4), deps(c), indexed);
    expect(c.calls.filter((x) => x === "health")).toHaveLength(1);
    expect(c.calls).toContain("at:4:5");
  });

  it("服务起不来 → noServer；请求抛错 → error", async () => {
    expect((await resolveCursor(at(3), deps(null), new Set())).state).toBe("noServer");
    const r = await resolveCursor(at(3), deps(client({ symbolAt: async () => { throw new Error("symbols/at → 500"); } })), new Set());
    expect(r).toEqual({ state: "error", message: "symbols/at → 500" });
  });
});
