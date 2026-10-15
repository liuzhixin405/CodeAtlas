// 影响范围（侧边栏树视图，反向：改它会影响谁）的纯逻辑：ImpactDto → 树节点 → TreeItem 字段。不 import vscode，方便单测；
// impactView.ts 把这里算出的 OutlineItemSpec 原样搬进 vscode.TreeItem。名字、ctx 文案、置信度、跳转目标与调用大纲共用 outlineModel.ts。
import { EntryKindLabels, entryTitle } from "../webview/flowView";
import type { EntryDto, ImpactCallerDto, ImpactCallerKind, ImpactDto, ImpactEntryDto, SymbolDto } from "../webview/types";
import { baseName, confidenceText, ctxLabel, hasDefinition, shortName, type JumpTarget, type OutlineItemSpec } from "./outlineModel";

export type ImpactNode = ImpactRootNode | EntriesGroupNode | ContainerNode | EntryNode | CallersGroupNode | CallerNode | TestsNode | ImpactInfoNode;

export interface ImpactRootNode {
  type: "root";
  symbol: SymbolDto;
  repoRoot: string;
}

/** 「受影响的入口（N）」；entries 为 null = 服务没给（老服务）。 */
export interface EntriesGroupNode {
  type: "entries";
  entries: ImpactEntryDto[] | null;
  truncated: boolean;
}

export interface ContainerNode {
  type: "container";
  key: string;
  name: string;
  entries: ImpactEntryDto[];
  /** 只有一个容器时直接展开。 */
  only: boolean;
}

export interface EntryNode {
  type: "entry";
  item: ImpactEntryDto;
}

/** 「直接调用方（N）」：只在根下面出现；更深的层直接列调用方。 */
export interface CallersGroupNode {
  type: "callers";
  count: number;
  truncated: boolean;
  children: ImpactNode[];
}

export interface CallerNode {
  type: "caller";
  caller: ImpactCallerDto;
  /** 展开路径上已经出现过的方法 id（含根）。caller 在里面 = 递归。 */
  ancestors: ReadonlySet<number>;
}

/** 「被 N 个测试调用」：tests=false 时服务只计数；点它等于切到包含测试。 */
export interface TestsNode {
  type: "tests";
  count: number;
}

export interface ImpactInfoNode {
  type: "info";
  text: string;
  tooltip?: string;
  icon?: string;
}

// ---------- 文案 ----------

/** 调用方式的中文；直接调用不写。 */
export const CallerKindLabels: Record<ImpactCallerKind, string | null> = {
  call: null,
  impl: "经接口",
  dispatch: "经分派",
  new: "构造",
  hop: "跨服务",
};

const CallerKindNames: Record<ImpactCallerKind, string> = {
  call: "直接调用",
  impl: "经接口调用（调用方调的是接口 / 基类方法，实现落到这里）",
  dispatch: "经分派（Mediator / 消息分发到这个 Handler）",
  new: "构造（new 了这个类型）",
  hop: "跨服务调用（别的容器里的 HTTP / gRPC 调用经路由对上这个入口，比如前端页面里的 fetch）",
};

const CallerKindIcons: Record<ImpactCallerKind, string> = {
  call: "symbol-method",
  impl: "symbol-interface",
  dispatch: "send",
  new: "symbol-constructor",
  hop: "globe",
};

/** 入口行前缀：比 EntryKindLabels（分组标题，如"HTTP 路由"）短，后面紧跟路由 / 名字。没列的种类退回 EntryKindLabels。 */
export const EntryPrefixes: Record<string, string> = {
  http: "HTTP",
  command: "命令",
  hosted: "常驻服务",
  job: "定时任务",
  hub: "SignalR",
  grpc: "gRPC",
  handler: "处理器",
  page: "页面",
  "api-route": "API 路由",
};

const EntryIcons: Record<string, string> = {
  http: "globe",
  command: "terminal",
  hosted: "server-process",
  job: "watch",
  hub: "broadcast",
  grpc: "plug",
  handler: "inbox",
  page: "browser",
  "api-route": "cloud",
};

export function entryKindName(kind: string): string {
  return EntryKindLabels[kind] ?? kind;
}

/** 入口一行：`HTTP POST /api/orders`、`定时任务 CleanupJob`、`命令 remote add`。 */
export function entryLabel(e: EntryDto): string {
  const prefix = EntryPrefixes[e.kind] ?? EntryKindLabels[e.kind] ?? e.kind;
  return `${prefix} ${entryTitle(e)}`;
}

export function entryIcon(kind: string): string {
  return EntryIcons[kind] ?? "debug-start";
}

// ---------- 递归 / 可展开 ----------

export function rootAncestors(root: ImpactRootNode): ReadonlySet<number> {
  return new Set([root.symbol.id]);
}

export function isRecursive(node: CallerNode): boolean {
  return node.ancestors.has(node.caller.caller.id);
}

export function canExpand(node: CallerNode): boolean {
  return node.caller.expandable && !isRecursive(node);
}

/** 展开一个调用方后它这一层的 ancestors：父路径 + 这个调用方。 */
export function expandedAncestors(node: CallerNode): ReadonlySet<number> {
  const s = new Set(node.ancestors);
  s.add(node.caller.caller.id);
  return s;
}

// ---------- 子节点 ----------

/**
 * 根请求（entries=true）的结果 → 根下面的两组：受影响的入口、直接调用方。
 * tests：当前是否包含测试（包含时服务已经把测试调用方列进 callers，不再显示"被 N 个测试调用"）。
 */
export function rootChildren(impact: ImpactDto, root: ImpactRootNode, tests: boolean): ImpactNode[] {
  return [
    { type: "entries", entries: impact.entries, truncated: impact.entriesTruncated },
    { type: "callers", count: impact.callers.length, truncated: impact.truncated, children: callerChildren(impact, rootAncestors(root), tests) },
  ];
}

/** 一层调用方（根的「直接调用方」组里，或展开某个调用方后）。 */
export function callerChildren(impact: ImpactDto, ancestors: ReadonlySet<number>, tests: boolean): ImpactNode[] {
  const nodes: ImpactNode[] = impact.callers.map((caller) => ({ type: "caller", caller, ancestors }));
  if (!tests && impact.testCallers > 0) nodes.push({ type: "tests", count: impact.testCallers });
  if (impact.truncated) nodes.push({ type: "info", text: "…调用方过多，后面的省略了", tooltip: "服务按上限截断了这一层；在 CodeTrail Graph 面板里看完整调用。", icon: "ellipsis" });
  if (nodes.length === 0) {
    nodes.push({ type: "info", text: "（没有调用方）", tooltip: "索引里没有找到调用它的地方。可能经反射、依赖注入、框架回调或配置调用，也可能确实没人用。", icon: "dash" });
  }
  return nodes;
}

/** 入口按容器分组：容器按最浅的入口深度、再按名字排；容器里按深度、再按显示名排。 */
export function entryContainers(entries: ImpactEntryDto[]): ContainerNode[] {
  const groups = new Map<string, { name: string; entries: ImpactEntryDto[] }>();
  for (const e of entries) {
    const g = groups.get(e.containerKey) ?? groups.set(e.containerKey, { name: e.containerName || e.containerKey, entries: [] }).get(e.containerKey)!;
    g.entries.push(e);
  }
  const byDepth = (a: ImpactEntryDto, b: ImpactEntryDto) => a.depth - b.depth || cmp(entryLabel(a.entry), entryLabel(b.entry));
  const list = [...groups.entries()].map(([key, g]) => ({ key, name: g.name, entries: [...g.entries].sort(byDepth) }));
  list.sort((a, b) => a.entries[0]!.depth - b.entries[0]!.depth || cmp(a.name, b.name));
  const only = list.length === 1;
  return list.map((g) => ({ type: "container", key: g.key, name: g.name, entries: g.entries, only }));
}

export function containerChildren(node: ContainerNode): EntryNode[] {
  return node.entries.map((item) => ({ type: "entry", item }));
}

export function entriesChildren(node: EntriesGroupNode): ImpactNode[] {
  if (node.entries === null) return [];
  return entryContainers(node.entries);
}

function cmp(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

// ---------- TreeItem 字段 ----------

/** 调用方的 description：[入口] · [经接口 / 经分派 / 构造] · [×N] · [第一处的 ctx] · L行。 */
export function callerDescription(c: ImpactCallerDto, recursive: boolean): string {
  const parts: string[] = [];
  if (recursive) parts.push("↺ 递归");
  const entry = c.asEntry[0];
  if (entry) parts.push(entryLabel(entry.entry) + (c.asEntry.length > 1 ? ` 等 ${c.asEntry.length} 个入口` : ""));
  const k = CallerKindLabels[c.kind];
  if (k) parts.push(k);
  if (c.sites.length > 1) parts.push(`×${c.sites.length}`);
  const first = c.sites[0];
  if (first) {
    const ctx = ctxLabel(first.ctx);
    if (ctx) parts.push(ctx);
    parts.push(`L${first.line}`);
  }
  return parts.join(" · ");
}

/** tooltip 里列出的调用处行号上限，再多写"等 N 处"。 */
export const MaxTooltipSites = 12;

export function callerTooltip(c: ImpactCallerDto, recursive: boolean): string {
  const s = c.caller;
  const lines: string[] = [];
  lines.push(s.qualifiedName || c.label);
  if (s.signature) lines.push(s.signature);
  if (hasDefinition(s)) lines.push(`定义：${s.path}:${s.startLine}`);
  if (c.sites.length > 0) {
    const shown = c.sites.slice(0, MaxTooltipSites).map((site) => {
      const ctx = ctxLabel(site.ctx);
      return `L${site.line}${ctx ? `（${ctx}）` : ""}`;
    });
    const more = c.sites.length > MaxTooltipSites ? ` 等 ${c.sites.length} 处` : "";
    lines.push(`调用处（${s.path}）：${shown.join("、")}${more}`);
  }
  lines.push(`${CallerKindNames[c.kind] ?? c.kind} · ${confidenceText(c.confidence)}`);
  for (const e of c.asEntry) lines.push(`本身是入口：${entryLabel(e.entry)}（${e.containerName || e.containerKey}）`);
  if (c.sites.length > 1) lines.push("单击跳到第一处调用");
  if (recursive) lines.push("↺ 递归：这个方法已经在展开路径上，不再展开");
  return lines.join("\n");
}

export function callerItem(node: CallerNode): OutlineItemSpec {
  const c = node.caller;
  const recursive = isRecursive(node);
  const entry = c.asEntry[0];
  return {
    label: c.label,
    description: callerDescription(c, recursive),
    tooltip: callerTooltip(c, recursive),
    icon: entry ? entryIcon(entry.entry.kind) : CallerKindIcons[c.kind] ?? "symbol-method",
    dim: false,
    collapsible: canExpand(node) ? "collapsed" : "none",
    contextValue: hasDefinition(c.caller) ? "impact.caller.target" : "impact.caller",
    clickable: true,
  };
}

export function rootItem(node: ImpactRootNode): OutlineItemSpec {
  const s = node.symbol;
  return {
    label: shortName(s),
    description: `${baseName(s.path)}:${s.startLine}`,
    tooltip: [s.qualifiedName, s.signature, `${s.path}:${s.startLine}`].filter(Boolean).join("\n"),
    icon: s.kind === "function" ? "symbol-function" : "symbol-method",
    dim: false,
    collapsible: "expanded",
    contextValue: "impact.root.target",
    clickable: true,
  };
}

export function entriesGroupItem(node: EntriesGroupNode): OutlineItemSpec {
  const partial = node.truncated ? "（可能不全）" : "";
  const base = { icon: "debug-start", dim: false, contextValue: "impact.group", clickable: false };
  if (node.entries === null) {
    return { ...base, label: "受影响的入口", description: "服务没有返回", tooltip: "服务没有返回入口信息（服务版本过旧？）。", icon: "warning", dim: true, collapsible: "none" };
  }
  if (node.entries.length === 0) {
    return {
      ...base, label: "没有走到任何入口", description: partial, icon: "dash", dim: true, collapsible: "none",
      tooltip: "沿调用方一路往上没有碰到 HTTP 路由、命令、定时任务等入口。可能经反射 / 框架回调调用，或者只被测试调用。"
        + (node.truncated ? "\n服务按上限停止了向上搜索，结果可能不全。" : ""),
    };
  }
  return {
    ...base,
    label: `受影响的入口（${node.entries.length}）`,
    description: partial,
    tooltip: "沿调用方一路往上能走到的全部入口，按容器分组。改这个方法可能影响这些入口的行为。"
      + (node.truncated ? "\n服务按上限停止了向上搜索，结果可能不全。" : ""),
    collapsible: "expanded",
  };
}

export function containerItem(node: ContainerNode): OutlineItemSpec {
  return {
    label: node.name,
    description: `${node.entries.length} 个入口`,
    tooltip: `${node.name}（${node.key}）`,
    icon: "package",
    dim: false,
    collapsible: node.only ? "expanded" : "collapsed",
    contextValue: "impact.container",
    clickable: false,
  };
}

export function entryItem(node: EntryNode): OutlineItemSpec {
  const { entry, depth, containerName } = node.item;
  return {
    label: entryLabel(entry),
    description: `深度 ${depth}`,
    tooltip: [
      entry.qualifiedName,
      `${entryKindName(entry.kind)} · ${containerName}`,
      `定义：${entry.file}:${entry.line}`,
      depth === 0 ? "这个方法自己就是入口" : `往上 ${depth} 层调用到达`,
    ].join("\n"),
    icon: entryIcon(entry.kind),
    dim: false,
    collapsible: "none",
    contextValue: "impact.entry",
    clickable: true,
  };
}

export function callersGroupItem(node: CallersGroupNode): OutlineItemSpec {
  const n = `${node.count}${node.truncated ? "+" : ""}`;
  return {
    label: `直接调用方（${n}）`,
    description: "",
    tooltip: "直接调用这个方法的地方（含经接口、经分派、构造）。展开一个调用方看谁又调用了它。",
    icon: "call-incoming",
    dim: false,
    collapsible: "expanded",
    contextValue: "impact.group",
    clickable: false,
  };
}

export function testsItem(node: TestsNode): OutlineItemSpec {
  return {
    label: `被 ${node.count} 个测试调用`,
    description: "点击包含测试",
    tooltip: `测试代码里有 ${node.count} 个直接调用方，当前没有列出。点击切到「包含测试」。`,
    icon: "beaker",
    dim: true,
    collapsible: "none",
    contextValue: "impact.tests",
    clickable: true,
  };
}

export function infoItem(node: ImpactInfoNode): OutlineItemSpec {
  return {
    label: node.text,
    description: "",
    tooltip: node.tooltip ?? node.text,
    icon: node.icon ?? "info",
    dim: true,
    collapsible: "none",
    contextValue: "impact.info",
    clickable: false,
  };
}

export function impactItemSpec(node: ImpactNode): OutlineItemSpec {
  switch (node.type) {
    case "root": return rootItem(node);
    case "entries": return entriesGroupItem(node);
    case "container": return containerItem(node);
    case "entry": return entryItem(node);
    case "callers": return callersGroupItem(node);
    case "caller": return callerItem(node);
    case "tests": return testsItem(node);
    case "info": return infoItem(node);
  }
}

// ---------- 单击 / 转到定义 ----------

export type ImpactClick =
  | { kind: "jump"; target: JumpTarget }
  | { kind: "includeTests" }
  | null;

/** 单击：根 → 方法定义；入口 → 入口方法定义；调用方 → 第一处调用（没有调用处就到它的定义）；测试行 → 包含测试。 */
export function impactClick(node: ImpactNode): ImpactClick {
  switch (node.type) {
    case "root": return { kind: "jump", target: { path: node.symbol.path, line: node.symbol.startLine, col: node.symbol.startCol, pos: null } };
    case "entry": return { kind: "jump", target: { path: node.item.entry.file, line: node.item.entry.line, col: null, pos: null } };
    case "caller": {
      const site = node.caller.sites[0];
      if (site) return { kind: "jump", target: { path: node.caller.caller.path, line: site.line, col: null, pos: site.pos ?? null } };
      const def = impactDefinition(node);
      return def ? { kind: "jump", target: def } : null;
    }
    case "tests": return { kind: "includeTests" };
    default: return null;
  }
}

/** 转到定义：调用方方法的定义处；根就是它自己。 */
export function impactDefinition(node: ImpactNode): JumpTarget | null {
  if (node.type === "root") return { path: node.symbol.path, line: node.symbol.startLine, col: node.symbol.startCol, pos: null };
  if (node.type === "caller" && hasDefinition(node.caller.caller)) {
    const s = node.caller.caller;
    return { path: s.path, line: s.startLine, col: s.startCol, pos: null };
  }
  return null;
}
