import { describe, expect, it } from "vitest";
import { DefaultCollapseDepth, isExpanded, layoutFlow, toggle } from "../webview/flowLayout";
import type { FlowDto, FlowNodeDto } from "../webview/types";

function node(partial: Partial<FlowNodeDto> & { label: string; kind: FlowNodeDto["kind"]; actor: string; depth: number }): FlowNodeDto {
  return { symbolId: 1, qualifiedName: partial.label, via: null, confidence: 1, count: 1, children: [], hidden: 0, note: null, ...partial };
}

/**
 * api(入口) → 分发 GetOrdersQuery（同列）→ Handler（app 列）→ 仓储（infra，×3，深度 3，孩子默认折起）
 *                                                            → 外部库组
 *            → 跨容器 hop 到 payment（带 note）→ 其 Handler；再回调 api（向左）
 *            → 还有 4 个
 */
const dto: FlowDto = {
  entrySymbolId: 1, entryLabel: "GET /api/orders", route: "/api/orders", containerKey: "Acme.Api",
  actors: [
    { key: "api", label: "Acme.Api", kind: "container" },
    { key: "app", label: "Acme.App", kind: "project" },
    { key: "infra", label: "Acme.Infra", kind: "project" },
    { key: "payment", label: "Payment", kind: "container" },
    { key: "ext", label: "外部库", kind: "external" },
  ],
  root: node({
    label: "OrdersController.Get", kind: "entry", actor: "api", depth: 0,
    children: [
      node({
        label: "GetOrdersQuery", kind: "dispatch", actor: "api", depth: 1, via: "dispatch",
        children: [
          node({
            label: "GetOrdersHandler.Handle", kind: "call", actor: "app", depth: 2,
            children: [
              node({
                label: "OrderRepository.List", kind: "call", actor: "infra", depth: 3, count: 3,
                children: [node({ label: "Db.Query", kind: "call", actor: "infra", depth: 4 })],
              }),
              node({ label: "EF Core 等 2 个", kind: "external-group", actor: "ext", depth: 3, symbolId: null }),
            ],
          }),
        ],
      }),
      node({
        label: "Payment.Charge", kind: "hop", actor: "payment", depth: 1, via: "http", note: "按路由对上（0.9）", confidence: 0.9,
        children: [
          node({ label: "ChargeHandler.Handle", kind: "call", actor: "payment", depth: 2 }),
          node({ label: "OrdersCallback.Post", kind: "call", actor: "api", depth: 2 }),
        ],
      }),
      node({ label: "还有 4 个", kind: "more", actor: "api", depth: 1, symbolId: null, count: 4 }),
    ],
  }),
  behaviors: ["Logging", "Validation"], nodes: 10, truncated: false, note: null,
};

describe("flow layout", () => {
  it("keeps the actor order as columns", () => {
    const l = layoutFlow(dto, new Set());
    expect(l.columns.map((c) => c.key)).toEqual(["api", "app", "infra", "payment", "ext"]);
  });

  it("walks visible nodes in pre-order with child-index ids and hides children at the default collapse depth", () => {
    const l = layoutFlow(dto, new Set());
    expect(DefaultCollapseDepth).toBe(3);
    expect(l.rows.map((r) => r.id)).toEqual(["0", "0.0", "0.0.0", "0.0.0.0", "0.0.0.1", "0.1", "0.1.0", "0.1.1", "0.2"]);
    const byId = new Map(l.rows.map((r) => [r.id, r]));
    const repo = byId.get("0.0.0.0")!;
    expect(repo.hasChildren).toBe(true);
    expect(repo.collapsed).toBe(true); // 深度 3，默认折起，Db.Query 不在行里
    expect(byId.has("0.0.0.0.0")).toBe(false);
    expect(byId.get("0.0.0")!.collapsed).toBe(false);
    expect(byId.get("0.0.0.1")!.hasChildren).toBe(false);
    expect(byId.get("0.0.0.1")!.collapsed).toBe(false);
  });

  it("computes from/to columns and directions; root points at its own column", () => {
    const l = layoutFlow(dto, new Set());
    const cols = l.rows.map((r) => [r.id, r.fromCol, r.toCol, r.direction]);
    expect(cols).toEqual([
      ["0", 0, 0, "self"],
      ["0.0", 0, 0, "self"],      // 分发：同一个容器里
      ["0.0.0", 0, 1, "right"],   // api → app
      ["0.0.0.0", 1, 2, "right"], // app → infra
      ["0.0.0.1", 1, 4, "right"], // app → 外部
      ["0.1", 0, 3, "right"],     // hop 到 payment
      ["0.1.0", 3, 3, "self"],
      ["0.1.1", 3, 0, "left"],    // 回调 api
      ["0.2", 0, 0, "self"],
    ]);
  });

  it("suffixes the label with ×N only when the node repeats", () => {
    const l = layoutFlow(dto, new Set());
    const byId = new Map(l.rows.map((r) => [r.id, r]));
    expect(byId.get("0.0.0.0")!.label).toBe("OrderRepository.List ×3");
    expect(byId.get("0.0.0")!.label).toBe("GetOrdersHandler.Handle");
    expect(byId.get("0.1")!.node.note).toBe("按路由对上（0.9）");
  });

  it("toggling a collapsed id reveals its children, toggling an expanded id hides them", () => {
    const opened = toggle(new Set(), "0.0.0.0");
    const l1 = layoutFlow(dto, opened);
    const ids1 = l1.rows.map((r) => r.id);
    expect(ids1.indexOf("0.0.0.0.0")).toBe(ids1.indexOf("0.0.0.0") + 1);
    const db = l1.rows.find((r) => r.id === "0.0.0.0.0")!;
    expect([db.fromCol, db.toCol, db.direction]).toEqual([2, 2, "self"]);
    expect(l1.rows.find((r) => r.id === "0.0.0.0")!.collapsed).toBe(false);

    const closed = toggle(new Set(), "0.0");
    const l2 = layoutFlow(dto, closed);
    expect(l2.rows.map((r) => r.id)).toEqual(["0", "0.0", "0.1", "0.1.0", "0.1.1", "0.2"]);
    expect(l2.rows[1]!.collapsed).toBe(true);
  });

  it("toggle returns a new set and twice is a no-op", () => {
    const base = new Set<string>();
    const once = toggle(base, "0.1");
    expect(base.size).toBe(0);
    expect(once.has("0.1")).toBe(true);
    expect(toggle(once, "0.1").has("0.1")).toBe(false);
    expect(isExpanded(dto.root.children[1]!, "0.1", once)).toBe(false);
    expect(isExpanded(dto.root.children[1]!, "0.1", base)).toBe(true);
  });

  it("falls back to the parent column when an actor key is unknown", () => {
    const odd: FlowDto = { ...dto, root: node({ label: "x", kind: "entry", actor: "api", depth: 0, children: [node({ label: "y", kind: "call", actor: "nope", depth: 1 })] }) };
    const l = layoutFlow(odd, new Set());
    expect([l.rows[1]!.fromCol, l.rows[1]!.toCol, l.rows[1]!.direction]).toEqual([0, 0, "self"]);
  });
});
