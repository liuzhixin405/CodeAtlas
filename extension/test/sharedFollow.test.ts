import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CursorSource, resolveCursor, type CursorPos, type FollowClient, type ResolveResult } from "../src/outlineFollow";
import type { SymbolDto } from "../webview/types";

// 调用大纲与影响范围共用一个 CursorSource：一次光标移动只解析一次（一次 symbolAt），各自固定 / 可见互不影响。

function sym(id: number): SymbolDto {
  return { id, kind: "method", name: `m${id}`, qualifiedName: `Acme.Shop.C.m${id}`, signature: null, path: "src/C.cs", startLine: 1, startCol: 1, endLine: 9, endCol: 1, visibility: null };
}

const at = (line: number, filePath = "/repo/src/C.cs"): CursorPos => ({ filePath, line, col: 5 });

/** 第 1–9 行是方法 1，10–19 行是方法 2，20 行以后是类体。calls 记录每次 resolve（= 一次 symbolAt）。 */
function setup(resolve?: (p: CursorPos) => Promise<ResolveResult>) {
  const calls: CursorPos[] = [];
  const src = new CursorSource({
    resolve: resolve ?? (async (pos) => {
      calls.push(pos);
      if (pos.line < 10) return { state: "method", symbol: sym(1), repoRoot: "/repo" };
      if (pos.line < 20) return { state: "method", symbol: sym(2), repoRoot: "/repo" };
      return { state: "noMethod" };
    }),
    isSupported: (p) => p.endsWith(".cs"),
    debounceMs: 300,
  });
  const outlineResults: ResolveResult[] = [];
  const impactResults: ResolveResult[] = [];
  const outline = src.subscribe((r) => outlineResults.push(r));
  const impact = src.subscribe((r) => impactResults.push(r));
  return { src, calls, outline, impact, outlineResults, impactResults };
}

const roots = (rs: ResolveResult[]) => rs.map((r) => (r.state === "method" ? r.symbol.id : r.state));

describe("CursorSource：两个视图共用", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("两个视图都可见：一次光标移动只请求一次，两边都拿到结果", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(1);
    expect(roots(outlineResults)).toEqual([1]);
    expect(roots(impactResults)).toEqual([1]);
    src.notify(at(12), true); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(2);
    expect(roots(outlineResults)).toEqual([1, 2]);
    expect(roots(impactResults)).toEqual([1, 2]);
  });

  it("都不可见：不请求", async () => {
    const { src, calls } = setup();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toHaveLength(0);
  });

  it("只有一个可见：只给它；另一个变可见时同一行直接用缓存，不再请求", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(outlineResults)).toEqual([1]);
    expect(impactResults).toHaveLength(0);
    impact.setActive(true, at(2)); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(1);
    expect(roots(impactResults)).toEqual([1]);
    // 大纲不会因为影响范围变可见而重复回调
    expect(roots(outlineResults)).toEqual([1]);
  });

  it("另一个变可见时光标已在别的行：请求一次，已可见的那个也跟着更新", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    impact.setActive(true, at(12)); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(2);
    expect(roots(impactResults)).toEqual([2]);
    expect(roots(outlineResults)).toEqual([1, 2]);
  });

  it("固定大纲不影响影响范围，反之亦然", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    outline.setPinned(true);
    src.notify(at(12), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(outlineResults)).toEqual([1]);
    expect(roots(impactResults)).toEqual([1, 2]);
    // 反过来：取消大纲固定、固定影响范围
    impact.setPinned(true);
    outline.setPinned(false, at(12)); await vi.advanceTimersByTimeAsync(300);
    // 同一行用缓存，不再请求
    expect(calls).toHaveLength(2);
    expect(roots(outlineResults)).toEqual([1, 2]);
    src.notify(at(25), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(outlineResults)).toEqual([1, 2, "noMethod"]);
    expect(roots(impactResults)).toEqual([1, 2]);
    expect(outline.pinned).toBe(false);
    expect(impact.pinned).toBe(true);
  });

  it("一个固定发生在防抖期间：另一个照常拿到；两个都固定才作废", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true);
    await vi.advanceTimersByTimeAsync(100);
    outline.setPinned(true);
    await vi.advanceTimersByTimeAsync(300);
    expect(outlineResults).toHaveLength(0);
    expect(roots(impactResults)).toEqual([1]);

    src.notify(at(12), true);
    await vi.advanceTimersByTimeAsync(100);
    impact.setPinned(true);
    await vi.advanceTimersByTimeAsync(500);
    expect(calls).toHaveLength(1);
  });

  it("在途时一个视图藏起：结果只给还可见的", async () => {
    const pending: Array<(r: ResolveResult) => void> = [];
    const { src, outline, impact, outlineResults, impactResults } = setup(() => new Promise((resolve) => pending.push(resolve)));
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    impact.setActive(false);
    pending[0]!({ state: "method", symbol: sym(1), repoRoot: "/repo" });
    await vi.advanceTimersByTimeAsync(0);
    expect(roots(outlineResults)).toEqual([1]);
    expect(impactResults).toHaveLength(0);
  });

  it("任一视图跳转编辑器：两个视图都不跟随由此引起的光标移动", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    src.beginNavigation();
    src.notify(at(12, "/repo/src/D.cs"), false); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(1);
    src.notify(at(13, "/repo/src/D.cs"), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(outlineResults)).toEqual([1, 2]);
    expect(roots(impactResults)).toEqual([1, 2]);
  });

  it("一个视图点刷新：只请求一次；同一方法另一个视图不重复回调", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    await impact.resolveNow(at(2));
    expect(calls).toHaveLength(2);
    expect(roots(impactResults)).toEqual([1, 1]);
    expect(roots(outlineResults)).toEqual([1]);
  });

  it("固定的视图点刷新不解析光标", async () => {
    const { src, calls, outline, impact } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    impact.setPinned(true);
    await impact.resolveNow(at(12));
    expect(calls).toHaveLength(1);
  });

  it("索引变化：只请求一次，两个视图都重新回调（即使还是同一方法）", async () => {
    const { src, calls, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    await src.refresh(at(2));
    expect(calls).toHaveLength(2);
    expect(roots(outlineResults)).toEqual([1, 1]);
    expect(roots(impactResults)).toEqual([1, 1]);
  });

  it("索引变化时没有可见视图：不请求，之后变可见再解析", async () => {
    const { src, calls, outline, outlineResults } = setup();
    outline.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    outline.setActive(false);
    await src.refresh(at(2));
    expect(calls).toHaveLength(1);
    outline.setActive(true, at(2)); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(2);
    expect(roots(outlineResults)).toEqual([1, 1]);
  });

  it("出错不缓存：另一个视图之后变可见会重新请求", async () => {
    let fail = true;
    const calls: CursorPos[] = [];
    const { src, outline, impact, impactResults } = setup(async (pos) => {
      calls.push(pos);
      return fail ? { state: "error", message: "boom" } : { state: "method", symbol: sym(1), repoRoot: "/repo" };
    });
    outline.setActive(true);
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    fail = false;
    impact.setActive(true, at(2)); await vi.advanceTimersByTimeAsync(300);
    expect(calls).toHaveLength(2);
    expect(roots(impactResults)).toEqual([1]);
  });

  it("退订后不再收到结果", async () => {
    const { src, outline, impact, outlineResults, impactResults } = setup();
    outline.setActive(true);
    impact.setActive(true);
    impact.dispose();
    src.notify(at(2), true); await vi.advanceTimersByTimeAsync(300);
    expect(roots(outlineResults)).toEqual([1]);
    expect(impactResults).toHaveLength(0);
  });

  it("经 resolveCursor + 假 client：两个视图可见，symbolAt 每行只调一次", async () => {
    const symbolAtCalls: number[] = [];
    const client: FollowClient = {
      health: async () => ({ files: 3 }),
      symbolAt: async (_p, line) => { symbolAtCalls.push(line); return sym(line < 10 ? 1 : 2); },
    };
    const indexed = new Set<string>();
    const src = new CursorSource({
      resolve: (pos) => resolveCursor(pos, { rootFor: () => "/repo", hasServer: () => true, indexExists: () => true, connect: async () => client }, indexed),
      isSupported: () => true,
    });
    const a: ResolveResult[] = [];
    const b: ResolveResult[] = [];
    const outline = src.subscribe((r) => a.push(r));
    const impact = src.subscribe((r) => b.push(r));
    outline.setActive(true);
    impact.setActive(true);
    src.notify(at(3), true); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(3), false); await vi.advanceTimersByTimeAsync(300);
    src.notify(at(14), true); await vi.advanceTimersByTimeAsync(300);
    expect(symbolAtCalls).toEqual([3, 14]);
    expect(roots(a)).toEqual([1, 2]);
    expect(roots(b)).toEqual([1, 2]);
  });
});
