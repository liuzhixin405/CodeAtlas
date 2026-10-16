import { describe, expect, it } from "vitest";
import { DefaultKinds, neighborKinds, TypeRelationKinds } from "../webview/api";
import { GraphState } from "../webview/graphState";
import { isConstruction, isConstructorSymbol, nodeLabel, qualifiedLabel, qualifiedSegments, rankNeighbor } from "../webview/neighborRank";
import { collapseImplementations } from "../webview/neighborRules";
import type { NeighborDto, SymbolDto } from "../webview/types";

function sym(id: number, qualifiedName: string, kind = "method", extra: Partial<SymbolDto> = {}): SymbolDto {
  const name = qualifiedSegments(qualifiedName).pop() ?? qualifiedName;
  return { id, kind, name, qualifiedName, signature: null, path: "src/Acme.Shop/App.cs", startLine: id, startCol: 1, endLine: id, endCol: 1, visibility: null, ...extra };
}
function edge(edgeId: number, other: SymbolDto, opts: Partial<NeighborDto> = {}): NeighborDto {
  return { edgeId, kind: "calls", direction: "out", confidence: 1, line: edgeId, ambiguousGroup: null, other, toName: other.qualifiedName, via: null, evidenceLine: null, pos: edgeId * 10, ...opts };
}

describe("默认边集", () => {
  it("默认只取调用与分发；类型关系、类型引用各自开关", () => {
    expect(DefaultKinds.split(",").sort()).toEqual(["calls", "dispatches"]);
    expect(neighborKinds()).toBe(DefaultKinds);
    expect(neighborKinds({ typeRelations: true }).split(",")).toEqual(expect.arrayContaining(TypeRelationKinds.split(",")));
    expect(neighborKinds({ references: true }).split(",")).toContain("references");
    expect(neighborKinds({ references: true })).not.toContain("implements");
  });
});

describe("nodeLabel：一律「类型.方法」", () => {
  it("方法带所属类型，参数表与泛型实参不算", () => {
    expect(nodeLabel(sym(1, "Acme.Shop.Orders.OrderService.PlaceOrder(int, string)"))).toBe("OrderService.PlaceOrder");
    expect(nodeLabel(sym(2, "Acme.Shop.Handlers.Handler<Acme.Shop.Cmd>.Handle(Cmd)"))).toBe("Handler.Handle");
  });
  it("脚本语言的「文件:类.方法」：路径不当类型；顶层函数就是名字", () => {
    expect(nodeLabel(sym(3, "src/shop-web/cart.ts:Cart.add"))).toBe("Cart.add");
    expect(nodeLabel(sym(4, "src/shop-web/api.ts:fetchOrders", "function"))).toBe("fetchOrders");
    expect(nodeLabel(sym(5, "src/shop-web/api.ts:fetchOrders", "method"))).toBe("fetchOrders");
  });
  it("构造函数只写类型名；类型就是名字；外部符号取最后两段", () => {
    expect(nodeLabel(sym(6, "Acme.Shop.Orders.Order.Order(int)"))).toBe("Order");
    expect(nodeLabel(sym(7, "Acme.Shop.Orders.OrderDto", "class"))).toBe("OrderDto");
    expect(nodeLabel(sym(8, "external:Microsoft.Extensions.Logging.ILogger.LogInformation(string)", "external"))).toBe("ILogger.LogInformation");
  });
  it("显式接口实现（名字本身带点）：类型.方法，不把接口的命名空间拼进来", () => {
    const s = { ...sym(9, "Acme.Shop.Events.IEventHandler<TEvent>.Acme.Shop.Events.IEventHandler.Handle(Event)"), name: "Acme.Shop.Events.IEventHandler.Handle" };
    expect(nodeLabel(s)).toBe("IEventHandler.Handle");
  });
  it("qualifiedLabel：只有限定名时取最后两段", () => {
    expect(qualifiedLabel("Acme.Shop.Orders.IOrderRepository.Add(Order)")).toBe("IOrderRepository.Add");
  });
});

describe("isConstruction", () => {
  it("目标是类 / 结构 / 记录，或构造函数，或 via 标着 new：算构造", () => {
    expect(isConstruction(edge(1, sym(1, "Acme.Shop.OrderDto", "class")))).toBe(true);
    expect(isConstruction(edge(2, sym(2, "Acme.Shop.Money", "struct")))).toBe(true);
    expect(isConstruction(edge(3, sym(3, "Acme.Shop.Order.Order(int)")))).toBe(true);
    expect(isConstruction(edge(4, sym(4, "src/shop-web/cart.ts:Cart.constructor")))).toBe(true);
    expect(isConstruction(edge(5, sym(5, "Acme.Shop.OrderService.Place()"), { via: "new" }))).toBe(true);
    expect(isConstruction(edge(6, sym(6, "Acme.Shop.OrderService.Place()")))).toBe(false);
    expect(isConstructorSymbol(sym(7, "Acme.Shop.Order.Total()"))).toBe(false);
  });
});

describe("rankNeighbor", () => {
  const service = sym(10, "Acme.Shop.Orders.OrderService.PlaceOrder(Order)", "method", { callers: 26 });
  it("不再因为被调用多而扣分：被 26 处调用的服务方法和只被调 1 次的一样", () => {
    const once = { ...service, id: 11, callers: 1 };
    expect(rankNeighbor(edge(1, service))).toBe(rankNeighbor(edge(1, once)));
  });
  it("接口调用换成唯一实现（via=impl 0.9）后与直接调用同分", () => {
    expect(rankNeighbor(edge(1, service, { via: "impl", confidence: 0.9 }))).toBe(rankNeighbor(edge(2, service)));
    expect(rankNeighbor(edge(1, service, { via: "impl", confidence: 0.5 }))).toBeLessThan(rankNeighbor(edge(2, service)));
  });
  it("分发 > 普通调用 > 构造 > 类型关系", () => {
    const dispatch = rankNeighbor(edge(1, sym(20, "Acme.Shop.PlaceOrderHandler.Handle(PlaceOrder)"), { kind: "dispatches", via: "dispatch:PlaceOrder" }));
    const call = rankNeighbor(edge(2, service, { confidence: 0.5 }));
    const ctor = rankNeighbor(edge(3, sym(21, "Acme.Shop.Orders.OrderDto", "class")), { focusModule: "Acme.Shop" });
    const types = rankNeighbor(edge(4, sym(22, "Acme.Shop.IHandler.Handle()"), { kind: "implements" }), { focusModule: "Acme.Shop" });
    expect(dispatch).toBeGreaterThan(call);
    // 即使构造的是同模块的类、普通调用置信度很低，构造也排在后面
    expect(call).toBeGreaterThan(ctor);
    expect(ctor).toBeGreaterThan(types);
  });
  it("测试代码、校验 / 日志类名字降权；同模块略加分", () => {
    const base = rankNeighbor(edge(1, service));
    expect(rankNeighbor(edge(2, sym(30, "Acme.Shop.OrderService.ValidateOrder()")))).toBeLessThan(base);
    expect(rankNeighbor(edge(3, sym(31, "Acme.Shop.Diagnostics.OrderLogger.Write()")))).toBeLessThan(base);
    expect(rankNeighbor(edge(4, sym(32, "Acme.Shop.Tests.OrderTests.Place()", "method", { path: "tests/Acme.Shop.Tests/OrderTests.cs" })))).toBeLessThan(base - 100);
    expect(rankNeighbor(edge(5, service), { focusModule: "Acme.Shop" })).toBeGreaterThan(base);
  });
});

/**
 * 复刻用户的场景：控制器 → 分派到 Handler；Handler.Handle 里
 * 1 个被 26 处调用的服务方法（经接口，唯一实现）+ 2 个 new DTO + 1 条 implements（Handler 实现 IRequestHandler.Handle）+ 1 个嵌套分派。
 * 老规则（被调用多扣 25 分、每种边各 2 个名额、implements 默认取）第一屏看到的是 DTO 和接口；新规则应该是服务方法与分派。
 */
describe("场景：控制器 → Handler → 服务方法 / DTO / 接口", () => {
  const controller = sym(1, "Acme.Shop.Api.OrdersController.Create(CreateOrderRequest)");
  const handler = sym(2, "Acme.Shop.Application.CreateOrderHandler.Handle(CreateOrderCommand, CancellationToken)");
  const iservice = sym(3, "Acme.Shop.Application.IOrderService.PlaceOrder(Order)", "method", { callers: 26 });
  const service = sym(4, "Acme.Shop.Infrastructure.OrderService.PlaceOrder(Order)", "method", { callers: 26 });
  const dto1 = sym(5, "Acme.Shop.Application.Dtos.OrderDto", "class");
  const dto2 = sym(6, "Acme.Shop.Application.Dtos.AddressDto", "class");
  const ihandler = sym(7, "external:MediatR.IRequestHandler<TRequest, TResponse>.Handle(TRequest, CancellationToken)", "external");
  const startedHandler = sym(8, "Acme.Shop.Application.OrderStartedHandler.Handle(OrderStarted, CancellationToken)");

  const neighbors: NeighborDto[] = [
    edge(100, controller, { direction: "in", kind: "dispatches", via: "dispatch:CreateOrderCommand", line: 40 }),
    edge(101, ihandler, { kind: "implements", line: 29, pos: null }),
    edge(102, dto1, { line: 32 }),
    edge(103, iservice, { line: 33, pos: 1700 }),
    edge(104, service, { line: 33, pos: 1700, via: "impl", confidence: 0.9 }),
    edge(105, dto2, { line: 39 }),
    edge(106, startedHandler, { kind: "dispatches", via: "dispatch:OrderStarted", line: 45 }),
  ];

  it("默认两分支：服务方法（接口已合成到唯一实现）与分派，不是 DTO / 接口；调用者收进胶囊", () => {
    const s = new GraphState();
    const ctx = { focusModule: "Acme.Shop" };
    // 页面的默认边集不取 implements；就算取了（开了「类型关系」），它也不占调用链的名额
    const data = collapseImplementations(neighbors);
    s.setFocus(handler, data, { limit: 2, rank: (n) => rankNeighbor(n, ctx), callersCapsule: true });

    const shown = s.toElements().nodes.filter((n) => n.type === "symbol" && n.id !== "sym:2" && n.kind !== "external");
    expect(shown.map((n) => n.label).sort()).toEqual(["OrderService.PlaceOrder", "OrderStartedHandler.Handle"]);
    expect(s.getNode("sym:3")).toBeUndefined(); // 接口方法没有单独的节点
    expect(s.getNode("sym:5")).toBeUndefined();
    expect(s.getNode("sym:6")).toBeUndefined();
    const overflow = s.toElements().nodes.find((n) => n.overflow);
    expect(s.getCapsuleMembers(overflow!.id).map((m) => m.other!.name).sort()).toEqual(["AddressDto", "OrderDto"]);
    expect(s.callersCapsule("sym:2")?.label).toBe("被 1 处调用");
    expect(s.toElements().edges.find((e) => e.id === "edge:104")?.viaInterface).toBe(iservice.qualifiedName);
  });

  it("对照：老规则（被调用多扣分）下服务方法输给 DTO 构造", () => {
    const oldRank = (n: NeighborDto) => {
      let v = (n.kind === "dispatches" ? 100 : n.kind === "calls" ? 30 : 10) + n.confidence * 20;
      const c = n.other?.callers ?? 0;
      if (c > 40) v -= 60; else if (c > 15) v -= 25;
      return v + 15;
    };
    expect(oldRank(edge(1, service, { confidence: 0.9 }))).toBeLessThan(oldRank(edge(2, dto1)));
    expect(rankNeighbor(edge(1, service, { confidence: 0.9 }))).toBeGreaterThan(rankNeighbor(edge(2, dto1), { focusModule: "Acme.Shop" }));
  });
});
