import type { ArchDto, ArchEdgeDto, ArchNodeDto } from "./types";

/** 容器图三列：使用者 / 前端 | 服务容器 | 外部系统。 */
export type ArchLayer = 0 | 1 | 2;

export const ArchColWidth = 340;
export const ArchRowHeight = 104;
export const ArchNodeWidth = 220;
export const ArchNodeHeight = 68;
/** 外部系统列超过这个数就折成两小列。 */
export const WrapAt = 10;

/** 容器图的预设版式：三列固定（columns）或按依赖方向分层（topological）。AI 只能在这两个里选。 */
export type ArchPreset = "columns" | "topological";

export interface ArchLayoutNode {
  data: {
    id: string;
    label: string;
    kind: string;
    subKind: string;
    /** columns 版式下是 0/1/2 三列；topological 下是层号，可以更大。 */
    layer: number;
    isContainer: boolean;
    payload: ArchNodeDto;
  };
  position: { x: number; y: number };
  classes: string;
}

export interface ArchLayoutEdge {
  data: {
    id: string;
    source: string;
    target: string;
    label: string;
    protocol: string;
    confidence: number;
    payload: ArchEdgeDto;
  };
  classes: string;
}

export function layerOf(n: ArchNodeDto): ArchLayer {
  if (n.kind === "person" || n.subKind === "frontend") return 0;
  if (n.kind === "container") return 1;
  return 2;
}

export const SubKindLabels: Record<string, string> = {
  "dotnet-web": "ASP.NET Core",
  "dotnet-worker": "Worker",
  "dotnet-exe": "进程",
  frontend: "前端",
  database: "数据库",
  cache: "缓存",
  broker: "消息",
  "http-service": "HTTP 服务",
  "grpc-service": "gRPC 服务",
  "native-sdk": "原生 SDK",
  storage: "存储",
  observability: "遥测",
  person: "使用者",
  declared: "声明",
  other: "外部",
};

function nodeLabel(n: ArchNodeDto): string {
  const sub = SubKindLabels[n.subKind] ?? n.subKind;
  const parts = [n.name];
  if (n.kind === "container") {
    const detail = [sub];
    if (n.ports.length > 0) detail.push(`:${n.ports.join("/")}`);
    parts.push(detail.join(" "));
    if (n.outbound > 0) parts.push(`${n.outbound} 处出口`);
  } else {
    parts.push(sub + (n.source === "declared" ? " · 声明" : ""));
  }
  return parts.join("\n");
}

function edgeLabel(e: ArchEdgeDto): string {
  const confidence = e.source === "declared" ? "声明" : e.confidence >= 0.9 ? "路由" : e.confidence >= 0.7 ? "配置" : "仅客户端";
  return `${e.protocol} · ${confidence}`;
}

function byName(a: ArchNodeDto, b: ArchNodeDto): number {
  return a.name < b.name ? -1 : a.name > b.name ? 1 : a.key < b.key ? -1 : a.key > b.key ? 1 : 0;
}

/** columns 版式的分层：人 / 前端、容器、外部 三列，列内按边数降序再按名字。 */
function columnLayers(dto: ArchDto): ArchNodeDto[][] {
  const degree = new Map<string, number>();
  for (const e of dto.edges) {
    degree.set(e.from, (degree.get(e.from) ?? 0) + 1);
    degree.set(e.to, (degree.get(e.to) ?? 0) + 1);
  }
  const byLayer: [ArchNodeDto[], ArchNodeDto[], ArchNodeDto[]] = [[], [], []];
  for (const n of dto.nodes) byLayer[layerOf(n)].push(n);
  for (const list of byLayer) list.sort((a, b) => (degree.get(b.key) ?? 0) - (degree.get(a.key) ?? 0) || byName(a, b));
  return byLayer;
}

/**
 * topological 版式的层号：Tarjan 找强连通分量，分量缩成一点后在 DAG 上按最长路径定层
 * （层号 = 所有前驱层号最大值 + 1，源点 0）；同一个环里的节点同层、按名字排。
 * 没有任何边的节点在第 0 层。
 */
export function topologicalLayers(dto: ArchDto): Map<string, number> {
  const keys = dto.nodes.map((n) => n.key);
  const keySet = new Set(keys);
  const out = new Map<string, string[]>();
  for (const k of keys) out.set(k, []);
  for (const e of dto.edges) if (keySet.has(e.from) && keySet.has(e.to) && e.from !== e.to) out.get(e.from)!.push(e.to);

  // Tarjan
  let index = 0;
  const idx = new Map<string, number>();
  const low = new Map<string, number>();
  const onStack = new Set<string>();
  const stack: string[] = [];
  const sccOf = new Map<string, number>();
  let sccCount = 0;
  const strong = (v: string): void => {
    idx.set(v, index); low.set(v, index); index++;
    stack.push(v); onStack.add(v);
    for (const w of out.get(v)!) {
      if (!idx.has(w)) { strong(w); low.set(v, Math.min(low.get(v)!, low.get(w)!)); }
      else if (onStack.has(w)) low.set(v, Math.min(low.get(v)!, idx.get(w)!));
    }
    if (low.get(v) === idx.get(v)) {
      for (;;) {
        const w = stack.pop()!;
        onStack.delete(w);
        sccOf.set(w, sccCount);
        if (w === v) break;
      }
      sccCount++;
    }
  };
  for (const k of [...keys].sort()) if (!idx.has(k)) strong(k);

  // 缩点后的 DAG 上做 Kahn，层号取前驱最大值 + 1
  const sccOut = new Map<number, Set<number>>();
  const indeg = new Map<number, number>();
  for (let s = 0; s < sccCount; s++) { sccOut.set(s, new Set()); indeg.set(s, 0); }
  for (const [from, tos] of out) {
    const a = sccOf.get(from)!;
    for (const to of tos) {
      const b = sccOf.get(to)!;
      if (a !== b && !sccOut.get(a)!.has(b)) { sccOut.get(a)!.add(b); indeg.set(b, indeg.get(b)! + 1); }
    }
  }
  const layerOfScc = new Map<number, number>();
  let frontier = [...indeg.entries()].filter(([, d]) => d === 0).map(([s]) => s).sort((a, b) => a - b);
  for (const s of frontier) layerOfScc.set(s, 0);
  while (frontier.length > 0) {
    const next: number[] = [];
    for (const s of frontier) {
      for (const t of sccOut.get(s)!) {
        layerOfScc.set(t, Math.max(layerOfScc.get(t) ?? 0, layerOfScc.get(s)! + 1));
        indeg.set(t, indeg.get(t)! - 1);
        if (indeg.get(t) === 0) next.push(t);
      }
    }
    frontier = next.sort((a, b) => a - b);
  }
  const result = new Map<string, number>();
  for (const k of keys) result.set(k, layerOfScc.get(sccOf.get(k)!) ?? 0);
  return result;
}

function topologicalGroups(dto: ArchDto): ArchNodeDto[][] {
  const layer = topologicalLayers(dto);
  const depth = Math.max(0, ...layer.values());
  const groups: ArchNodeDto[][] = Array.from({ length: depth + 1 }, () => []);
  for (const n of dto.nodes) groups[layer.get(n.key) ?? 0]!.push(n);
  for (const g of groups) g.sort(byName);
  return groups;
}

/**
 * 把容器图 DTO 变成带坐标的 cytoscape 元素。preset 决定怎么分层：
 * - `columns`（默认）：使用者 / 前端 | 服务容器 | 外部系统 三列固定，列内按边数降序再按名字；
 * - `topological`：按边方向分层，上游在左；见 topologicalLayers。
 * 纯函数，同样的 DTO 永远同样的图。
 */
export function layoutArch(dto: ArchDto, preset: ArchPreset | string = "columns"): { nodes: ArchLayoutNode[]; edges: ArchLayoutEdge[] } {
  const topological = preset === "topological";
  const groups = topological ? topologicalGroups(dto) : columnLayers(dto);
  // 一列太长时折成两小列：三列版式只对外部系统列这么做，拓扑版式对任何层
  const columnsOf = (layer: number, count: number) => ((topological || layer === 2) && count > WrapAt ? 2 : 1);
  const rowsOf = (layer: number, count: number) => Math.ceil(count / columnsOf(layer, count));
  const tallest = Math.max(1, ...groups.map((l, layer) => rowsOf(layer, l.length)));
  // 每层起点：前一层折成两小列时多占一个节点宽，后面的层整体右移，免得和下一层叠在一起
  const xStart: number[] = [0];
  for (let layer = 1; layer < groups.length; layer++) {
    xStart.push(xStart[layer - 1]! + ArchColWidth + (columnsOf(layer - 1, groups[layer - 1]!.length) - 1) * (ArchNodeWidth + 24));
  }
  const nodes: ArchLayoutNode[] = [];
  groups.forEach((list, layer) => {
    const cols = columnsOf(layer, list.length);
    // 每列垂直居中
    const offset = ((tallest - rowsOf(layer, list.length)) * ArchRowHeight) / 2;
    list.forEach((n, i) => {
      nodes.push({
        data: { id: n.key, label: nodeLabel(n), kind: n.kind, subKind: n.subKind, layer, isContainer: n.kind === "container", payload: n },
        position: { x: xStart[layer]! + (i % cols) * (ArchNodeWidth + 24), y: offset + Math.floor(i / cols) * ArchRowHeight },
        classes: [n.kind, n.subKind, n.source].join(" "),
      });
    });
  });
  const edges: ArchLayoutEdge[] = dto.edges.map((e) => ({
    data: { id: `${e.from}=>${e.to}:${e.protocol}`, source: e.from, target: e.to, label: edgeLabel(e), protocol: e.protocol, confidence: e.confidence, payload: e },
    classes: [e.source, e.confidence >= 0.7 ? "firm" : "weak", e.protocol.endsWith("-consume") ? "inbound" : "outbound"].join(" "),
  }));
  nodes.sort((a, b) => (a.data.id < b.data.id ? -1 : a.data.id > b.data.id ? 1 : 0));
  edges.sort((a, b) => (a.data.id < b.data.id ? -1 : a.data.id > b.data.id ? 1 : 0));
  return { nodes, edges };
}
