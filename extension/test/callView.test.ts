import { describe, expect, it } from "vitest";
import { CallView, CallViewModeKey, loadCallViewMode, saveCallViewMode, type CallViewDeps, type SeqViewState, type StorageLike } from "../webview/callView";
import type { FlowDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string): SymbolDto {
  const name = qualifiedName.replace(/\(.*$/, "").split(".").pop()!;
  return { id, kind: "method", name, qualifiedName, signature: null, path: "src/Acme.Shop/X.cs", startLine: id, startCol: 1, endLine: id, endCol: 1, visibility: null };
}
function flow(id: number): FlowDto {
  return {
    entrySymbolId: id, entryLabel: `F${id}`, route: null, containerKey: "shop-api", actors: [{ key: "a", label: "Acme.Shop", kind: "project" }],
    root: { symbolId: id, label: `F${id}`, qualifiedName: `F${id}`, kind: "entry", actor: "a", via: null, confidence: 1, count: 1, depth: 0, children: [], hidden: 0, note: null },
    behaviors: [], nodes: 1, truncated: false, note: null,
  };
}

class MemStorage implements StorageLike {
  data = new Map<string, string>();
  getItem(k: string): string | null { return this.data.get(k) ?? null; }
  setItem(k: string, v: string): void { this.data.set(k, v); }
}

const A = sym(1, "Acme.Shop.OrdersController.Create()");
const B = sym(2, "Acme.Shop.CreateOrderHandler.Handle(CreateOrder)");
const C = sym(3, "Acme.Shop.OrderService.PlaceOrder(Order)");

/** 记下所有副作用的假宿主。loadFlow 默认立即返回 flow(id)；pending 里的 id 要手动 resolve。 */
function harness(storage: StorageLike | null = new MemStorage(), opts: { flows?: (s: SymbolDto) => FlowDto | null; graphDelay?: (s: SymbolDto) => Promise<void> } = {}) {
  const log: string[] = [];
  const shown: SeqViewState[] = [];
  let graphFocus: SymbolDto | null = null;
  const pending = new Map<number, (f: FlowDto | null) => void>();
  const deferred = new Set<number>();
  const deps: CallViewDeps = {
    storage: () => storage,
    loadFlow: (s) => {
      log.push(`load:${s.id}`);
      if (deferred.has(s.id)) return new Promise((r) => pending.set(s.id, r));
      return Promise.resolve(opts.flows ? opts.flows(s) : flow(s.id));
    },
    showSeq: (v) => shown.push(v),
    focusGraph: async (s) => { log.push(`graph:${s.id}`); await opts.graphDelay?.(s); graphFocus = s; },
    chainUpGraph: async (s) => { log.push(`up:${s.id}`); },
    graphFocus: () => graphFocus,
    onModeChange: (m) => log.push(`mode:${m}`),
  };
  const view = new CallView(deps);
  return { view, log, shown, pending, deferred, storage, last: () => shown[shown.length - 1]! };
}

describe("时序 | 网状 的记忆", () => {
  it("默认时序；用户切换后记住，下次打开面板还是上次的选择", async () => {
    const storage = new MemStorage();
    const h = harness(storage);
    expect(h.view.mode).toBe("seq");
    await h.view.setMode("graph");
    expect(storage.getItem(CallViewModeKey)).toBe("graph");
    expect(harness(storage).view.mode).toBe("graph");
    await h.view.setMode("seq");
    expect(harness(storage).view.mode).toBe("seq");
  });

  it("localStorage 不可用（读写都抛错）时用默认值，不影响切换", async () => {
    const broken: StorageLike = { getItem: () => { throw new Error("denied"); }, setItem: () => { throw new Error("denied"); } };
    expect(loadCallViewMode(() => broken)).toBe("seq");
    expect(loadCallViewMode(() => { throw new Error("no storage"); })).toBe("seq");
    expect(() => saveCallViewMode(() => broken, "graph")).not.toThrow();
    const h = harness(broken);
    await h.view.setMode("graph");
    expect(h.view.mode).toBe("graph");
  });

  it("存的值不认识就用默认", () => {
    const s = new MemStorage();
    s.setItem(CallViewModeKey, "tree");
    expect(loadCallViewMode(() => s)).toBe("seq");
  });
});

describe("时序模式下的焦点", () => {
  // 搜索框、编辑器右键、侧边栏「在面板中打开」、跟随光标、模块关系「在调用图中查看」、入口流程 / 地图里的链接：页面都走 CallView.focus
  for (const source of ["搜索框", "编辑器右键", "侧边栏", "跟随光标", "模块关系"]) {
    it(`${source}：以新焦点重画时序图，不碰网状图`, async () => {
      const h = harness();
      await h.view.focus(A);
      await h.view.focus(B);
      expect(h.log).toEqual(["load:1", "load:2"]);
      expect(h.last()).toMatchObject({ status: "ready", symbol: B, dto: { entrySymbolId: 2 } });
      expect(h.view.history.crumbs.map((c) => c.label)).toEqual(["OrdersController.Create", "CreateOrderHandler.Handle"]);
      expect(h.view.focusId()).toBe(2);
    });
  }

  it("先画「正在取」，拿到再画图；拿不到画 missing", async () => {
    const h = harness(new MemStorage(), { flows: (s) => (s.id === 3 ? null : flow(s.id)) });
    await h.view.focus(C);
    expect(h.shown.map((v) => v.status)).toEqual(["loading", "missing"]);
  });

  it("连续换焦点时只认最后一次的结果", async () => {
    const h = harness();
    h.deferred.add(1);
    const first = h.view.focus(A);
    await h.view.focus(B);
    h.pending.get(1)!(flow(1));
    await first;
    expect(h.last()).toMatchObject({ status: "ready", symbol: B });
  });

  it("网状模式下焦点交给网状图，时序数据不取", async () => {
    const h = harness();
    await h.view.setMode("graph");
    await h.view.focus(A);
    expect(h.log).toEqual(["mode:graph", "graph:1"]);
    expect(h.view.focusId()).toBe(1);
  });
});

describe("chain: \"in\"（影响范围的「在面板中打开」）", () => {
  it("等待切换到网状期间收到新焦点，不再续跑旧的向上追链", async () => {
    let release!: () => void;
    const delay = new Promise<void>(resolve => { release = resolve; });
    const h = harness(new MemStorage(), { graphDelay: (s) => s.id === A.id ? delay : Promise.resolve() });
    await h.view.focus(A);
    const obsolete = h.view.focus(B, { chain: "in" });
    await h.view.focus(C);
    release();
    await obsolete;
    expect(h.log).not.toContain("graph:2");
    expect(h.log).not.toContain("up:2");
  });

  it("切到网状并「↑ 到源头」；这次切换不记住", async () => {
    const storage = new MemStorage();
    const h = harness(storage);
    await h.view.focus(B, { chain: "in" });
    expect(h.view.mode).toBe("graph");
    expect(h.log).toEqual(["mode:graph", "graph:2", "up:2"]);
    expect(storage.getItem(CallViewModeKey)).toBeNull();
    expect(harness(storage).view.mode).toBe("seq");
  });

  it("时序焦点就是它时，切网状只重画一次", async () => {
    const h = harness();
    await h.view.focus(B);
    await h.view.focus(B, { chain: "in" });
    expect(h.log.filter((x) => x === "graph:2")).toHaveLength(1);
    expect(h.log[h.log.length - 1]).toBe("up:2");
  });

  it("chain: \"out\" 和普通聚焦一样（时序图本来就一路往下）", async () => {
    const h = harness();
    await h.view.focus(C, { chain: "out" });
    expect(h.view.mode).toBe("seq");
    expect(h.last()).toMatchObject({ status: "ready", symbol: C });
  });
});

describe("面包屑", () => {
  it("双击行换焦点记进历史；回退 / 点面包屑回到之前的焦点（用缓存，不重取）", async () => {
    const h = harness();
    await h.view.focus(A);
    await h.view.focus(B);
    await h.view.focus(C);
    await h.view.back();
    expect(h.last()).toMatchObject({ status: "ready", symbol: B });
    expect(h.view.history.currentIndex).toBe(1);
    await h.view.goTo(0);
    expect(h.last()).toMatchObject({ status: "ready", symbol: A });
    expect(h.log.filter((x) => x.startsWith("load:"))).toEqual(["load:1", "load:2", "load:3"]);
    // 回退后再换新焦点：丢掉前进的历史
    await h.view.focus(C);
    expect(h.view.history.crumbs.map((c) => c.symbol.id)).toEqual([1, 3]);
  });

  it("连续聚焦同一个符号不重复推面包屑", async () => {
    const h = harness();
    await h.view.focus(A);
    await h.view.focus(A);
    expect(h.view.history.crumbs).toHaveLength(1);
  });

  it("索引刷新：缓存作废，按新 id 找回当前焦点重画，面包屑不加长", async () => {
    const h = harness();
    await h.view.focus(A);
    await h.view.focus(B);
    const moved = { ...B, id: 20 };
    expect(await h.view.refresh(async () => moved)).toBe(true);
    expect(h.view.history.crumbs.map((c) => c.symbol.id)).toEqual([1, 20]);
    expect(h.last()).toMatchObject({ status: "ready", symbol: moved });
    await h.view.goTo(0);
    expect(h.log.filter((x) => x === "load:1")).toHaveLength(2);
    expect(await h.view.refresh(async () => null)).toBe(false);
  });
});

describe("切换画法时同步焦点", () => {
  it("索引刷新等待期间切换焦点，不用旧焦点覆盖当前历史", async () => {
    const h = harness();
    await h.view.focus(A);
    let resolve!: (symbol: SymbolDto) => void;
    const refreshing = h.view.refresh(() => new Promise<SymbolDto>(done => { resolve = done; }));
    await h.view.focus(B);
    resolve({ ...A, id: 100 });
    await refreshing;
    expect(h.view.focusId()).toBe(B.id);
    expect(h.last()).toMatchObject({ status: "ready", symbol: B });
  });

  it("时序 → 网状：网状图以时序焦点重画；网状 → 时序：时序以网状焦点重画", async () => {
    const h = harness();
    await h.view.focus(A);
    await h.view.setMode("graph");
    expect(h.log).toEqual(["load:1", "mode:graph", "graph:1"]);
    await h.view.focus(C); // 网状里换了焦点
    await h.view.setMode("seq");
    expect(h.last()).toMatchObject({ status: "ready", symbol: C });
    expect(h.view.history.crumbs.map((c) => c.symbol.id)).toEqual([1, 3]);
  });

  it("两边焦点一样时切换不重取", async () => {
    const h = harness();
    await h.view.focus(A);
    await h.view.setMode("graph");
    await h.view.setMode("seq");
    expect(h.log.filter((x) => x.startsWith("load:"))).toEqual(["load:1"]);
  });
});
