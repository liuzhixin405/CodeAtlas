import { describe, expect, it } from "vitest";
import {
  callerChildren, callerItem, canExpand, containerChildren, entriesChildren, entryContainers, entryLabel, expandedAncestors, impactClick,
  impactDefinition, impactItemSpec, rootAncestors, rootChildren,
  type CallerNode, type CallersGroupNode, type ContainerNode, type EntriesGroupNode, type ImpactRootNode,
} from "../src/impactModel";
import type { EntryDto, ImpactCallerDto, ImpactDto, ImpactEntryDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string, over: Partial<SymbolDto> = {}): SymbolDto {
  const name = qualifiedName.split(".").pop()!;
  return {
    id, kind: "method", name, qualifiedName, signature: null, path: "src/Ordering/OrderService.cs",
    startLine: 20, startCol: 5, endLine: 60, endCol: 6, visibility: "public", ...over,
  };
}

function entry(over: Partial<EntryDto> = {}): EntryDto {
  return {
    symbolId: 50, kind: "http", ruleId: "aspnet-minimal", route: "/api/orders", httpMethod: "POST", name: "CreateOrder",
    qualifiedName: "Acme.Shop.Api.OrdersApi.CreateOrder", file: "src/Api/OrdersApi.cs", line: 12, ...over,
  };
}

function ie(e: Partial<EntryDto> = {}, depth = 2, containerKey = "src/Api", containerName = "Ordering.API"): ImpactEntryDto {
  return { entry: entry(e), containerKey, containerName, depth };
}

function caller(over: Partial<ImpactCallerDto> = {}): ImpactCallerDto {
  return {
    kind: "call", label: "OrdersApi.CreateOrderAsync",
    caller: sym(2, "Acme.Shop.Api.OrdersApi.CreateOrderAsync", { path: "src/Api/OrdersApi.cs", startLine: 38 }),
    sites: [{ line: 45, pos: 1500, ctx: null }], confidence: 1, expandable: true, asEntry: [], ...over,
  };
}

function impact(over: Partial<ImpactDto> = {}): ImpactDto {
  return { symbol: root.symbol, callers: [caller()], entries: [ie()], entriesTruncated: false, testCallers: 0, truncated: false, ...over };
}

const root: ImpactRootNode = { type: "root", symbol: sym(1, "Acme.Shop.Ordering.OrderService.Create"), repoRoot: "/repo" };

function cnode(c: ImpactCallerDto, ancestors: number[] = [1]): CallerNode {
  return { type: "caller", caller: c, ancestors: new Set(ancestors) };
}

describe("根与分组", () => {
  it("根：类型.方法 + 文件名:行；下面先入口组再调用方组", () => {
    expect(impactItemSpec(root)).toMatchObject({ label: "OrderService.Create", description: "OrderService.cs:20", collapsible: "expanded" });
    const kids = rootChildren(impact(), root, false);
    expect(kids.map((k) => k.type)).toEqual(["entries", "callers"]);
    expect(impactItemSpec(kids[0]!).label).toBe("受影响的入口（1）");
    expect(impactItemSpec(kids[1]!).label).toBe("直接调用方（1）");
  });

  it("入口截断：分组 description 加「（可能不全）」", () => {
    const g = rootChildren(impact({ entriesTruncated: true }), root, false)[0]!;
    expect(impactItemSpec(g).description).toBe("（可能不全）");
    expect(impactItemSpec(rootChildren(impact(), root, false)[0]!).description).toBe("");
  });

  it("0 个入口：「没有走到任何入口」，不可展开", () => {
    const g = rootChildren(impact({ entries: [] }), root, false)[0]!;
    expect(impactItemSpec(g)).toMatchObject({ label: "没有走到任何入口", collapsible: "none" });
  });

  it("服务没给 entries（老服务）：不报错、不可展开", () => {
    const g = rootChildren(impact({ entries: null }), root, false)[0] as EntriesGroupNode;
    expect(impactItemSpec(g).collapsible).toBe("none");
    expect(entriesChildren(g)).toEqual([]);
  });

  it("调用方截断：组标题 N+，末尾提示", () => {
    const g = rootChildren(impact({ truncated: true }), root, false)[1] as CallersGroupNode;
    expect(impactItemSpec(g).label).toBe("直接调用方（1+）");
    expect(g.children.at(-1)).toMatchObject({ type: "info", text: "…调用方过多，后面的省略了" });
  });

  it("没有调用方：提示行", () => {
    expect(callerChildren(impact({ callers: [] }), new Set([1]), false)).toEqual([expect.objectContaining({ type: "info", text: "（没有调用方）" })]);
  });
});

describe("入口按容器分组", () => {
  const entries = [
    ie({ kind: "job", route: null, httpMethod: null, name: "OrderCleanupJob", file: "src/Jobs/Cleanup.cs", line: 8 }, 3, "src/Worker", "Ordering.Worker"),
    ie({ route: "/api/orders/{id}", httpMethod: "PUT", file: "src/Api/OrdersApi.cs", line: 30 }, 2),
    ie({}, 1),
    ie({ kind: "command", route: "remote add", httpMethod: null, name: "RemoteAdd" }, 4, "tools/cli", "Acme.Cli"),
  ];

  it("容器按最浅深度排；容器里按深度排；多个容器时收起", () => {
    const cs = entryContainers(entries);
    expect(cs.map((c) => c.name)).toEqual(["Ordering.API", "Ordering.Worker", "Acme.Cli"]);
    const api = cs[0]!;
    expect(impactItemSpec(api)).toMatchObject({ label: "Ordering.API", description: "2 个入口", collapsible: "collapsed" });
    expect(containerChildren(api).map((n) => impactItemSpec(n).label)).toEqual(["HTTP POST /api/orders", "HTTP PUT /api/orders/{id}"]);
    expect(containerChildren(api).map((n) => impactItemSpec(n).description)).toEqual(["深度 1", "深度 2"]);
  });

  it("只有一个容器时直接展开", () => {
    const [only] = entriesChildren({ type: "entries", entries: [ie()], truncated: false }) as ContainerNode[];
    expect(impactItemSpec(only!).collapsible).toBe("expanded");
  });

  it("入口文案：HTTP 方法+路由、定时任务 名字、命令 路由；不认识的种类原样", () => {
    expect(entryLabel(entry())).toBe("HTTP POST /api/orders");
    expect(entryLabel(entry({ kind: "job", route: null, httpMethod: null, name: "OrderCleanupJob" }))).toBe("定时任务 OrderCleanupJob");
    expect(entryLabel(entry({ kind: "command", route: "remote add", httpMethod: null }))).toBe("命令 remote add");
    expect(entryLabel(entry({ kind: "queue", route: null, httpMethod: null, name: "OnOrderPaid" }))).toBe("queue OnOrderPaid");
  });

  it("单击入口：跳到入口方法定义（entry.file:line）", () => {
    const [n] = containerChildren(entryContainers([ie()])[0]!);
    expect(impactClick(n!)).toEqual({ kind: "jump", target: { path: "src/Api/OrdersApi.cs", line: 12, col: null, pos: null } });
    expect(impactItemSpec(n!).tooltip).toContain("HTTP 路由");
  });
});

describe("调用方 → 条目", () => {
  it("call 不写种类；description 是 L行", () => {
    expect(callerItem(cnode(caller()))).toMatchObject({ label: "OrdersApi.CreateOrderAsync", description: "L45", icon: "symbol-method" });
  });

  it("impl 经接口 / dispatch 经分派 / new 构造", () => {
    expect(callerItem(cnode(caller({ kind: "impl" }))).description).toBe("经接口 · L45");
    expect(callerItem(cnode(caller({ kind: "dispatch" }))).description).toBe("经分派 · L45");
    expect(callerItem(cnode(caller({ kind: "new" }))).description).toBe("构造 · L45");
    expect(callerItem(cnode(caller({ kind: "dispatch" }))).icon).toBe("send");
  });

  it("多处调用：×N + 第一处的 ctx 中文 + 第一处行号；tooltip 列各处行号", () => {
    const c = caller({ sites: [{ line: 45, pos: 1500, ctx: "loop" }, { line: 52, pos: 1800, ctx: null }, { line: 60, pos: 2100, ctx: "catch" }] });
    const item = callerItem(cnode(c));
    expect(item.description).toBe("×3 · ↻ 循环内 · L45");
    expect(item.tooltip).toContain("L45（↻ 循环内）、L52、L60（catch 内）");
    expect(item.tooltip).toContain("单击跳到第一处调用");
  });

  it("调用方本身是入口：入口图标、description 以入口开头", () => {
    const c = caller({ kind: "impl", asEntry: [ie({}, 0)] });
    const item = callerItem(cnode(c));
    expect(item.icon).toBe("globe");
    expect(item.description).toBe("HTTP POST /api/orders · 经接口 · L45");
    expect(item.tooltip).toContain("本身是入口：HTTP POST /api/orders（Ordering.API）");
    const two = callerItem(cnode(caller({ asEntry: [ie({}, 0), ie({ httpMethod: "GET" }, 0)] })));
    expect(two.description).toBe("HTTP POST /api/orders 等 2 个入口 · L45");
  });

  it("tooltip：完整限定名、定义文件:行、置信度；< 0.7 注明推测", () => {
    const t = callerItem(cnode(caller({ confidence: 0.5 }))).tooltip;
    expect(t).toContain("Acme.Shop.Api.OrdersApi.CreateOrderAsync");
    expect(t).toContain("定义：src/Api/OrdersApi.cs:38");
    expect(t).toContain("调用处（src/Api/OrdersApi.cs）：L45");
    expect(t).toContain("置信度 0.50（推测）");
    expect(callerItem(cnode(caller({ confidence: 0.9 }))).tooltip).not.toContain("推测");
  });

  it("可展开：expandable 收起待取；不可展开是叶子；有定义才有「转到定义」", () => {
    expect(callerItem(cnode(caller())).collapsible).toBe("collapsed");
    expect(callerItem(cnode(caller({ expandable: false }))).collapsible).toBe("none");
    expect(callerItem(cnode(caller())).contextValue).toMatch(/target/);
  });

  it("递归：展开路径上已出现的调用方标 ↺ 递归、不可展开", () => {
    const n = cnode(caller(), [1, 2]);
    expect(canExpand(n)).toBe(false);
    const item = callerItem(n);
    expect(item.description).toBe("↺ 递归 · L45");
    expect(item.collapsible).toBe("none");
    expect(item.tooltip).toContain("递归");
  });

  it("展开一层：ancestors 加上这个调用方，再出现就是递归", () => {
    const first = cnode(caller(), [1]);
    const anc = expandedAncestors(first);
    expect([...anc].sort()).toEqual([1, 2]);
    // 调用方 2 的调用方里又出现了根 1（互相调用）
    const back = caller({ label: "OrderService.Create", caller: root.symbol });
    const kids = callerChildren(impact({ callers: [back, caller({ label: "X.Y", caller: sym(3, "Acme.X.Y") })] }), anc, false) as CallerNode[];
    expect(impactItemSpec(kids[0]!).description).toContain("↺ 递归");
    expect(canExpand(kids[1]!)).toBe(true);
    expect(rootAncestors(root).has(1)).toBe(true);
  });
});

describe("测试调用方", () => {
  it("tests=false 且 TestCallers>0：最后一行「被 N 个测试调用」，单击 = 切到包含测试", () => {
    const kids = callerChildren(impact({ testCallers: 3, truncated: true }), new Set([1]), false);
    const tests = kids.find((k) => k.type === "tests")!;
    expect(impactItemSpec(tests).label).toBe("被 3 个测试调用");
    expect(impactClick(tests)).toEqual({ kind: "includeTests" });
    // 在截断提示之前、调用方之后
    expect(kids.map((k) => k.type)).toEqual(["caller", "tests", "info"]);
  });

  it("包含测试时不再显示测试行；没有测试调用方也不显示", () => {
    expect(callerChildren(impact({ testCallers: 3 }), new Set([1]), true).some((k) => k.type === "tests")).toBe(false);
    expect(callerChildren(impact({ testCallers: 0 }), new Set([1]), false).some((k) => k.type === "tests")).toBe(false);
  });

  it("只有测试调用方：组里只有测试行，没有「没有调用方」", () => {
    const kids = callerChildren(impact({ callers: [], testCallers: 2 }), new Set([1]), false);
    expect(kids.map((k) => k.type)).toEqual(["tests"]);
  });
});

describe("跳转", () => {
  it("单击调用方：调用方文件 + 第一处的 pos；pos 为空时按行", () => {
    expect(impactClick(cnode(caller()))).toEqual({ kind: "jump", target: { path: "src/Api/OrdersApi.cs", line: 45, col: null, pos: 1500 } });
    expect(impactClick(cnode(caller({ sites: [{ line: 45, pos: null, ctx: null }] })))).toEqual({
      kind: "jump", target: { path: "src/Api/OrdersApi.cs", line: 45, col: null, pos: null },
    });
  });

  it("没有调用处：单击退到调用方定义", () => {
    expect(impactClick(cnode(caller({ sites: [] })))).toEqual({ kind: "jump", target: { path: "src/Api/OrdersApi.cs", line: 38, col: 5, pos: null } });
  });

  it("转到定义：调用方方法定义处；根就是它自己；分组 / 提示行没有", () => {
    expect(impactDefinition(cnode(caller()))).toEqual({ path: "src/Api/OrdersApi.cs", line: 38, col: 5, pos: null });
    expect(impactDefinition(root)).toEqual({ path: "src/Ordering/OrderService.cs", line: 20, col: 5, pos: null });
    expect(impactDefinition({ type: "info", text: "x" })).toBeNull();
    expect(impactClick({ type: "callers", count: 0, truncated: false, children: [] })).toBeNull();
  });
});
