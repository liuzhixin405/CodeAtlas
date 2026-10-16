import { describe, expect, it } from "vitest";
import { layoutFlow } from "../webview/flowLayout";
import {
  controllerEntries, entriesLabel, entryOwner, filterHandlerGroups, FrameworkPanelState, frameworkJumpTarget, frameworkJumpText, frameworkJumpTitle, frameworkSummary,
  groupByOwner, handlerAction, handlerKey, handlerTotal, isPlaceholder, messageKindLabel, pickFrameworkContainer, placeholderText, stageColumnLabel, stageOfActor,
} from "../webview/frameworkModel";
import { controllerListHtml, frameworkHeaderHtml, handlerEntryKey, handlerListHtml, panelTitle } from "../webview/frameworkHtml";
import { actorKindText, isFocusableNode, rowText } from "../webview/seqDiagram";
import type { ArchNodeDto, EntryDto, FlowNodeDto, FrameworkFlowDto, FrameworkHandlerDto, FrameworkStageDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string): SymbolDto {
  const name = qualifiedName.slice(qualifiedName.lastIndexOf(".") + 1);
  return { id, kind: "method", name, qualifiedName, signature: null, path: `src/Ordering.API/${name}.cs`, startLine: 10, startCol: 5, endLine: 20, endCol: 6, visibility: "public" };
}

function entry(symbolId: number, qualifiedName: string, route: string, httpMethod = "POST"): EntryDto {
  return { symbolId, kind: "http", ruleId: "csharp.minimal-api", route, httpMethod, name: qualifiedName.slice(qualifiedName.lastIndexOf(".") + 1), qualifiedName, file: "src/Ordering.API/Apis/OrdersApi.cs", line: 30 };
}

function node(p: Partial<FlowNodeDto> & Pick<FlowNodeDto, "label" | "kind" | "actor" | "depth">): FlowNodeDto {
  return { symbolId: null, qualifiedName: p.label, via: null, confidence: 1, count: 1, children: [], hidden: 0, note: null, ...p };
}

function stage(key: FrameworkStageDto["key"], label: string, orderKnown: boolean, n = 0): FrameworkStageDto {
  return { key, label, orderKnown, symbols: Array.from({ length: n }, (_, i) => sym(900 + i, `Acme.Shop.${key}${i}`)) };
}

const create = entry(11, "Acme.Shop.Ordering.API.Apis.OrdersApi.CreateOrderAsync", "/api/orders");
const cancel = entry(12, "Acme.Shop.Ordering.API.Apis.OrdersApi.CancelOrderAsync", "/api/orders/cancel", "PUT");
const ship = entry(13, "Acme.Shop.Ordering.API.Apis.OrdersApi.ShipOrderAsync", "/api/orders/ship", "PUT");
const getOrders = entry(14, "Acme.Shop.Ordering.API.Apis.OrdersApi.GetOrdersByUserAsync", "/api/orders", "GET");
const cardTypes = entry(15, "Acme.Shop.Ordering.API.Apis.CardTypesApi.GetCardTypesAsync", "/api/orders/cardtypes", "GET");

const createHandler: FrameworkHandlerDto = { handler: sym(101, "Acme.Shop.Ordering.API.Application.Commands.CreateOrderCommandHandler.Handle"), messageType: "CreateOrderCommand", messageKind: "command", entries: [create] };
const cancelHandler: FrameworkHandlerDto = { handler: sym(102, "Acme.Shop.Ordering.API.Application.Commands.CancelOrderCommandHandler.Handle"), messageType: "CancelOrderCommand", messageKind: "command", entries: [cancel, ship] };
const queryHandler: FrameworkHandlerDto = { handler: sym(103, "Acme.Shop.Ordering.API.Application.Queries.OrderQueriesHandler.Handle"), messageType: "GetOrdersQuery", messageKind: "query", entries: [getOrders] };
const eventHandler: FrameworkHandlerDto = { handler: sym(104, "Acme.Shop.Ordering.API.Application.DomainEventHandlers.OrderStartedDomainEventHandler.Handle"), messageType: "OrderStartedDomainEvent", messageKind: "event", entries: [] };

/** Acme.Shop 的 Ordering.API：中间件（顺序已知）→ 控制器占位 → 分派器 → 三个行为（顺序未知）→ Handler 占位 → 收尾。 */
const fw: FrameworkFlowDto = {
  containerKey: "Ordering.API",
  containerName: "Ordering.API",
  stages: [
    stage("middleware", "中间件", true, 2),
    stage("controller", "控制器", true, 26),
    stage("dispatcher", "分派器", true, 1),
    stage("behavior", "行为链", false, 3),
    stage("handler", "Handler", true),
    stage("tail", "收尾", true, 2),
  ],
  handlers: [
    { module: "Commands", items: [createHandler, cancelHandler] },
    { module: "Queries", items: [queryHandler] },
    { module: "DomainEventHandlers", items: [eventHandler] },
  ],
  notes: ["行为的先后来自 AddOpenBehavior 注册顺序，缺 Roslyn 索引时按名字排", "没有识别到全局异常中间件"],
  flow: {
    entrySymbolId: 0, entryLabel: "HTTP 请求", route: null, containerKey: "Ordering.API",
    actors: [
      { key: "middleware", label: "中间件", kind: "stage" },
      { key: "controller", label: "控制器", kind: "stage" },
      { key: "dispatcher", label: "分派器", kind: "stage" },
      { key: "behavior", label: "行为链", kind: "stage" },
      { key: "handler", label: "Handler", kind: "stage" },
      { key: "tail", label: "收尾", kind: "stage" },
    ],
    root: node({
      label: "HTTP 请求", kind: "entry", actor: "middleware", depth: 0,
      children: [
        node({ label: "UseAuthentication", kind: "call", actor: "middleware", depth: 1, symbolId: 201 }),
        node({ label: "控制器", kind: "controllers", actor: "controller", depth: 1, count: 26 }),
        node({ label: "IMediator.Send", kind: "dispatch", actor: "dispatcher", depth: 1, symbolId: 202 }),
        node({
          label: "LoggingBehavior.Handle", kind: "call", actor: "behavior", depth: 1, symbolId: 203,
          children: [node({ label: "ILogger.LogInformation", kind: "external-group", actor: "behavior", depth: 2 })],
        }),
        node({ label: "254 个 Handler", kind: "handlers", actor: "handler", depth: 1, count: 254 }),
        node({ label: "OrderingContext.SaveEntitiesAsync", kind: "call", actor: "tail", depth: 1, symbolId: 204 }),
      ],
    }),
    behaviors: [], nodes: 8, truncated: false, note: null,
  },
};

function container(key: string): ArchNodeDto {
  return { key, kind: "container", subKind: "dotnet-web", name: key, path: `src/${key}`, source: "discovered", evidence: [], members: [], ports: [], namespaceRoots: [], outbound: 0 };
}

describe("framework skeleton diagram", () => {
  it("draws placeholder nodes as one row each: 'N 个 Handler ▸' / 'N 个控制器 ▸', not ×N", () => {
    const { rows } = layoutFlow(fw.flow, new Set());
    const texts = rows.map(rowText);
    expect(texts).toContain("254 个 Handler ▸");
    expect(texts).toContain("26 个控制器 ▸");
    expect(texts.some((t) => t.includes("×254") || t.includes("×26"))).toBe(false);
    expect(placeholderText({ kind: "handlers", count: 3 })).toBe("3 个 Handler ▸");
    // 非 Web 容器的入口占位：用服务端给的 label
    expect(placeholderText({ kind: "controllers", count: 4, label: "入口（常驻服务 / 任务 / 消息消费）" })).toBe("4 个入口（常驻服务 / 任务 / 消息消费） ▸");
    expect(placeholderText({ kind: "controllers", count: 8, label: "控制器" })).toBe("8 个控制器 ▸");
    // 别的语言：服务端给"页面""路由"；没给 label 时按控制器
    expect(placeholderText({ kind: "controllers", count: 12, label: "页面" })).toBe("12 个页面 ▸");
    expect(placeholderText({ kind: "controllers", count: 2 })).toBe("2 个控制器 ▸");
    // 占位行画在自己的阶段列上，箭头从上一级（入口所在的中间件列）指过去
    const h = rows.find((r) => r.node.kind === "handlers")!;
    expect(h.toCol).toBe(4);
    expect(h.direction).toBe("right");
  });

  it("placeholders are not focusable; behaviour / tail calls are", () => {
    const { rows } = layoutFlow(fw.flow, new Set());
    const byLabel = (l: string) => rows.find((r) => r.node.label === l)!.node;
    expect(isPlaceholder(byLabel("254 个 Handler"))).toBe(true);
    expect(isFocusableNode(byLabel("254 个 Handler"))).toBe(false);
    expect(isFocusableNode(byLabel("控制器"))).toBe(false);
    expect(isFocusableNode(byLabel("LoggingBehavior.Handle"))).toBe(true);
    expect(isFocusableNode(byLabel("OrderingContext.SaveEntitiesAsync"))).toBe(true);
  });

  it("column headers are stage labels; orderKnown=false adds （顺序未知）", () => {
    const labels = fw.flow.actors.map((a) => stageColumnLabel(a, fw.stages));
    expect(labels).toEqual(["中间件", "控制器", "分派器", "行为链（顺序未知）", "Handler", "收尾"]);
    expect(actorKindText("stage")).toBe("阶段");
    // actor key 带前缀、或只认得出 label 时也能对上阶段
    expect(stageOfActor({ key: "stage|behavior", label: "x", kind: "stage" }, fw.stages)?.key).toBe("behavior");
    expect(stageOfActor({ key: "?", label: "收尾", kind: "stage" }, fw.stages)?.key).toBe("tail");
    expect(stageColumnLabel({ key: "?", label: "未知阶段", kind: "stage" }, fw.stages)).toBe("未知阶段");
  });

  it("header shows notes as a grey list, a container dropdown, and per-stage counts", () => {
    const html = frameworkHeaderHtml(fw, [container("Basket.API"), container("Ordering.API")]);
    expect(html).toContain('<ul class="fw-notes"><li>行为的先后来自 AddOpenBehavior 注册顺序，缺 Roslyn 索引时按名字排</li><li>没有识别到全局异常中间件</li></ul>');
    expect(html).toContain('<option value="Ordering.API" selected>Ordering.API</option>');
    expect(html).toContain('<option value="Basket.API">');
    expect(html).toContain("行为链 3（顺序未知）");
    expect(html).toContain("Handler 4");
    expect(frameworkHeaderHtml({ ...fw, notes: [] }, [])).not.toContain("fw-notes");
    // 当前容器不在下拉里（容器图还没取到）：补在最前面
    expect(frameworkHeaderHtml(fw, [container("Basket.API")])).toMatch(/<select[^>]*><option value="Ordering.API" selected>/);
  });

  it("summary lists every stage with counts", () => {
    expect(frameworkSummary(fw.stages, fw.handlers)).toBe("中间件 2 → 控制器 26 → 分派器 1 → 行为链 3（顺序未知） → Handler 4 → 收尾 2");
  });
});

describe("Handler / controller panel", () => {
  it("clicking a placeholder opens its list; clicking again closes; the other kind switches", () => {
    const s = new FrameworkPanelState();
    expect(s.open).toBeNull();
    expect(s.toggle("handlers")).toBe("handlers");
    expect(s.toggle("controllers")).toBe("controllers");
    expect(s.toggle("controllers")).toBeNull();
    s.toggle("handlers");
    s.setFilter("handlers", "cancel");
    s.toggleExpanded("x");
    s.reset();
    expect(s.open).toBeNull();
    expect(s.filter("handlers")).toBe("");
    expect(s.isExpanded("x")).toBe(false);
  });

  it("MessageKind reads in Chinese", () => {
    expect(messageKindLabel("command")).toBe("命令");
    expect(messageKindLabel("query")).toBe("查询");
    expect(messageKindLabel("event")).toBe("事件");
    expect(messageKindLabel("Command")).toBe("命令");
    expect(messageKindLabel(null)).toBe("");
    expect(messageKindLabel("notification")).toBe("notification");
  });

  it("list is grouped by module with kind badge, message type and entry count", () => {
    const html = handlerListHtml(fw.handlers, "", () => false);
    expect(html.match(/class="flow-group"/g)).toHaveLength(3);
    expect(html).toContain("Commands · 2");
    expect(html).toContain("DomainEventHandlers · 1");
    expect(html).toMatch(/<span class="fw-mk command">命令<\/span><span class="route">CreateOrderCommand<\/span><span class="name">入口 1<\/span>/);
    expect(html).toMatch(/<span class="fw-mk query">查询<\/span><span class="route">GetOrdersQuery<\/span>/);
    expect(html).toMatch(/<span class="fw-mk event">事件<\/span><span class="route">OrderStartedDomainEvent<\/span><span class="name">无入口<\/span>/);
    expect(html).toContain("入口 2 ▸");
    expect(handlerTotal(fw.handlers)).toBe(4);
    expect(panelTitle("handlers", fw, null)).toBe("Handler · 4");
  });

  it("filter matches message type, module and Chinese / English kind; empty groups drop out", () => {
    expect(filterHandlerGroups(fw.handlers, "cancel").map((g) => [g.module, g.items.map((h) => h.messageType)])).toEqual([["Commands", ["CancelOrderCommand"]]]);
    expect(filterHandlerGroups(fw.handlers, "查询").flatMap((g) => g.items.map((h) => h.messageType))).toEqual(["GetOrdersQuery"]);
    expect(filterHandlerGroups(fw.handlers, "event").flatMap((g) => g.items.map((h) => h.messageType))).toEqual(["OrderStartedDomainEvent"]);
    expect(filterHandlerGroups(fw.handlers, "commands order").flatMap((g) => g.items)).toHaveLength(2);
    expect(filterHandlerGroups(fw.handlers, "nothing-like-this")).toEqual([]);
    expect(handlerListHtml(fw.handlers, "nothing-like-this", () => false)).toContain("没有匹配的 Handler");
    expect(handlerListHtml([], "", () => false)).toContain("没有发现 Handler");
  });

  it("clicking a handler: one entry opens its entry flow, several let the user choose, none falls back to the call graph", () => {
    expect(handlerAction(createHandler)).toEqual({ kind: "entry", entry: create });
    expect(handlerAction(cancelHandler)).toEqual({ kind: "choose", entries: [cancel, ship] });
    expect(handlerAction(eventHandler)).toEqual({ kind: "graph", handler: eventHandler });
    // 展开后列出它的入口，每个都带可点的 data-he
    const open = handlerListHtml(fw.handlers, "", (k) => k === handlerKey(cancelHandler));
    expect(open).toContain("2 个入口会分派到它");
    expect(open).toContain(`data-he="${handlerEntryKey(cancelHandler, 0)}"`);
    expect(open).toContain(`data-he="${handlerEntryKey(cancelHandler, 1)}"`);
    expect(open).toContain("/api/orders/ship");
    expect(open).toContain("入口 2 ▾");
  });

  it("controllers list reuses the entry list look, HTTP only, grouped by owning type", () => {
    const entries = [create, cancel, cardTypes, { ...create, symbolId: 99, kind: "hosted", route: null, httpMethod: null, name: "Main", qualifiedName: "Program.Main" }];
    expect(controllerEntries(entries)).toHaveLength(3);
    expect(entryOwner(create)).toBe("Acme.Shop.Ordering.API.Apis.OrdersApi");
    expect(groupByOwner(controllerEntries(entries)).map((g) => [g.label, g.entries.length])).toEqual([["OrdersApi", 2], ["CardTypesApi", 1]]);
    const html = controllerListHtml(entries, "");
    expect(html).toContain("OrdersApi · 2");
    expect(html).toMatch(/<span class="method post">POST<\/span><span class="route">\/api\/orders<\/span>/);
    expect(html).toContain('data-ci="2"'); // 下标指回原 entries（cardTypes）
    expect(controllerListHtml(entries, "cardtypes")).not.toContain("OrdersApi · ");
    expect(controllerListHtml(null, "")).toContain("正在取入口");
    expect(controllerListHtml([], "")).toContain("没有发现入口");
    expect(panelTitle("controllers", fw, entries)).toBe("控制器入口 · 3");
    // 前端：入口占位叫"页面"，清单标题跟着叫
    const pages = { ...fw, flow: { ...fw.flow, root: { ...fw.flow.root, children: [{ ...fw.flow.root, kind: "controllers" as const, label: "页面", children: [] }] } } };
    expect(entriesLabel(pages)).toBe("页面");
    expect(panelTitle("controllers", pages, null)).toBe("页面");
  });
});

describe("entering the framework view", () => {
  const nodes = [container("Basket.API"), container("Ordering.API"), { ...container("Redis"), kind: "external" }];

  it("prefers the current / recent container, else the one with the most HTTP entries", () => {
    expect(pickFrameworkContainer(nodes, "Ordering.API", null)?.key).toBe("Ordering.API");
    expect(pickFrameworkContainer(nodes, null, new Map([["Basket.API", 3], ["Ordering.API", 12]]))?.key).toBe("Ordering.API");
    expect(pickFrameworkContainer(nodes, "gone", new Map([["Basket.API", 3]]))?.key).toBe("Basket.API");
    expect(pickFrameworkContainer(nodes, null, null)?.key).toBe("Basket.API");
    expect(pickFrameworkContainer(nodes, "Redis", null)?.key).toBe("Basket.API"); // 外部系统不算容器
    expect(pickFrameworkContainer([], null, null)).toBeNull();
  });
});

describe("request flow → framework flow jump", () => {
  it("the behaviour line becomes '经框架流程 ▸ 行为 ×N' and targets the same container", () => {
    const behaviors = ["LoggingBehavior", "ValidatorBehavior", "TransactionBehavior"];
    expect(frameworkJumpText(behaviors)).toBe("经框架流程 ▸ 行为 ×3");
    expect(frameworkJumpText([])).toBeNull();
    expect(frameworkJumpTitle(behaviors)).toContain("LoggingBehavior → ValidatorBehavior → TransactionBehavior");
    expect(frameworkJumpTarget({ containerKey: "Ordering.API" }, [container("Basket.API"), container("Ordering.API")])?.key).toBe("Ordering.API");
    expect(frameworkJumpTarget({ containerKey: "Missing" }, [container("Basket.API")])).toBeNull();
  });
});
