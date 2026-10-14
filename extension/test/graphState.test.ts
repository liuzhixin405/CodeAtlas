import { describe, expect, it } from "vitest";
import { GraphState } from "../webview/graphState";
import type { NeighborDto, SymbolDto } from "../webview/types";

function sym(id: number, name: string, kind = "method", path = "src/a.ts"): SymbolDto {
  return { id, kind, name, qualifiedName: `${path}:${name}`, signature: null, path, startLine: id, startCol: 1, endLine: id + 1, endCol: 1, visibility: null };
}

function edge(edgeId: number, direction: "in" | "out", other: SymbolDto | null, opts: Partial<NeighborDto> = {}): NeighborDto {
  return { edgeId, kind: "calls", direction, confidence: 0.7, line: 10, ambiguousGroup: null, other, toName: other?.qualifiedName ?? "external:x", via: null, evidenceLine: null, ...opts };
}

const focus = sym(1, "focus");
const callee = sym(2, "callee");
const caller = sym(3, "caller");

describe("GraphState", () => {
  it("setFocus builds focus node plus one layer, out edges go focus→other and in edges other→focus", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee), edge(11, "in", caller)]);

    const el = s.toElements();
    expect(el.nodes.map((n) => n.id).sort()).toEqual(["sym:1", "sym:2", "sym:3"]);
    expect(el.edges).toContainEqual(expect.objectContaining({ source: "sym:1", target: "sym:2", confidence: 0.7 }));
    expect(el.edges).toContainEqual(expect.objectContaining({ source: "sym:3", target: "sym:1" }));
    expect(s.focusNodeId).toBe("sym:1");
    expect(s.getNode("sym:1")?.expanded).toBe(true);
    expect(s.getNode("sym:2")?.expanded).toBe(false);
  });

  it("expand marks node expanded and never duplicates nodes reachable both ways", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    // callee 反过来也调 focus（互相递归）
    s.expand("sym:2", [edge(20, "out", focus), edge(10, "in", focus)]);

    const el = s.toElements();
    expect(el.nodes).toHaveLength(2);
    expect(el.edges.map((e) => e.id).sort()).toEqual(["edge:10", "edge:20"]);
    expect(s.getNode("sym:2")?.expanded).toBe(true);
  });

  it("folds ambiguousGroup edges into one capsule node with members", () => {
    const s = new GraphState();
    const candidates = [sym(5, "Ok", "method", "t1.cs"), sym(6, "Ok", "method", "t2.cs"), sym(7, "Ok", "method", "t3.cs")];
    s.setFocus(focus, candidates.map((c, i) => edge(30 + i, "out", c, { confidence: 0.3, ambiguousGroup: "calls:86", line: 86 })));

    const el = s.toElements();
    const capsule = el.nodes.find((n) => n.type === "ambiguous");
    expect(capsule).toBeDefined();
    expect(capsule!.label).toBe("3 个可能目标");
    expect(el.nodes.filter((n) => n.type === "symbol")).toHaveLength(1);
    expect(el.edges).toHaveLength(1);
    expect(el.edges[0]).toMatchObject({ source: "sym:1", target: capsule!.id, dashed: true });
    expect(s.getCapsuleMembers(capsule!.id)).toHaveLength(3);
  });

  it("incoming ambiguous edges are grouped per kind regardless of caller line (they are ambiguous callers)", () => {
    const s = new GraphState();
    const callers = [sym(5, "A", "method", "a.cs"), sym(6, "B", "method", "b.cs"), sym(7, "C", "method", "c.cs")];
    s.setFocus(focus, callers.map((c, i) => edge(40 + i, "in", c, { confidence: 0.3, ambiguousGroup: `calls:${100 + i}`, line: 100 + i })));

    const el = s.toElements();
    const capsules = el.nodes.filter((n) => n.type === "ambiguous");
    expect(capsules).toHaveLength(1);
    expect(capsules[0]!.label).toBe("3 个可能调用者");
    expect(el.edges[0]).toMatchObject({ source: capsules[0]!.id, target: "sym:1", dashed: true });
  });

  it("expandAmbiguous materializes chosen members and removes empty capsule", () => {
    const s = new GraphState();
    const a = sym(5, "Ok", "method", "t1.cs");
    const b = sym(6, "Ok", "method", "t2.cs");
    s.setFocus(focus, [edge(30, "out", a, { confidence: 0.3, ambiguousGroup: "calls:86" }), edge(31, "out", b, { confidence: 0.3, ambiguousGroup: "calls:86" })]);
    const capsuleId = s.toElements().nodes.find((n) => n.type === "ambiguous")!.id;

    s.expandAmbiguous(capsuleId, [30]);
    let el = s.toElements();
    expect(el.nodes.find((n) => n.id === "sym:5")).toBeDefined();
    expect(el.nodes.find((n) => n.id === capsuleId)!.label).toBe("1 个可能目标");
    expect(el.edges).toContainEqual(expect.objectContaining({ id: "edge:30", source: "sym:1", target: "sym:5", confidence: 0.3 }));

    s.expandAmbiguous(capsuleId, [31]);
    el = s.toElements();
    expect(el.nodes.find((n) => n.id === capsuleId)).toBeUndefined();
    expect(el.nodes.filter((n) => n.type === "symbol")).toHaveLength(3);
  });

  it("dangling edge (other = null) becomes a placeholder node keyed by toName", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(40, "out", null, { toName: "src/gone.ts:vanished" }), edge(41, "out", null, { toName: "src/gone.ts:vanished" })]);
    const el = s.toElements();
    const dangling = el.nodes.filter((n) => n.type === "dangling");
    expect(dangling).toHaveLength(1);
    expect(dangling[0]!.label).toBe("vanished");
    expect(el.edges).toHaveLength(2);
  });

  it("hide: focus is a no-op; hiding another node removes its edges from elements", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee), edge(11, "in", caller)]);
    s.hide("sym:1");
    expect(s.toElements().nodes.map((n) => n.id)).toContain("sym:1");
    s.hide("sym:2");
    const el = s.toElements();
    expect(el.nodes.map((n) => n.id)).not.toContain("sym:2");
    expect(el.edges.map((e) => e.id)).toEqual(["edge:11"]);
  });

  it("collapse removes neighbors that are only connected through the collapsed node", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    const deep = sym(9, "deep");
    s.expand("sym:2", [edge(50, "out", deep)]);
    expect(s.toElements().nodes).toHaveLength(3);
    s.collapse("sym:2");
    const el = s.toElements();
    expect(el.nodes.map((n) => n.id).sort()).toEqual(["sym:1", "sym:2"]);
    expect(s.getNode("sym:2")?.expanded).toBe(false);
  });

  it("breadcrumbs: setFocus pushes history, goTo restores the expanded set, new focus truncates forward history", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    s.expand("sym:2", [edge(50, "out", sym(9, "deep"))]);
    expect(s.toElements().nodes).toHaveLength(3);

    s.setFocus(caller, [edge(11, "out", focus)]);
    expect(s.breadcrumbs.map((b) => b.label)).toEqual(["focus", "caller"]);
    expect(s.toElements().nodes).toHaveLength(2);

    s.goTo(0);
    expect(s.focusNodeId).toBe("sym:1");
    expect(s.toElements().nodes).toHaveLength(3); // 恢复了当时的展开集合
    expect(s.breadcrumbs).toHaveLength(2);
    expect(s.breadcrumbIndex).toBe(0);

    s.setFocus(callee, []);
    expect(s.breadcrumbs.map((b) => b.label)).toEqual(["focus", "callee"]);
    expect(s.breadcrumbIndex).toBe(1);
  });

  it("directional expand marks only that side; both sides expanded means fully expanded", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    s.expand("sym:2", [edge(50, "out", sym(9, "deep"))], "out");
    const n = s.getNode("sym:2")!;
    expect(n.expandedOut).toBe(true);
    expect(n.expandedIn).toBe(false);
    expect(n.expanded).toBe(false);
    s.expand("sym:2", [edge(10, "in", focus)], "in");
    expect(s.getNode("sym:2")!.expanded).toBe(true);
  });

  it("chainTargets lists the symbol nodes reachable by one hop on a side, excluding external and capsules", () => {
    const s = new GraphState();
    s.setFocus(focus, [
      edge(10, "out", callee),
      edge(11, "out", sym(70, "fetch", "external", "<external>")),
      edge(12, "out", sym(5, "Ok"), { confidence: 0.3, ambiguousGroup: "calls:1" }),
      edge(13, "out", sym(8, "Dto"), { kind: "references" }),
      edge(14, "in", caller),
    ]);
    expect(s.chainTargets("sym:1", "out").map((n) => n.id)).toEqual(["sym:2"]);
    expect(s.chainTargets("sym:1", "in").map((n) => n.id)).toEqual(["sym:3"]);
  });

  it("focusing the same symbol again does not add a duplicate breadcrumb", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    s.setFocus(focus, [edge(10, "out", callee), edge(11, "in", caller)]);
    expect(s.breadcrumbs.map((b) => b.label)).toEqual(["focus"]);
    expect(s.toElements().nodes).toHaveLength(3); // 用新数据重建了
    s.setFocus(caller, []);
    s.setFocus(focus, []);
    expect(s.breadcrumbs.map((b) => b.label)).toEqual(["focus", "caller", "focus"]); // 不相邻的重复允许，像浏览器历史
  });

  it("toElements returns nodes and edges in a stable order regardless of insertion order", () => {
    const a = new GraphState();
    a.setFocus(focus, [edge(10, "out", callee), edge(11, "in", caller)]);
    const b = new GraphState();
    b.setFocus(focus, [edge(11, "in", caller), edge(10, "out", callee)]);
    expect(a.toElements().nodes.map((n) => n.id)).toEqual(b.toElements().nodes.map((n) => n.id));
    expect(a.toElements().edges.map((e) => e.id)).toEqual(b.toElements().edges.map((e) => e.id));
  });

  it("expand with a limit and a rank keeps only the top-ranked targets visible and folds the rest", () => {
    const s = new GraphState();
    s.setFocus(focus, []);
    const n = (id: number, name: string, conf = 0.7) => edge(id, "out", sym(100 + id, name), { confidence: conf });
    const neighbors = [n(1, "a"), n(2, "b"), n(3, "c"), n(4, "d")];
    // rank：名字越靠后分越高
    s.expand("sym:1", neighbors, "out", { limit: 2, rank: (x) => x.other!.name.charCodeAt(0) });
    const el = s.toElements();
    const visible = el.nodes.filter((x) => x.type === "symbol" && x.id !== "sym:1").map((x) => x.label).sort();
    expect(visible).toEqual(["c", "d"]);
    const folded = el.nodes.find((x) => x.type === "ambiguous" && x.overflow);
    expect(folded!.label).toBe("还有 2 个目标");
    expect(s.getCapsuleMembers(folded!.id).map((m) => m.other!.name).sort()).toEqual(["a", "b"]);
  });

  it("external targets of a node fold into one capsule instead of individual nodes", () => {
    const s = new GraphState();
    s.setFocus(focus, [
      edge(1, "out", sym(70, "ToList", "external", "<external>")),
      edge(2, "out", sym(71, "Format", "external", "<external>")),
      edge(3, "out", callee),
    ]);
    const el = s.toElements();
    expect(el.nodes.filter((x) => x.kind === "external")).toHaveLength(0);
    const ext = el.nodes.find((x) => x.type === "ambiguous" && x.externalGroup);
    expect(ext!.label).toBe("2 个框架调用");
    expect(el.nodes.find((x) => x.id === "sym:2")).toBeDefined();
    // 单个外部调用不折叠，直接显示
    const t = new GraphState();
    t.setFocus(focus, [edge(1, "out", sym(70, "ToList", "external", "<external>"))]);
    expect(t.toElements().nodes.find((x) => x.id === "sym:70")).toBeDefined();
  });

  it("markTrunk flags the edges along a node path and clears previous trunk", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    const deep = sym(9, "deep");
    s.expand("sym:2", [edge(50, "out", deep)], "out");
    s.markTrunk(["sym:1", "sym:2", "sym:9"]);
    let el = s.toElements();
    expect(el.edges.filter((e) => e.trunk).map((e) => e.id).sort()).toEqual(["edge:10", "edge:50"]);
    expect(el.nodes.filter((n) => n.trunk).map((n) => n.id).sort()).toEqual(["sym:1", "sym:2", "sym:9"]);
    s.markTrunk(["sym:1", "sym:2"]);
    el = s.toElements();
    expect(el.edges.filter((e) => e.trunk).map((e) => e.id)).toEqual(["edge:10"]);
  });

  it("replaceFocus rebuilds the current crumb instead of pushing a new one", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(10, "out", callee)]);
    s.setFocus(caller, [edge(11, "out", focus)]);
    s.replaceFocus(caller, [edge(11, "out", focus), edge(12, "out", callee)]);
    expect(s.breadcrumbs.map((b) => b.label)).toEqual(["focus", "caller"]);
    expect(s.breadcrumbIndex).toBe(1);
    expect(s.toElements().nodes).toHaveLength(3);
  });

  it("more than 30 neighbors on one side/kind: top 30 by confidence become nodes, the rest fold into an overflow capsule", () => {
    const s = new GraphState();
    const callers = Array.from({ length: 50 }, (_, i) => edge(100 + i, "in", sym(200 + i, `caller${i}`), { confidence: i < 10 ? 0.5 : 0.7 }));
    s.setFocus(focus, callers);

    const el = s.toElements();
    const real = el.nodes.filter((n) => n.type === "symbol" && n.id !== "sym:1");
    expect(real).toHaveLength(GraphState.MaxVisiblePerSide);
    // 低 confidence 的 10 个（caller0..9）应该在溢出胶囊里
    expect(real.map((n) => n.label)).not.toContain("caller0");
    const overflow = el.nodes.find((n) => n.type === "ambiguous");
    expect(overflow).toBeDefined();
    expect(overflow!.label).toBe("还有 20 个调用者");
    expect(s.getCapsuleMembers(overflow!.id)).toHaveLength(20);
    expect(el.edges).toHaveLength(31);

    // 从溢出胶囊里实化两个
    const members = s.getCapsuleMembers(overflow!.id);
    s.expandAmbiguous(overflow!.id, members.slice(0, 2).map((m) => m.edgeId));
    const after = s.toElements();
    expect(after.nodes.filter((n) => n.type === "symbol")).toHaveLength(33);
    expect(after.nodes.find((n) => n.type === "ambiguous")!.label).toBe("还有 18 个调用者");
  });

  it("overflow is counted per direction and kind, so 25 callers plus 25 callees fold nothing", () => {
    const s = new GraphState();
    const ins = Array.from({ length: 25 }, (_, i) => edge(100 + i, "in", sym(200 + i, `in${i}`)));
    const outs = Array.from({ length: 25 }, (_, i) => edge(300 + i, "out", sym(400 + i, `out${i}`)));
    s.setFocus(focus, [...ins, ...outs]);
    expect(s.toElements().nodes.filter((n) => n.type === "ambiguous")).toHaveLength(0);
    expect(s.toElements().nodes).toHaveLength(51);
  });

  it("generic entry-method names are labeled with their class so several HandleAsync nodes stay distinguishable", () => {
    const s = new GraphState();
    const handle = { ...sym(50, "HandleAsync", "method", "H.cs"), qualifiedName: "Acme.Handlers.ListFeedsHandler.HandleAsync" };
    const plain = { ...sym(51, "ListFeedsAsync", "method", "R.cs"), qualifiedName: "Acme.Repo.FeedRepository.ListFeedsAsync" };
    s.setFocus(focus, [edge(80, "out", handle), edge(81, "out", plain)]);
    expect(s.getNode("sym:50")!.label).toBe("ListFeedsHandler.HandleAsync");
    expect(s.getNode("sym:51")!.label).toBe("ListFeedsAsync");
  });

  it("external symbols are flagged not expandable", () => {
    const s = new GraphState();
    s.setFocus(focus, [edge(60, "out", sym(70, "fetch", "external", "<external>"))]);
    const ext = s.getNode("sym:70");
    expect(ext?.expandable).toBe(false);
    expect(s.getNode("sym:1")?.expandable).toBe(true);
  });
});
