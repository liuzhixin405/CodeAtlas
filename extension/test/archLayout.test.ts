import { describe, expect, it } from "vitest";
import { ArchColWidth, ArchRowHeight, layerOf, layoutArch } from "../webview/archLayout";
import type { ArchDto, ArchNodeDto } from "../webview/types";

function node(key: string, kind: string, subKind: string, over: Partial<ArchNodeDto> = {}): ArchNodeDto {
  return { key, kind, subKind, name: key.split("/").pop()!, path: key, source: "discovered", evidence: [], members: [key], ports: [], namespaceRoots: [], outbound: 0, ...over };
}

const dto: ArchDto = {
  nodes: [
    node("external:database:postgresql", "external", "database", { name: "PostgreSQL" }),
    node("Acme.Api", "container", "dotnet-web", { ports: [5001, 8080], outbound: 3 }),
    node("web", "container", "frontend", { name: "acme-web", ports: [3000] }),
    node("Acme.Worker", "container", "dotnet-exe"),
    node("external:broker:kafka", "external", "broker", { name: "Kafka" }),
    node("person:ops", "person", "person", { name: "运维", source: "declared" }),
  ],
  edges: [
    { from: "Acme.Api", to: "external:database:postgresql", protocol: "sql", confidence: 0.5, source: "discovered", evidence: [], configKey: null, url: null },
    { from: "Acme.Worker", to: "external:database:postgresql", protocol: "sql", confidence: 0.5, source: "discovered", evidence: [], configKey: null, url: null },
    { from: "external:broker:kafka", to: "Acme.Worker", protocol: "kafka-consume", confidence: 0.5, source: "discovered", evidence: [], configKey: null, url: null },
    { from: "web", to: "Acme.Api", protocol: "http", confidence: 0.7, source: "discovered", evidence: [], configKey: "Api:Url", url: "http://localhost:5001/" },
    { from: "person:ops", to: "web", protocol: "https", confidence: 1, source: "declared", evidence: [], configKey: null, url: null },
  ],
  conflicts: [],
  note: null,
};

describe("layoutArch", () => {
  it("puts people and frontends left, containers middle, externals right", () => {
    expect(layerOf(dto.nodes[5]!)).toBe(0);
    expect(layerOf(dto.nodes[2]!)).toBe(0);
    expect(layerOf(dto.nodes[1]!)).toBe(1);
    expect(layerOf(dto.nodes[0]!)).toBe(2);
    const { nodes } = layoutArch(dto);
    const x = (id: string) => nodes.find((n) => n.data.id === id)!.position.x;
    expect([x("person:ops"), x("web"), x("Acme.Api"), x("external:broker:kafka")]).toEqual([0, 0, ArchColWidth, 2 * ArchColWidth]);
  });

  it("orders each column by degree then name, and centres shorter columns", () => {
    const { nodes } = layoutArch(dto);
    const y = (id: string) => nodes.find((n) => n.data.id === id)!.position.y;
    // 中列：Acme.Api 2 条边、Acme.Worker 2 条边 → 按名字；两个节点，最高列也是 2 → 不偏移
    expect(y("Acme.Api")).toBe(0);
    expect(y("Acme.Worker")).toBe(ArchRowHeight);
    // 右列：postgresql 2 条边在 kafka 1 条之前
    expect(y("external:database:postgresql")).toBe(0);
    expect(y("external:broker:kafka")).toBe(ArchRowHeight);
    // 左列两节点：web 2 条边（入 + 出）在 ops 1 条之前
    expect(y("web")).toBe(0);
    expect(y("person:ops")).toBe(ArchRowHeight);
  });

  it("labels nodes with kind, ports and outbound count, and edges with protocol and confidence tier", () => {
    const { nodes, edges } = layoutArch(dto);
    expect(nodes.find((n) => n.data.id === "Acme.Api")!.data.label).toBe("Acme.Api\nASP.NET Core :5001/8080\n3 处出口");
    expect(nodes.find((n) => n.data.id === "external:database:postgresql")!.data.label).toBe("PostgreSQL\n数据库");
    expect(nodes.find((n) => n.data.id === "person:ops")!.data.label).toBe("运维\n使用者 · 声明");
    const byId = new Map(edges.map((e) => [e.data.id, e]));
    expect(byId.get("web=>Acme.Api:http")!.data.label).toBe("http · 配置");
    expect(byId.get("web=>Acme.Api:http")!.classes).toBe("discovered firm outbound");
    expect(byId.get("Acme.Api=>external:database:postgresql:sql")!.data.label).toBe("sql · 仅客户端");
    expect(byId.get("Acme.Api=>external:database:postgresql:sql")!.classes).toBe("discovered weak outbound");
    expect(byId.get("external:broker:kafka=>Acme.Worker:kafka-consume")!.classes).toBe("discovered weak inbound");
    expect(byId.get("person:ops=>web:https")!.data.label).toBe("https · 声明");
  });

  it("wraps a crowded external column into two sub-columns", () => {
    const many: ArchDto = {
      ...dto,
      nodes: [node("Acme.Api", "container", "dotnet-web"), ...Array.from({ length: 12 }, (_, i) => node(`external:other:e${String(i).padStart(2, "0")}`, "external", "other"))],
      edges: [],
    };
    const { nodes } = layoutArch(many);
    const ext = nodes.filter((n) => n.data.kind === "external");
    expect(new Set(ext.map((n) => n.position.x)).size).toBe(2);
    expect(Math.max(...ext.map((n) => n.position.y))).toBe(5 * ArchRowHeight); // 12 个分两列 → 6 行
    // 容器列只有 1 个节点，居中到 6 行的中间
    expect(nodes.find((n) => n.data.id === "Acme.Api")!.position.y).toBe((5 * ArchRowHeight) / 2);
  });

  it("is deterministic regardless of input order", () => {
    const shuffled: ArchDto = { ...dto, nodes: [...dto.nodes].reverse(), edges: [...dto.edges].reverse() };
    const a = layoutArch(dto);
    const b = layoutArch(shuffled);
    expect(a.nodes.map((n) => [n.data.id, n.position.x, n.position.y])).toEqual(b.nodes.map((n) => [n.data.id, n.position.x, n.position.y]));
    expect(a.edges.map((e) => e.data.id)).toEqual(b.edges.map((e) => e.data.id));
  });
});
