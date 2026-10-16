import { describe, expect, it } from "vitest";
import { calleePickItems, callerPickItem, callerPickItems, entryPickItems, jumpPosition, linesText, MaxPickLines, type DocLike } from "../src/jumpModel";
import type { ImpactCallerDto, ImpactDto, ImpactEntryDto, OutlineDto, OutlineStepDto, SymbolDto } from "../webview/types";

function sym(id: number, name: string, path = "src/Orders/OrderService.cs", startLine = 10, kind = "method"): SymbolDto {
  return { id, kind, name, qualifiedName: `Acme.Shop.Orders.OrderService.${name}`, signature: null, path, startLine, startCol: 5, endLine: startLine + 9, endCol: 6, visibility: "public" };
}

function caller(over: Partial<ImpactCallerDto> = {}): ImpactCallerDto {
  return {
    kind: "call",
    label: "OrdersController.Create",
    caller: sym(2, "Create", "src/Api/OrdersController.cs", 20),
    sites: [{ line: 25, pos: 812, ctx: null }, { line: 31, pos: 990, ctx: "cond" }],
    confidence: 1,
    expandable: true,
    asEntry: [],
    ...over,
  };
}

describe("调用方 QuickPick 项", () => {
  it("label 调用方，description 方式 + 各处行号，detail 文件；选中跳到第一处调用（带 pos）", () => {
    const item = callerPickItem(caller());
    expect(item).toMatchObject({
      label: "OrdersController.Create",
      description: "调用 · L25, L31",
      detail: "src/Api/OrdersController.cs",
      target: { path: "src/Api/OrdersController.cs", line: 25, col: null, pos: 812 },
      definition: { path: "src/Api/OrdersController.cs", line: 20, col: 5, pos: null },
    });
  });

  it("经接口 / 经分派 / 构造 / 跨服务写成中文", () => {
    expect(callerPickItem(caller({ kind: "impl" })).description).toBe("经接口 · L25, L31");
    expect(callerPickItem(caller({ kind: "dispatch" })).description.startsWith("经分派")).toBe(true);
    expect(callerPickItem(caller({ kind: "new" })).description.startsWith("构造")).toBe(true);
    expect(callerPickItem(caller({ kind: "hop" })).description.startsWith("跨服务")).toBe(true);
  });

  it("没有调用处（老服务）：退回调用方定义", () => {
    const item = callerPickItem(caller({ sites: [] }));
    expect(item.description).toBe("调用");
    expect(item.target).toEqual({ path: "src/Api/OrdersController.cs", line: 20, col: 5, pos: null });
  });

  it("行号太多：写前几个加「等 N 处」", () => {
    const lines = Array.from({ length: MaxPickLines + 2 }, (_, i) => i + 1);
    expect(linesText(lines)).toBe(`${lines.slice(0, MaxPickLines).map((l) => `L${l}`).join(", ")} 等 ${lines.length} 处`);
  });

  it("ImpactDto → 每个调用方一项，保持服务给的顺序", () => {
    const impact: ImpactDto = { symbol: sym(1, "PlaceOrder"), callers: [caller(), caller({ label: "CheckoutJob.Run" })], entries: null, entriesTruncated: false, testCallers: 0, truncated: false };
    expect(callerPickItems(impact).map((i) => i.label)).toEqual(["OrdersController.Create", "CheckoutJob.Run"]);
  });
});

function step(over: Partial<OutlineStepDto> = {}): OutlineStepDto {
  return { order: 1, kind: "call", label: "OrderRepository.Save", target: sym(3, "Save", "src/Orders/OrderRepository.cs", 40), line: 14, pos: 420, ctx: null, confidence: 1, expandable: true, repeat: 1, children: [], ...over };
}

describe("被调方 QuickPick 项（调用大纲第一层）", () => {
  const outline = (steps: OutlineStepDto[]): OutlineDto => ({ symbol: sym(1, "PlaceOrder"), steps, truncated: false });

  it("复用调用大纲文案；选中跳到调用处（当前方法的文件），按钮转到定义", () => {
    const [item] = calleePickItems(outline([step({ ctx: "loop" })]));
    expect(item).toMatchObject({
      label: "OrderRepository.Save",
      description: "↻ 循环内 · L14",
      detail: "src/Orders/OrderRepository.cs:40",
      target: { path: "src/Orders/OrderService.cs", line: 14, col: null, pos: 420 },
      definition: { path: "src/Orders/OrderRepository.cs", line: 40, col: 5, pos: null },
    });
  });

  it("框架调用：description 标「框架」，没有转到定义", () => {
    const [item] = calleePickItems(outline([step({ kind: "external", label: "ILogger.LogInformation", target: sym(9, "LogInformation", "?", 0, "external") })]));
    expect(item!.description).toBe("框架 · L14");
    expect(item!.definition).toBeNull();
    expect(item!.detail).toBe("仓库外");
  });

  it("相邻同一调用合并：label 带 ×N；多个候选：detail 写候选数", () => {
    const items = calleePickItems(outline([step({ repeat: 3 }), step({ kind: "ambiguous", target: null, label: "Handle", children: [step(), step()] })]));
    expect(items[0]!.label).toBe("OrderRepository.Save ×3");
    expect(items[1]!.detail).toBe("2 个候选");
    expect(items[1]!.definition).toBeNull();
  });

  it("分发调用下的 Handler、接口调用下的实现：各补一项「↳ …」，选中直接跳到定义（不是停在 Send 那一行）", () => {
    const handler = step({ kind: "dispatch", label: "RestartFeedsHandler.HandleAsync", target: sym(30, "HandleAsync", "src/Feeds/RestartFeedsHandler.cs", 22), line: 14, pos: 420 });
    const impl = step({ kind: "impl", label: "OrderRepository.Save", target: sym(31, "Save", "src/Orders/OrderRepository.cs", 40) });
    const external = step({ kind: "external", label: "Task.FromResult", target: sym(9, "FromResult", "?", 0, "external") });
    const items = calleePickItems(outline([
      step({ kind: "external", label: "IDispatcher.SendAsync", target: sym(8, "SendAsync", "?", 0, "external"), children: [handler, external] }),
      step({ kind: "call", label: "IOrderRepository.Save", children: [impl] }),
      step({ kind: "dispatch", label: "PlaceOrderHandler.Handle", target: sym(32, "Handle", "src/Orders/PlaceOrderHandler.cs", 12), children: [] }),
    ]));
    expect(items.map((i) => i.label)).toEqual(["IDispatcher.SendAsync", "↳ RestartFeedsHandler.HandleAsync", "IOrderRepository.Save", "↳ OrderRepository.Save", "PlaceOrderHandler.Handle"]);
    expect(items[1]).toMatchObject({ target: { path: "src/Feeds/RestartFeedsHandler.cs", line: 22, col: 5, pos: null } });
    expect(items[1]!.description.startsWith("经分派 · ")).toBe(true);
    expect(items[3]!.description.startsWith("经接口 · ")).toBe(true);
    expect(items[3]!.target).toEqual(items[3]!.definition);
    // 第一层本身就是 dispatch：同样跳到 Handler 定义；Send 那一项照旧跳到调用处
    expect(items[4]!.target).toMatchObject({ path: "src/Orders/PlaceOrderHandler.cs", line: 12 });
    expect(items[0]!.target).toMatchObject({ path: "src/Orders/OrderService.cs", line: 14 });
  });
});

describe("受影响的入口 QuickPick 项", () => {
  const entry = (name: string, depth: number, kind = "http"): ImpactEntryDto => ({
    entry: { symbolId: depth + 100, kind, ruleId: "r", route: kind === "http" ? "/api/orders" : null, httpMethod: kind === "http" ? "POST" : null, name, qualifiedName: `Acme.Shop.${name}`, file: "src/Api/OrdersController.cs", line: 30 + depth },
    containerKey: "api",
    containerName: "Acme.Shop.Api",
    depth,
  });

  it("按层数从近到远；跳到入口方法；depth 0 写「它自己就是入口」", () => {
    const items = entryPickItems([entry("CleanupJob", 3, "job"), entry("Create", 1), entry("Self", 0, "job")]);
    expect(items.map((i) => i.description)).toEqual(["Acme.Shop.Api · 它自己就是入口", "Acme.Shop.Api · 往上 1 层", "Acme.Shop.Api · 往上 3 层"]);
    expect(items[1]!.label).toContain("HTTP");
    expect(items[1]!.target).toEqual({ path: "src/Api/OrdersController.cs", line: 31, col: null, pos: null });
    expect(items[1]!.detail).toBe("src/Api/OrdersController.cs:31");
  });
});

describe("跳转位置", () => {
  /** 每行 10 个字符（含换行）：偏移 n 在第 floor(n/10) 行。 */
  const doc = (lineCount: number, indent = 4): DocLike => ({
    lineCount,
    positionAt: (o) => ({ line: Math.floor(o / 10), character: o % 10 }),
    firstNonWhitespace: () => indent,
  });

  it("pos 换算出的行与记录的行一致：用 pos（落在调用名上）", () => {
    expect(jumpPosition(doc(50), { path: "a", line: 13, col: null, pos: 127 })).toEqual({ line: 12, character: 7 });
  });

  it("对不上（文件改过）：退回按行，行首第一个非空白", () => {
    expect(jumpPosition(doc(50), { path: "a", line: 20, col: null, pos: 127 })).toEqual({ line: 19, character: 4 });
  });

  it("没有 pos：有列用列，否则行首第一个非空白", () => {
    expect(jumpPosition(doc(50), { path: "a", line: 5, col: 9, pos: null })).toEqual({ line: 4, character: 8 });
    expect(jumpPosition(doc(50, 2), { path: "a", line: 5, col: null, pos: null })).toEqual({ line: 4, character: 2 });
  });

  it("行号超出文档：夹到最后一行", () => {
    expect(jumpPosition(doc(3), { path: "a", line: 99, col: null, pos: null })).toEqual({ line: 2, character: 4 });
  });
});
