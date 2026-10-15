import { describe, expect, it } from "vitest";
import { componentElementId, externalComponentKeys, isReverseDependency, kindsSummary, layoutRelations, orderedComponents, primaryRelation, RelColumnGap, RelationKindLabels, RelationKindOrder, relationToCenter, RelRowHeight, RelSubColumnGap, RelWrapAt, reverseNote, stripGenerics, TierWidths, UnitKindLabels, unitKindLabel, unitLabel, UnitWidth, weightTier, componentTitleId, componentTitlePosition } from "../webview/relationsLayout";
import type { RelationComponentDto, RelationEdgeDto, RelationKind, RelationNodeDto, RelationsDto, SymbolDto } from "../webview/types";

function sym(id: number, name: string, kind = "class", over: Partial<SymbolDto> = {}): SymbolDto {
  return {
    id, kind, name, qualifiedName: `Acme.${name}`, signature: null, path: `src/${name}.cs`,
    startLine: 1, startCol: 1, endLine: 10, endCol: 1, visibility: "public", ...over,
  };
}

function node(unit: SymbolDto, componentKey: string, distance: number, external = false): RelationNodeDto {
  return { unit, componentKey, distance, external };
}

function edge(from: number, to: number, kinds: Partial<Record<RelationKind, number>>, weight: number): RelationEdgeDto {
  return { from, to, kinds, weight, samples: [] };
}

const comp = (key: string, order: number): RelationComponentDto => ({ key, label: key.replace(/^Acme\./, ""), order, containerKey: "Acme.Api" });

// 组件依赖序：Domain(0) ← Application(1) ← Infrastructure(2) ← Api(3)；外部组件 Order 故意给 0，也必须排最右
const OrderService = sym(1, "OrderService");
const IOrderRepository = sym(2, "IOrderRepository", "interface");
const Order = sym(3, "Order");
const AggregateBase = sym(4, "AggregateBase", "class", { signature: "public abstract class AggregateBase" });
const SqlOrderRepository = sym(5, "SqlOrderRepository");
const OrdersController = sym(6, "OrdersController");
const ILogger = sym(7, "ILogger<TCategory>", "external", { qualifiedName: "Microsoft.Extensions.Logging.ILogger<TCategory>", path: "" });
const AuditLog = sym(8, "AuditLog");
const BasketController = sym(9, "BasketController");
const AdminController = sym(10, "AdminController");

const dto: RelationsDto = {
  focus: OrderService,
  components: [comp("external", 0), comp("Acme.Api", 3), comp("Acme.Domain", 0), comp("Acme.Infrastructure", 2), comp("Acme.Application", 1), comp("Acme.Unused", 4)],
  nodes: [
    node(OrderService, "Acme.Application", 0),
    node(AggregateBase, "Acme.Domain", 2),
    node(AuditLog, "Acme.Domain", 1),
    node(Order, "Acme.Domain", 1),
    node(IOrderRepository, "Acme.Domain", 1),
    node(SqlOrderRepository, "Acme.Infrastructure", 1),
    node(BasketController, "Acme.Api", 2),
    node(OrdersController, "Acme.Api", 1),
    node(AdminController, "Acme.Api", 2),
    node(ILogger, "external", 1, true),
  ],
  edges: [
    edge(1, 2, { call: 12, reference: 3 }, 13.5),
    edge(1, 3, { new: 2, reference: 4 }, 4),
    edge(3, 4, { inherits: 1 }, 5),
    edge(5, 2, { implements: 1 }, 5),
    edge(6, 1, { call: 3, dispatch: 1 }, 4),
    edge(1, 7, { reference: 1 }, 0.5),
    edge(8, 1, { dispatch: 2 }, 2),
    edge(9, 6, { reference: 2 }, 1),
    edge(10, 6, { reference: 2 }, 1),
  ],
  truncated: false,
};

function shuffled<T>(list: T[]): T[] {
  return [...list].reverse();
}

describe("relationsLayout · 列序与列内排序", () => {
  it("orders component columns by Order, external last, unused components dropped", () => {
    expect(orderedComponents(dto).map((c) => c.key)).toEqual(["Acme.Domain", "Acme.Application", "Acme.Infrastructure", "Acme.Api", "external"]);
    expect([...externalComponentKeys(dto)]).toEqual(["external"]);
    const { columns } = layoutRelations(dto);
    expect(columns.map((c) => c.componentKey)).toEqual(["Acme.Domain", "Acme.Application", "Acme.Infrastructure", "Acme.Api", "external"]);
  });

  it("sorts units in a column by distance, then weight to the centre, then name", () => {
    const { columns } = layoutRelations(dto);
    const domain = columns.find((c) => c.componentKey === "Acme.Domain")!.unitIds;
    // 距离 1：IOrderRepository（与中心 13.5）> Order（4）> AuditLog（2）；距离 2：AggregateBase
    expect(domain).toEqual([2, 3, 8, 4]);
    const api = columns.find((c) => c.componentKey === "Acme.Api")!.unitIds;
    // OrdersController 距离 1 在前；两个距离 2、与中心无边的按名字：Admin < Basket
    expect(api).toEqual([6, 10, 9]);
  });

  it("places columns left to right and centres shorter columns vertically", () => {
    const { units } = layoutRelations(dto);
    const pos = (id: number) => units.find((u) => u.data.unitId === id)!.position;
    const step = UnitWidth + RelColumnGap;
    expect(pos(2).x).toBe(0);
    expect(pos(1).x).toBe(step);
    expect(pos(5).x).toBe(2 * step);
    expect(pos(6).x).toBe(3 * step);
    expect(pos(7).x).toBe(4 * step);
    // 最高的列是 Domain（4 个）；Application 只有中心一个 → 下移 (4-1)/2 行
    expect([pos(2).y, pos(3).y, pos(8).y, pos(4).y]).toEqual([0, RelRowHeight, 2 * RelRowHeight, 3 * RelRowHeight]);
    expect(pos(1).y).toBe((3 * RelRowHeight) / 2);
  });

  it("puts units inside their component box and marks only the centre", () => {
    const { units, components } = layoutRelations(dto);
    const center = units.filter((u) => u.classes.split(" ").includes("center"));
    expect(center.map((u) => u.data.unitId)).toEqual([1]);
    expect(units.find((u) => u.data.unitId === 1)!.data.parent).toBe(componentElementId("Acme.Application"));
    expect(components.find((c) => c.data.componentKey === "Acme.Application")!.classes).toContain("has-center");
    expect(components.find((c) => c.data.componentKey === "external")!.classes).toContain("external");
    expect(units.find((u) => u.data.unitId === 7)!.classes).toContain("external");
    expect(units.find((u) => u.data.unitId === 4)!.classes).toContain("d2");
  });

  it("is deterministic: shuffled input gives identical output", () => {
    const a = layoutRelations(dto);
    const b = layoutRelations({ ...dto, components: shuffled(dto.components), nodes: shuffled(dto.nodes), edges: shuffled(dto.edges) });
    expect(JSON.stringify(b)).toBe(JSON.stringify(a));
    expect(JSON.stringify(layoutRelations(dto))).toBe(JSON.stringify(a));
  });

  it("wraps a long component into sub-columns and shifts the next column right", () => {
    const many = Array.from({ length: RelWrapAt + 1 }, (_, i) => node(sym(100 + i, `Unit${String(i).padStart(2, "0")}`), "Acme.Domain", 1));
    const d: RelationsDto = { ...dto, nodes: [node(OrderService, "Acme.Application", 0), ...many], edges: [] };
    const { units, columns } = layoutRelations(d);
    expect(columns[0]!.unitIds).toHaveLength(RelWrapAt + 1);
    const xs = new Set(units.filter((u) => u.data.componentKey === "Acme.Domain").map((u) => u.position.x));
    expect([...xs].sort((p, q) => p - q)).toEqual([0, UnitWidth + RelSubColumnGap]);
    expect(units.find((u) => u.data.unitId === 1)!.position.x).toBe(2 * UnitWidth + RelSubColumnGap + RelColumnGap);
  });

  it("adds a component for node keys missing from components, and drops dangling / self edges", () => {
    const d: RelationsDto = {
      ...dto,
      nodes: [...dto.nodes, node(sym(50, "Stray"), "Acme.Mystery", 1)],
      edges: [...dto.edges, edge(1, 999, { call: 1 }, 1), edge(1, 1, { call: 4 }, 4), edge(1, 50, { call: 1 }, 1)],
    };
    const { columns, edges } = layoutRelations(d);
    // 补出来的组件 Order = 现有最大 + 1，排在 Unused(4) 之后、外部之前
    expect(columns.map((c) => c.componentKey)).toEqual(["Acme.Domain", "Acme.Application", "Acme.Infrastructure", "Acme.Api", "Acme.Mystery", "external"]);
    expect(edges.some((e) => e.data.id.includes("999"))).toBe(false);
    expect(edges.some((e) => e.data.source === e.data.target)).toBe(false);
    expect(edges.some((e) => e.data.id === "e:1->50")).toBe(true);
  });
});

describe("relationsLayout · 边样式", () => {
  it("picks the primary relation: inherits / implements > call / new > dispatch > reference", () => {
    expect(primaryRelation({ call: 9, inherits: 1, reference: 3 })).toBe("inherits");
    expect(primaryRelation({ implements: 1, call: 2 })).toBe("implements");
    expect(primaryRelation({ inherits: 1, implements: 1 })).toBe("inherits");
    expect(primaryRelation({ new: 1, dispatch: 5 })).toBe("call");
    expect(primaryRelation({ call: 1, reference: 9 })).toBe("call");
    expect(primaryRelation({ dispatch: 2, reference: 9 })).toBe("dispatch");
    expect(primaryRelation({ reference: 4 })).toBe("reference");
    expect(primaryRelation({ call: 0, reference: 1 })).toBe("reference");
    expect(primaryRelation({})).toBe("reference");
  });

  it("buckets weight into three line widths", () => {
    expect([0, 2.99, 3, 9.99, 10, 120].map(weightTier)).toEqual([1, 1, 2, 2, 3, 3]);
    expect(TierWidths[1]).toBeLessThan(TierWidths[2]);
    expect(TierWidths[2]).toBeLessThan(TierWidths[3]);
  });

  it("flags reverse dependencies: from a lower-Order component to a higher one, never inside one component or to externals", () => {
    const byPair = (f: number, t: number) => dto.edges.find((e) => e.from === f && e.to === t)!;
    expect(isReverseDependency(byPair(8, 1), dto)).toBe(true);   // Domain(0) → Application(1)
    expect(isReverseDependency(byPair(1, 2), dto)).toBe(false);  // Application(1) → Domain(0)：正常
    expect(isReverseDependency(byPair(3, 4), dto)).toBe(false);  // 同组件
    expect(isReverseDependency(byPair(1, 7), dto)).toBe(false);  // 到外部
    expect(reverseNote(byPair(8, 1), dto)).toBe("反向依赖：Domain 组件依赖了 Application 组件");
    expect(reverseNote(byPair(1, 2), dto)).toBeNull();
  });

  it("carries primary / tier / reverse / near as classes", () => {
    const { edges } = layoutRelations(dto);
    const cls = (id: string) => edges.find((e) => e.data.id === id)!.classes.split(" ");
    expect(cls("e:1->2")).toEqual(["call", "w3", "near"]);
    expect(cls("e:1->3")).toEqual(["call", "w2", "near"]);
    expect(cls("e:3->4")).toEqual(["structural", "w2", "intra"]);
    expect(cls("e:5->2")).toEqual(["structural", "w2"]);
    expect(cls("e:6->1")).toEqual(["call", "w2", "near"]);
    expect(cls("e:1->7")).toEqual(["reference", "w1", "near"]);
    expect(cls("e:8->1")).toEqual(["dispatch", "w1", "reverse", "near"]);
    expect(edges.find((e) => e.data.id === "e:5->2")!.data.primary).toBe("implements");
    expect(edges.map((e) => e.data.id)).toEqual([...edges.map((e) => e.data.id)].sort());
  });
});

describe("relationsLayout · 中文汇总与标签", () => {
  it("summarises kinds in a fixed order, structural ones without counts", () => {
    expect(kindsSummary({ implements: 1, new: 2, call: 12 })).toBe("调用 12 · 构造 2 · 实现");
    expect(kindsSummary({ reference: 3, inherits: 1, dispatch: 4 })).toBe("分派 4 · 继承 · 引用 3");
    expect(kindsSummary({ call: 0, reference: 2 })).toBe("引用 2");
    expect(kindsSummary({})).toBe("");
    // 页面还不认识的种类原样附在后面
    expect(kindsSummary({ call: 1, ...({ awaits: 2 } as Partial<Record<RelationKind, number>>) })).toBe("调用 1 · awaits 2");
  });

  it("has a Chinese label for every RelationKind and the summary order covers them all", () => {
    // Record<RelationKind, true> 由编译器保证列全；新增种类时这里和标签表都要补
    const all: Record<RelationKind, true> = { call: true, new: true, dispatch: true, inherits: true, implements: true, reference: true };
    const kinds = Object.keys(all) as RelationKind[];
    for (const k of kinds) expect(RelationKindLabels[k], k).toMatch(/\S/);
    expect(Object.keys(RelationKindLabels).sort()).toEqual([...kinds].sort());
    expect([...RelationKindOrder].sort()).toEqual([...kinds].sort());
    expect(new Set(Object.values(RelationKindLabels)).size).toBe(kinds.length);
  });

  it("labels every unit kind the server can return", () => {
    for (const k of ["class", "abstract", "interface", "struct", "enum", "module", "type_alias", "function", "external"]) expect(UnitKindLabels[k], k).toMatch(/\S/);
    expect(unitKindLabel(AggregateBase)).toBe("抽象类");
    expect(unitKindLabel(IOrderRepository)).toBe("接口");
    expect(unitKindLabel(sym(1, "X", "weird_kind"))).toBe("weird_kind");
  });

  it("uses short names: generics stripped, interface marked, modules by file name", () => {
    expect(stripGenerics("Repository<T>")).toBe("Repository");
    expect(stripGenerics("Dictionary<string, List<int>>")).toBe("Dictionary");
    expect(stripGenerics("List`1")).toBe("List");
    expect(stripGenerics("Acme.Ordering.OrderService")).toBe("OrderService");
    expect(stripGenerics("std::vector<int>")).toBe("vector");
    expect(unitLabel(ILogger)).toBe("ILogger");
    expect(unitLabel(IOrderRepository)).toBe("◇ IOrderRepository");
    expect(unitLabel(sym(1, "orders", "module", { path: "web/src/api/orders.ts" }))).toBe("orders.ts");
    expect(unitLabel(sym(1, "index", "module", { path: "web\\src\\index.tsx" }))).toBe("index.tsx");
    const { units } = layoutRelations(dto);
    expect(units.find((u) => u.data.unitId === 4)!.classes.split(" ")).toEqual(expect.arrayContaining(["abstract", "italic"]));
    expect(units.find((u) => u.data.unitId === 2)!.classes.split(" ")).toContain("italic");
    expect(units.find((u) => u.data.unitId === 3)!.classes.split(" ")).not.toContain("italic");
  });

  it("describes a unit's relation to the centre", () => {
    expect(relationToCenter(dto, 1)).toBe("这是中心");
    expect(relationToCenter(dto, 2)).toBe("中心依赖它：调用 12 · 引用 3");
    expect(relationToCenter(dto, 6)).toBe("它依赖中心：调用 3 · 分派 1");
    expect(relationToCenter(dto, 4)).toBe("与中心没有直接关系（隔一层）");
    const both: RelationsDto = { ...dto, edges: [...dto.edges, edge(2, 1, { reference: 1 }, 0.5)] };
    expect(relationToCenter(both, 2)).toBe("中心依赖它：调用 12 · 引用 3；它依赖中心：引用 1");
  });
});

describe("componentTitlePosition", () => {
  it("puts the component title on the middle of the box's top edge (drawn as its own node, above edge labels)", () => {
    expect(componentTitlePosition({ x1: 100, y1: 40, w: 300 })).toEqual({ x: 250, y: 40 });
    expect(componentTitleId("c:src/Ordering.API")).toBe("c:src/Ordering.API::title");
  });
});
