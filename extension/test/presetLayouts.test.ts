import { describe, expect, it } from "vitest";
import { ArchColWidth, ArchNodeWidth, ArchRowHeight, layoutArch, topologicalLayers, WrapAt } from "../webview/archLayout";
import { ExternalGroupKey, flowColumns, layoutFlow } from "../webview/flowLayout";
import type { ArchDto, ArchEdgeDto, ArchNodeDto, FlowDto, FlowNodeDto } from "../webview/types";

// ---------- 容器图 topological ----------

function anode(key: string, kind = "container", subKind = "dotnet-web"): ArchNodeDto {
  return { key, kind, subKind, name: key, path: null, source: "discovered", evidence: [], members: [], ports: [], namespaceRoots: [], outbound: 0 };
}
function aedge(from: string, to: string, protocol = "http"): ArchEdgeDto {
  return { from, to, protocol, confidence: 0.7, source: "discovered", evidence: [], configKey: null, url: null };
}

/**
 * ops → web → Api → pg；kafka → Worker → pg；Api ↔ Billing 成环，Billing → Mail；Lonely 无边。
 * 层号（最长路径）：ops 0、kafka 0、Lonely 0；web 1、Worker 1；{Api, Billing} 2；Mail 3、pg 3。
 */
const arch: ArchDto = {
  nodes: [
    anode("person:ops", "person", "person"),
    anode("web", "container", "frontend"),
    anode("Api"),
    anode("Billing"),
    anode("Worker", "container", "dotnet-worker"),
    anode("Lonely", "container", "dotnet-exe"),
    anode("external:database:pg", "external", "database"),
    anode("external:broker:kafka", "external", "broker"),
    anode("external:http-service:Mail", "external", "http-service"),
  ],
  edges: [
    aedge("person:ops", "web", "https"),
    aedge("web", "Api"),
    aedge("Api", "external:database:pg", "sql"),
    aedge("Api", "Billing"),
    aedge("Billing", "Api"),
    aedge("Billing", "external:http-service:Mail"),
    aedge("external:broker:kafka", "Worker", "kafka-consume"),
    aedge("Worker", "external:database:pg", "sql"),
    aedge("Worker", "Worker", "self"),
  ],
  conflicts: [],
  note: null,
};

describe("topological preset", () => {
  it("pins layer numbers: longest path over the SCC-condensed graph", () => {
    const layers = topologicalLayers(arch);
    expect(Object.fromEntries([...layers].sort(([a], [b]) => (a < b ? -1 : 1)))).toEqual({
      Api: 2,
      Billing: 2,
      Lonely: 0,
      Worker: 1,
      "external:broker:kafka": 0,
      "external:database:pg": 3,
      "external:http-service:Mail": 3,
      "person:ops": 0,
      web: 1,
    });
  });

  it("layers go left to right; nodes in a layer (and a cycle) sort by name", () => {
    const { nodes } = layoutArch(arch, "topological");
    const at = (id: string) => nodes.find((n) => n.data.id === id)!;
    expect(at("person:ops").position.x).toBe(0);
    expect(at("web").position.x).toBe(ArchColWidth);
    expect(at("Api").position.x).toBe(2 * ArchColWidth);
    expect(at("Billing").position.x).toBe(2 * ArchColWidth);
    expect(at("external:database:pg").position.x).toBe(3 * ArchColWidth);
    expect(at("Api").data.layer).toBe(2);
    // 第 0 层三个按名字：Lonely < external:broker:kafka < person:ops（name 就是 key）
    const layer0 = nodes.filter((n) => n.data.layer === 0).sort((a, b) => a.position.y - b.position.y).map((n) => n.data.id);
    expect(layer0).toEqual(["Lonely", "external:broker:kafka", "person:ops"]);
    // 环内 Api 在 Billing 上面，相隔一行
    expect(at("Billing").position.y - at("Api").position.y).toBe(ArchRowHeight);
  });

  it("a layer folded into two sub-columns pushes the following layers right", () => {
    const mids = Array.from({ length: WrapAt + 1 }, (_, i) => `T${String(i).padStart(2, "0")}`);
    const wide: ArchDto = {
      nodes: [anode("S"), ...mids.map((k) => anode(k)), anode("Z")],
      edges: [...mids.map((k) => aedge("S", k)), ...mids.map((k) => aedge(k, "Z"))],
      conflicts: [], note: null,
    };
    const { nodes } = layoutArch(wide, "topological");
    const x = (id: string) => nodes.find((n) => n.data.id === id)!.position.x;
    expect([x("S"), x("T00"), x("T01"), x("Z")]).toEqual([0, ArchColWidth, ArchColWidth + ArchNodeWidth + 24, 2 * ArchColWidth + ArchNodeWidth + 24]);
  });

  it("columns preset is still the default", () => {
    const def = layoutArch(arch);
    const cols = layoutArch(arch, "columns");
    expect(def).toEqual(cols);
    expect(def.nodes.find((n) => n.data.id === "external:database:pg")!.position.x).toBe(2 * ArchColWidth);
  });
});

// ---------- 时序图 grouped-external ----------

function fnode(label: string, actor: string, depth: number, children: FlowNodeDto[] = []): FlowNodeDto {
  return { symbolId: null, label, qualifiedName: label, kind: depth === 0 ? "entry" : "call", actor, via: null, confidence: 1, count: 1, depth, children, hidden: 0, note: null };
}

/** 外部 actor 夹在项目 actor 中间：api, ext:db, app, ext:http。 */
const flow: FlowDto = {
  entrySymbolId: 1, entryLabel: "GET /x", route: "/x", containerKey: "api",
  actors: [
    { key: "api", label: "Acme.Api", kind: "container" },
    { key: "ext:db", label: "Npgsql", kind: "external" },
    { key: "app", label: "Acme.App", kind: "project" },
    { key: "ext:http", label: "HttpClient", kind: "external" },
  ],
  root: fnode("Controller.Get", "api", 0, [
    fnode("Db.Query", "ext:db", 1),
    fnode("Service.Run", "app", 1, [fnode("Http.Send", "ext:http", 2)]),
  ]),
  behaviors: [], nodes: 4, truncated: false, note: null,
};

describe("grouped-external preset", () => {
  it("first-seen keeps the server's actor columns", () => {
    const { columns, colOf } = flowColumns(flow, "first-seen");
    expect(columns.map((c) => c.key)).toEqual(["api", "ext:db", "app", "ext:http"]);
    expect([...colOf]).toEqual([["api", 0], ["ext:db", 1], ["app", 2], ["ext:http", 3]]);
  });

  it("merges every external actor into one last column ext|*", () => {
    const { columns, colOf } = flowColumns(flow, "grouped-external");
    expect(columns).toEqual([
      { key: "api", label: "Acme.Api", kind: "container" },
      { key: "app", label: "Acme.App", kind: "project" },
      { key: ExternalGroupKey, label: "外部（2）", kind: "external" },
    ]);
    expect(ExternalGroupKey).toBe("ext|*");
    expect(Object.fromEntries(colOf)).toEqual({ api: 0, app: 1, "ext:db": 2, "ext:http": 2 });
  });

  it("rows draw to the merged column", () => {
    const { rows } = layoutFlow(flow, new Set(), "grouped-external");
    expect(rows.map((r) => [r.id, r.fromCol, r.toCol, r.direction])).toEqual([
      ["0", 0, 0, "self"],
      ["0.0", 0, 2, "right"],
      ["0.1", 0, 1, "right"],
      ["0.1.0", 1, 2, "right"],
    ]);
  });

  it("a single external keeps its own label; no externals adds no column", () => {
    const one: FlowDto = { ...flow, actors: flow.actors.filter((a) => a.key !== "ext:http") };
    expect(flowColumns(one, "grouped-external").columns.map((c) => c.label)).toEqual(["Acme.Api", "Acme.App", "Npgsql"]);
    const none: FlowDto = { ...flow, actors: flow.actors.filter((a) => a.kind !== "external") };
    expect(flowColumns(none, "grouped-external").columns.map((c) => c.key)).toEqual(["api", "app"]);
  });
});
