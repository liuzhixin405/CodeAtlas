import { describe, expect, it } from "vitest";
import {
  baseName, childSource, clickTarget, ctxLabel, definitionTarget, expandedAncestors, inPlaceChildren, isOutlineSymbol, itemSpec,
  outlineChildren, rootAncestors, shortName, splitQualified, stepItem, trustPos, type RootNode, type StepNode,
} from "../src/outlineModel";
import type { OutlineDto, OutlineStepDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string, over: Partial<SymbolDto> = {}): SymbolDto {
  const name = qualifiedName.split(".").pop()!;
  return {
    id, kind: "method", name, qualifiedName, signature: null, path: "src/Ordering/OrdersApi.cs",
    startLine: 10, startCol: 5, endLine: 40, endCol: 6, visibility: "public", ...over,
  };
}

function step(over: Partial<OutlineStepDto> = {}): OutlineStepDto {
  return {
    order: 1, kind: "call", label: "OrderService.Create", target: sym(2, "Acme.Shop.OrderService.Create", { path: "src/Ordering/OrderService.cs", startLine: 20 }),
    line: 42, pos: 1234, ctx: null, confidence: 1, expandable: true, repeat: 1, children: [], ...over,
  };
}

function node(s: OutlineStepDto, ancestors: number[] = [1], containerPath = "src/Ordering/OrdersApi.cs"): StepNode {
  return { type: "step", step: s, containerPath, ancestors: new Set(ancestors) };
}

const root: RootNode = { type: "root", symbol: sym(1, "Acme.Shop.Api.OrdersApi.CreateOrderAsync", { startLine: 38 }), repoRoot: "/repo" };

describe("步骤 → 条目", () => {
  it("label 是 序号 + 两个空格 + 显示名；Repeat > 1 加 ×N", () => {
    expect(stepItem(node(step())).label).toBe("1  OrderService.Create");
    expect(stepItem(node(step({ order: 3, repeat: 4 }))).label).toBe("3  OrderService.Create ×4");
  });

  it("description：ctx 中文 + · L行", () => {
    expect(stepItem(node(step())).description).toBe("L42");
    expect(stepItem(node(step({ ctx: "loop", line: 45 }))).description).toBe("↻ 循环内 · L45");
    expect(stepItem(node(step({ ctx: "cond" }))).description).toBe("? 条件内 · L42");
    expect(stepItem(node(step({ ctx: "try" }))).description).toBe("try 内 · L42");
    expect(stepItem(node(step({ ctx: "catch" }))).description).toBe("catch 内 · L42");
    expect(stepItem(node(step({ ctx: "callback" }))).description).toBe("λ 回调 · L42");
  });

  it("不认识的 ctx 原样显示，不出 undefined", () => {
    expect(ctxLabel("using" as never)).toBe("using");
    expect(ctxLabel(null)).toBeNull();
  });

  it("图标按 kind", () => {
    const icon = (kind: OutlineStepDto["kind"]) => stepItem(node(step({ kind }))).icon;
    expect(icon("call")).toBe("symbol-method");
    expect(icon("new")).toBe("symbol-constructor");
    expect(icon("dispatch")).toBe("send");
    expect(icon("impl")).toBe("symbol-interface");
    expect(icon("external")).toBe("library");
    expect(icon("ambiguous")).toBe("question");
  });

  it("external：置灰、描述带「框架」、不能展开、没有转到定义", () => {
    const ext = stepItem(node(step({
      kind: "external", label: "ILogger.LogInformation", expandable: false, ctx: "cond", line: 55,
      target: sym(9, "Microsoft.Extensions.Logging.ILogger.LogInformation", { kind: "external", path: "?" }),
    })));
    expect(ext.dim).toBe(true);
    expect(ext.iconColor).toBe("disabledForeground");
    expect(ext.description).toBe("框架 · ? 条件内 · L55");
    expect(ext.collapsible).toBe("none");
    expect(ext.contextValue).not.toMatch(/target/);
  });

  it("new 在描述里标出来", () => {
    expect(stepItem(node(step({ kind: "new", label: "CreateOrderCommand", expandable: false }))).description).toBe("new · L42");
  });

  it("可展开：expandable 收起待取；不可展开的是叶子", () => {
    expect(stepItem(node(step())).collapsible).toBe("collapsed");
    expect(childSource(node(step()))).toBe("fetch");
    expect(stepItem(node(step({ expandable: false }))).collapsible).toBe("none");
  });

  it("递归：target 已在展开路径上 → 标 ↺ 递归、不可展开", () => {
    const s = step({ target: sym(1, "Acme.Shop.Api.OrdersApi.CreateOrderAsync"), label: "OrdersApi.CreateOrderAsync" });
    const item = stepItem(node(s, [1]));
    expect(item.description).toBe("↺ 递归 · L42");
    expect(item.collapsible).toBe("none");
    expect(item.tooltip).toContain("递归");
    // 不在路径上就正常展开
    expect(stepItem(node(s, [7])).collapsible).toBe("collapsed");
  });

  it("tooltip：限定名、定义文件:行、调用处、置信度；< 0.7 注明推测", () => {
    const t = stepItem(node(step({ confidence: 0.5 }))).tooltip;
    expect(t).toContain("Acme.Shop.OrderService.Create");
    expect(t).toContain("src/Ordering/OrderService.cs:20");
    expect(t).toContain("src/Ordering/OrdersApi.cs:42");
    expect(t).toContain("置信度 0.50（推测）");
    expect(stepItem(node(step({ confidence: 0.9 }))).tooltip).toContain("置信度 0.90");
    expect(stepItem(node(step({ confidence: 0.9 }))).tooltip).not.toContain("推测");
  });

  it("有 children（dispatch / impl / ambiguous 候选）：直接展开就地子项，不发请求", () => {
    const handler = step({ order: 1, kind: "dispatch", label: "CreateOrderIdentifiedCommandHandler.Handle", target: sym(5, "Acme.Shop.CreateOrderIdentifiedCommandHandler.Handle", { path: "src/Ordering/Handlers.cs" }) });
    const send = step({ order: 2, kind: "external", label: "IMediator.Send", target: null, expandable: false, children: [handler] });
    const n = node(send, [1]);
    expect(childSource(n)).toBe("inPlace");
    expect(stepItem(n).collapsible).toBe("expanded");
    const kids = inPlaceChildren(n);
    expect(kids).toHaveLength(1);
    // 就地子项与父步骤同一调用处、同一路径
    expect(kids[0]!.containerPath).toBe("src/Ordering/OrdersApi.cs");
    expect([...kids[0]!.ancestors]).toEqual([1]);
    // 子项自己能展开
    expect(childSource(kids[0]!)).toBe("fetch");
    expect(stepItem(kids[0]!).icon).toBe("send");
  });

  it("ambiguous：问号图标、target 为空也不报错，候选作为子项", () => {
    const a = step({ order: 1, kind: "call", label: "A.Run" });
    const b = step({ order: 2, kind: "call", label: "B.Run", target: sym(3, "Acme.B.Run") });
    const amb = node(step({ kind: "ambiguous", label: "Run", target: null, expandable: false, confidence: 0.3, children: [a, b] }));
    const item = stepItem(amb);
    expect(item.icon).toBe("question");
    expect(item.collapsible).toBe("expanded");
    expect(item.tooltip).toContain("多个候选");
    expect(item.contextValue).not.toMatch(/target/);
    expect(inPlaceChildren(amb).map((k) => stepItem(k).label)).toEqual(["1  A.Run", "2  B.Run"]);
  });
});

describe("根节点与层级", () => {
  it("根：类型.方法 + 文件名:行，可转到定义", () => {
    const item = itemSpec(root);
    expect(item.label).toBe("OrdersApi.CreateOrderAsync");
    expect(item.description).toBe("OrdersApi.cs:38");
    expect(item.collapsible).toBe("expanded");
    expect(item.contextValue).toMatch(/target/);
  });

  it("shortName 不切泛型和参数表里的点", () => {
    expect(shortName(sym(1, "Acme.Repo<Acme.Order>.Get(System.Int32)"))).toBe("Repo<Acme.Order>.Get");
    expect(shortName(sym(1, "run"))).toBe("run");
    expect(splitQualified("a.b<c.d>.e")).toEqual(["a", "b<c.d>", "e"]);
    expect(baseName("src\\a\\b.cs")).toBe("b.cs");
  });

  it("大纲 → 子节点：容器文件是本方法；空 / 截断给提示节点", () => {
    const outline: OutlineDto = { symbol: root.symbol, steps: [step(), step({ order: 2 })], truncated: true };
    const kids = outlineChildren(outline, rootAncestors(root));
    expect(kids.map((k) => k.type)).toEqual(["step", "step", "info"]);
    expect((kids[0] as StepNode).containerPath).toBe(root.symbol.path);
    expect(outlineChildren({ symbol: root.symbol, steps: [], truncated: false }, rootAncestors(root))).toEqual([
      expect.objectContaining({ type: "info", text: "（没有调用）" }),
    ]);
  });

  it("展开一层后 ancestors 加上这一步的 target，再出现就是递归", () => {
    const first = node(step(), [1]);
    const anc = expandedAncestors(first);
    expect([...anc].sort()).toEqual([1, 2]);
    const inner = step({ target: sym(2, "Acme.Shop.OrderService.Create") });
    const childOutline: OutlineDto = { symbol: first.step.target!, steps: [inner], truncated: false };
    const kids = outlineChildren(childOutline, anc) as StepNode[];
    expect(kids[0]!.containerPath).toBe("src/Ordering/OrderService.cs");
    expect(itemSpec(kids[0]!).description).toContain("↺ 递归");
  });
});

describe("跳转", () => {
  it("单击步骤：当前所在方法的文件 + pos；pos 为空时按行", () => {
    expect(clickTarget(node(step()))).toEqual({ path: "src/Ordering/OrdersApi.cs", line: 42, col: null, pos: 1234 });
    expect(clickTarget(node(step({ pos: null })))).toEqual({ path: "src/Ordering/OrdersApi.cs", line: 42, col: null, pos: null });
    expect(clickTarget(root)).toEqual({ path: root.symbol.path, line: 38, col: 5, pos: null });
    expect(clickTarget({ type: "info", text: "x" })).toBeNull();
  });

  it("转到定义：target 的文件:行；外部符号没有", () => {
    expect(definitionTarget(node(step()))).toEqual({ path: "src/Ordering/OrderService.cs", line: 20, col: 5, pos: null });
    expect(definitionTarget(node(step({ kind: "external", target: sym(9, "System.Console.WriteLine", { kind: "external", path: "?" }) })))).toBeNull();
    expect(definitionTarget(node(step({ kind: "ambiguous", target: null })))).toBeNull();
  });

  it("pos 换算的行和记录的行不一致就不信 pos", () => {
    expect(trustPos(42, 42)).toBe(true);
    expect(trustPos(40, 42)).toBe(false);
  });

  it("只有方法 / 函数能当根", () => {
    expect(isOutlineSymbol(sym(1, "A.B"))).toBe(true);
    expect(isOutlineSymbol(sym(1, "a", { kind: "function" }))).toBe(true);
    expect(isOutlineSymbol(sym(1, "A", { kind: "class" }))).toBe(false);
    expect(isOutlineSymbol(sym(1, "A.P", { kind: "property" }))).toBe(false);
  });
});
