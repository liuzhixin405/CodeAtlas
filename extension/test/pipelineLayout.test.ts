import { describe, expect, it } from "vitest";
import { cellId, ColWidth, FirstRowY, HeaderY, layoutPipeline, RowHeight } from "../webview/pipelineLayout";
import type { PipelineDto, PipelineRowDto } from "../webview/types";

function row(key: string, over: Partial<PipelineRowDto> = {}): PipelineRowDto {
  return {
    key, label: key.slice(key.lastIndexOf(".") + 1), hasChildren: true, symbolId: null,
    entries: { count: 3, items: [{ name: "OrdersController", qualifiedName: "Acme.Api.OrdersController", symbolId: 2, weight: 3 }] },
    messages: { count: 2, commands: 1, queries: 1, events: 0, orphans: 0 },
    handlers: { count: 2, items: [] },
    nested: 0, bypass: 0, externalCalls: 1,
    downstream: [{ key: "Acme.Infra.Repos", label: "Repos", weight: 3 }, { key: "Acme.Shared", label: "Shared", weight: 1 }, { key: "Acme.X", label: "X", weight: 1 }],
    ...over,
  };
}

const dto: PipelineDto = {
  root: "Acme.App", defaultRoot: "Acme.App", leaf: false, behaviorsOrdered: false, testEntriesSkipped: 1, testMessagesSkipped: 0, messagesOutsideRoot: 0,
  behaviors: [
    { name: "AuditBehavior", qualifiedName: "Acme.App.Behaviors.AuditBehavior", symbolId: 42, order: 1 },
    { name: "ValidationBehavior", qualifiedName: "Acme.App.Behaviors.ValidationBehavior", symbolId: 41, order: 2 },
  ],
  rows: [
    row("Acme.App.Orders", { nested: 2, bypass: 2 }),
    row("Acme.App.Users", { messages: { count: 2, commands: 1, queries: 0, events: 1, orphans: 1 } }),
  ],
  flows: [
    { from: "Acme.App.Orders", to: "Acme.App.Users", kind: "nested", weight: 1 },
    { from: "Acme.App.Users", to: "Acme.App.Orders", kind: "bypass", weight: 1 },
  ],
};

describe("layoutPipeline", () => {
  it("places column headers on the top row and one cell per column per row", () => {
    const { nodes } = layoutPipeline(dto);
    expect(nodes).toHaveLength(5 + 2 * 6);
    const headers = nodes.filter((n) => n.data.kind === "header");
    expect(headers.map((h) => h.data.label).sort()).toEqual(["Handler", "下游", "入口", "消息", "行为链"].sort());
    expect(headers.every((h) => h.position.y === HeaderY)).toBe(true);
    const byId = new Map(nodes.map((n) => [n.data.id, n]));
    expect(byId.get(cellId("Acme.App.Orders", "entry"))!.position).toEqual({ x: ColWidth, y: FirstRowY });
    expect(byId.get(cellId("Acme.App.Users", "message"))!.position).toEqual({ x: 2 * ColWidth, y: FirstRowY + RowHeight });
    expect(byId.get(cellId("Acme.App.Users", "label"))!.position.x).toBe(0);
  });

  it("writes counts into labels and flags anomalies with warn", () => {
    const { nodes } = layoutPipeline(dto);
    const byId = new Map(nodes.map((n) => [n.data.id, n]));
    expect(byId.get(cellId("Acme.App.Orders", "entry"))!.data.label).toBe("入口 3\nOrdersController");
    expect(byId.get(cellId("Acme.App.Orders", "message"))!.data.label).toBe("消息 2\n命令 1 · 查询 1 · 事件 0");
    expect(byId.get(cellId("Acme.App.Orders", "message"))!.data.warn).toBe(false);
    const usersMsg = byId.get(cellId("Acme.App.Users", "message"))!;
    expect(usersMsg.data.label).toBe("消息 2\n命令 1 · 查询 0 · 事件 1\n孤儿 1");
    expect(usersMsg.data.warn).toBe(true);
    expect(usersMsg.classes).toBe("message warn");
    const handler = byId.get(cellId("Acme.App.Orders", "handler"))!;
    expect(handler.data.label).toBe("Handler 2\n嵌套分发 2\n绕过 2");
    expect(handler.data.warn).toBe(true);
    expect(byId.get(cellId("Acme.App.Users", "handler"))!.data.warn).toBe(false);
    const behaviors = byId.get(cellId("Acme.App.Orders", "behaviors"))!;
    expect(behaviors.data.label).toBe("行为链 ×2\nAuditBehavior … ValidationBehavior\n顺序未知");
    expect(behaviors.data.warn).toBe(true);
    expect(byId.get(cellId("Acme.App.Orders", "downstream"))!.data.label).toBe("下游 5\nRepos 3\nShared 1\n框架调用 1");
  });

  it("links the five pipeline cells of each row in order and draws flows between rows", () => {
    const { edges } = layoutPipeline(dto);
    const lanes = edges.filter((e) => e.data.kind === "lane");
    expect(lanes).toHaveLength(8);
    expect(lanes.some((e) => e.data.source === cellId("Acme.App.Orders", "entry") && e.data.target === cellId("Acme.App.Orders", "message"))).toBe(true);
    expect(lanes.some((e) => e.data.source === cellId("Acme.App.Orders", "handler") && e.data.target === cellId("Acme.App.Orders", "downstream"))).toBe(true);
    const nested = edges.find((e) => e.data.kind === "nested")!;
    expect([nested.data.source, nested.data.target, nested.classes, nested.data.label]).toEqual([cellId("Acme.App.Orders", "handler"), cellId("Acme.App.Users", "message"), "nested", "1"]);
    const bypass = edges.find((e) => e.data.kind === "bypass")!;
    expect([bypass.data.source, bypass.data.target, bypass.classes]).toEqual([cellId("Acme.App.Users", "entry"), cellId("Acme.App.Orders", "handler"), "bypass"]);
  });

  it("is deterministic: elements sorted by id regardless of input order", () => {
    const reversed: PipelineDto = { ...dto, rows: [...dto.rows].reverse(), flows: [...dto.flows].reverse() };
    const a = layoutPipeline(dto);
    const b = layoutPipeline(reversed);
    expect(a.nodes.map((n) => n.data.id)).toEqual(b.nodes.map((n) => n.data.id));
    expect(a.edges.map((e) => e.data.id)).toEqual(b.edges.map((e) => e.data.id));
    // 行的 y 由行序决定，倒序输入时两行互换位置
    expect(b.nodes.find((n) => n.data.id === cellId("Acme.App.Users", "entry"))!.position.y).toBe(FirstRowY);
  });

  it("leaf rows show the message name instead of category counts", () => {
    const leaf: PipelineDto = { ...dto, leaf: true, rows: [row("Acme.App.Orders.Commands.CreateOrderCommand", { hasChildren: false, symbolId: 11, messages: { count: 1, commands: 1, queries: 0, events: 0, orphans: 0 } })], flows: [] };
    const { nodes } = layoutPipeline(leaf);
    expect(nodes.find((n) => n.data.kind === "message")!.data.label).toBe("消息 1\nCreateOrderCommand");
  });
});
