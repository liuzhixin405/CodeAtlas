import type { ArchNodeDto, EntryDto, FlowActorDto, FlowDto, FlowNodeDto, FrameworkFlowDto, FrameworkHandlerDto, FrameworkHandlerGroupDto, FrameworkStageDto } from "./types";

/**
 * 框架流程（项目地图「容器 | 框架流程 | 命名空间」的第二个）的纯逻辑：不碰 DOM、不 import vscode，便于测试。
 * 框架流程画的是「每个请求都要经过的那套框架」：中间件 → 控制器 → 分派器 → 行为链 → Handler → 收尾，
 * 用和入口流程同一个时序图组件（SequenceDiagram），列 = 阶段；控制器与 Handler 是占位节点，点开看清单。
 */

/** 顶层切换按钮的说明。 */
export const FrameworkTabTitle = "每个请求都要经过的那套框架怎么运转：从进来（中间件 / 过滤器 / 拦截器）到控制器、分派、收尾，各语言按自己的框架画";

/** 消息种类的中文；没见过的原样返回，null 返回空串。 */
export const MessageKindLabels: Record<string, string> = { command: "命令", query: "查询", event: "事件" };

export function messageKindLabel(kind: string | null): string {
  if (!kind) return "";
  return MessageKindLabels[kind.toLowerCase()] ?? kind;
}

/** 框架流程里的占位节点（count = 个数），点开看清单。 */
export type PlaceholderKind = "handlers" | "controllers";

export function isPlaceholder(node: Pick<FlowNodeDto, "kind">): boolean {
  return node.kind === "handlers" || node.kind === "controllers";
}

/** 占位节点那一行的文字：「254 个 Handler ▸」「26 个控制器 ▸」「12 个页面 ▸」——入口占位的名字用服务端给的 label（控制器 / 路由 / 页面 / 入口……）。 */
export function placeholderText(node: Pick<FlowNodeDto, "kind" | "count"> & { label?: string }): string {
  if (node.kind === "handlers") return `${node.count} 个 Handler ▸`;
  return `${node.count} 个${node.label?.trim() || "控制器"} ▸`;
}

/** 入口占位（kind = controllers）的名字：控制器 / 路由 / 页面 / 入口（…）；骨架里没有就是"控制器"。 */
export function entriesLabel(dto: Pick<FrameworkFlowDto, "flow">): string {
  const find = (n: FlowNodeDto): FlowNodeDto | null => n.kind === "controllers" ? n : n.children.reduce<FlowNodeDto | null>((hit, c) => hit ?? find(c), null);
  return find(dto.flow.root)?.label?.trim() || "控制器";
}

/** actor 对应的阶段：服务端的 actor key 就是阶段 key（也接受 "stage|key" 这种带前缀的），再按 label 兜底。 */
export function stageOfActor(actor: FlowActorDto, stages: readonly FrameworkStageDto[]): FrameworkStageDto | null {
  const tail = actor.key.slice(actor.key.lastIndexOf("|") + 1);
  return stages.find((s) => s.key === actor.key || s.key === tail) ?? stages.find((s) => s.label === actor.label) ?? null;
}

/** 列标题：阶段名；阶段内部顺序没有依据（orderKnown=false）时加「（顺序未知）」。 */
export function stageColumnLabel(actor: FlowActorDto, stages: readonly FrameworkStageDto[]): string {
  const stage = stageOfActor(actor, stages);
  const label = stage?.label || actor.label;
  return stage && !stage.orderKnown ? `${label}（顺序未知）` : label;
}

// ---------- Handler 清单 ----------

export function handlerTotal(groups: readonly FrameworkHandlerGroupDto[]): number {
  return groups.reduce((n, g) => n + g.items.length, 0);
}

/** 清单项的主标题：消息类型；没有消息类型（自研分派认不出）就用 Handler 名。 */
export function handlerTitle(h: FrameworkHandlerDto): string {
  return h.messageType ?? h.handler.name;
}

/** 一个 Handler 的身份（清单里展开 / 收起它的入口用）。 */
export function handlerKey(h: FrameworkHandlerDto): string {
  return `${h.handler.id}|${h.messageType ?? ""}`;
}

/**
 * 客户端筛选：消息类型 / Handler 限定名 / 模块名 / 种类（中英文都认）任一包含关键词，多个词都要命中（忽略大小写）。
 * 筛完为空的分组不返回；分组与组内顺序保持服务端给的。
 */
export function filterHandlerGroups(groups: readonly FrameworkHandlerGroupDto[], q: string): FrameworkHandlerGroupDto[] {
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return groups.filter((g) => g.items.length > 0);
  const out: FrameworkHandlerGroupDto[] = [];
  for (const g of groups) {
    const items = g.items.filter((h) => {
      const hay = `${g.module} ${h.messageType ?? ""} ${h.handler.qualifiedName} ${h.messageKind ?? ""} ${messageKindLabel(h.messageKind)}`.toLowerCase();
      return words.every((w) => hay.includes(w));
    });
    if (items.length > 0) out.push({ module: g.module, items });
  }
  return out;
}

/** 点清单里的一个 Handler 该做什么：没有入口 → 在调用图里看它；一个入口 → 直接打开；多个 → 列出来让用户选。 */
export type HandlerAction =
  | { kind: "graph"; handler: FrameworkHandlerDto }
  | { kind: "entry"; entry: EntryDto }
  | { kind: "choose"; entries: EntryDto[] };

export function handlerAction(h: FrameworkHandlerDto): HandlerAction {
  if (h.entries.length === 0) return { kind: "graph", handler: h };
  if (h.entries.length === 1) return { kind: "entry", entry: h.entries[0]! };
  return { kind: "choose", entries: h.entries };
}

// ---------- 控制器清单（复用入口清单的显示，按所在类型分组）----------

/** 控制器清单列哪些入口：HTTP 路由。 */
export function controllerEntries(entries: readonly EntryDto[]): EntryDto[] {
  const http = entries.filter((e) => e.kind === "http");
  // 非 Web 容器没有 HTTP 入口：列常驻服务 / 任务 / 消息消费 / 命令这些（Handler 由分派进来，不算请求的起点）
  return http.length > 0 ? http : entries.filter((e) => e.kind !== "handler");
}

/** 入口所在的类型（控制器 / Minimal API 所在的静态类）：限定名去掉最后一段；只有一段时原样。 */
export function entryOwner(e: Pick<EntryDto, "qualifiedName">): string {
  const q = e.qualifiedName.replace(/\(.*$/, "");
  const i = q.lastIndexOf(".");
  return i < 0 ? q : q.slice(0, i);
}

/** 按所在类型分组，组的顺序按首次出现；组标题用类型名的最后一段。 */
export function groupByOwner(entries: readonly EntryDto[]): Array<{ owner: string; label: string; entries: EntryDto[] }> {
  const groups = new Map<string, EntryDto[]>();
  for (const e of entries) {
    const k = entryOwner(e);
    (groups.get(k) ?? groups.set(k, []).get(k)!).push(e);
  }
  return [...groups.entries()].map(([owner, list]) => ({ owner, label: owner.slice(owner.lastIndexOf(".") + 1) || owner, entries: list }));
}

// ---------- 右侧清单面板的状态 ----------

/** 右侧清单面板：开着哪一种、各自的筛选词、展开了入口的 Handler。换容器时 reset。 */
export class FrameworkPanelState {
  open: PlaceholderKind | null = null;
  private filters: Record<PlaceholderKind, string> = { handlers: "", controllers: "" };
  private expanded = new Set<string>();

  /** 点占位行：没开或开着另一种 → 打开这种；已经开着这种 → 收起。返回新的 open。 */
  toggle(kind: PlaceholderKind): PlaceholderKind | null {
    this.open = this.open === kind ? null : kind;
    return this.open;
  }

  close(): void {
    this.open = null;
  }

  filter(kind: PlaceholderKind): string {
    return this.filters[kind];
  }

  setFilter(kind: PlaceholderKind, q: string): void {
    this.filters[kind] = q;
  }

  isExpanded(key: string): boolean {
    return this.expanded.has(key);
  }

  toggleExpanded(key: string): boolean {
    if (this.expanded.has(key)) { this.expanded.delete(key); return false; }
    this.expanded.add(key);
    return true;
  }

  reset(): void {
    this.open = null;
    this.filters = { handlers: "", controllers: "" };
    this.expanded.clear();
  }
}

// ---------- 进入方式 ----------

/**
 * 框架流程看哪个容器：优先 preferred（当前 / 最近一次钻进的容器）；没有就挑 HTTP 入口最多的（同样多取靠前的）；
 * 都没数就第一个容器。人 / 外部系统不算。没有容器返回 null。
 */
export function pickFrameworkContainer(nodes: readonly ArchNodeDto[], preferredKey: string | null, httpCounts: ReadonlyMap<string, number> | null): ArchNodeDto | null {
  const containers = nodes.filter((n) => n.kind === "container");
  if (containers.length === 0) return null;
  const preferred = preferredKey !== null ? containers.find((c) => c.key === preferredKey) : undefined;
  if (preferred) return preferred;
  let best = containers[0]!;
  let bestCount = httpCounts?.get(best.key) ?? 0;
  for (const c of containers.slice(1)) {
    const n = httpCounts?.get(c.key) ?? 0;
    if (n > bestCount) { best = c; bestCount = n; }
  }
  return best;
}

// ---------- 请求流程那一侧的跳转 ----------

/** 入口流程 / 调用图时序的标题里，行为链那一行：「经框架流程 ▸ 行为 ×N」；没有行为返回 null。 */
export function frameworkJumpText(behaviors: readonly string[]): string | null {
  return behaviors.length > 0 ? `经框架流程 ▸ 行为 ×${behaviors.length}` : null;
}

/** 那一行的悬停说明：行为按服务端给的顺序列出。 */
export function frameworkJumpTitle(behaviors: readonly string[]): string {
  return `行为链：${behaviors.join(" → ")}\n点击切到框架流程（同一个容器），看中间件、分派器、行为链、Handler、收尾各阶段`;
}

/** 跳到哪个容器的框架流程：流程所在容器（FlowDto.containerKey）在容器图里的节点；对不上返回 null。 */
export function frameworkJumpTarget(dto: Pick<FlowDto, "containerKey">, nodes: readonly ArchNodeDto[]): ArchNodeDto | null {
  return nodes.find((n) => n.kind === "container" && n.key === dto.containerKey) ?? null;
}

/** 底部详情条的一句话：各阶段的个数与 Handler 总数。 */
export function frameworkSummary(stages: readonly FrameworkStageDto[], handlers: readonly FrameworkHandlerGroupDto[]): string {
  const parts = stages.map((s) => {
    const n = s.key === "handler" ? handlerTotal(handlers) : s.symbols.length;
    return `${s.label} ${n}${s.orderKnown ? "" : "（顺序未知）"}`;
  });
  return parts.join(" → ");
}
