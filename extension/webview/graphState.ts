import type { NeighborDto, SymbolDto } from "./types";

/** 图里的节点：真实符号、多候选胶囊、悬空占位。 */
export type NodeType = "symbol" | "ambiguous" | "dangling";

export interface GraphNode {
  id: string;
  type: NodeType;
  label: string;
  kind: string;
  symbol?: SymbolDto;
  /** 两侧都展开过。 */
  expanded: boolean;
  expandedOut: boolean;
  expandedIn: boolean;
  expandable: boolean;
  /** 胶囊：尚未实化的成员边。 */
  members?: NeighborDto[];
  /** 胶囊：挂在哪个节点上、哪一侧。 */
  anchor?: { nodeId: string; direction: "in" | "out" };
  /** 溢出胶囊（超过每侧上限被折起来的普通边），区别于多候选胶囊。 */
  overflow?: boolean;
  /** 外部调用胶囊：一个节点调的所有框架/第三方方法合成一个。 */
  externalGroup?: boolean;
  /** 在主干路径上。 */
  trunk?: boolean;
}

export interface GraphEdge {
  id: string;
  source: string;
  target: string;
  kind: string;
  confidence: number;
  line: number;
  dashed: boolean;
  label?: string;
  /** 在主干路径上。 */
  trunk?: boolean;
}

/** 展开时的取舍：每侧每种边最多直接显示几个、按什么排名（高分优先）。 */
export interface ExpandOptions {
  limit?: number;
  rank?: (n: NeighborDto) => number;
}

export interface Breadcrumb {
  label: string;
  focusNodeId: string;
  snapshot: Snapshot;
}

interface Snapshot {
  nodes: Map<string, GraphNode>;
  edges: Map<string, GraphEdge>;
  hidden: Set<string>;
}

/** 像 HandleAsync / Execute / Consume 这种到处都是的入口方法名，带上类名才分得清。 */
const GenericMethodNames = /^(Handle|HandleAsync|Execute|ExecuteAsync|Consume|ConsumeAsync|Invoke|InvokeAsync|Run|RunAsync|Process|ProcessAsync|Validate|ValidateAsync|Dispose|ToString|Equals|GetHashCode|Build|Create|Get|Set|Add|Update|Delete|Remove|Save|Load|Send|SendAsync|Start|Stop|Initialize|Configure)$/;
function displayLabel(symbol: SymbolDto): string {
  if (symbol.kind !== "method" || !GenericMethodNames.test(symbol.name)) return symbol.name;
  const parts = symbol.qualifiedName.split(/[.:]/);
  return parts.length >= 2 ? `${parts[parts.length - 2]}.${symbol.name}` : symbol.name;
}

/** 边上显示的字：分发边显示 "new 消息类型"，其余显示 via 或 kind。 */
function edgeLabel(n: NeighborDto): string {
  if (n.via?.startsWith("dispatch:")) return `new ${n.via.slice("dispatch:".length)}`;
  return n.via ?? n.kind;
}

function capsuleLabel(count: number, direction: "in" | "out", overflow = false, external = false): string {
  if (external) return `${count} 个框架调用`;
  if (overflow) return direction === "out" ? `还有 ${count} 个目标` : `还有 ${count} 个调用者`;
  return direction === "out" ? `${count} 个可能目标` : `${count} 个可能调用者`;
}

export type Side = "out" | "in" | "both";

export interface Elements {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

/**
 * 纯状态机：焦点、节点、边、隐藏集合、面包屑。
 * 不依赖 DOM / cytoscape。所有交互先改状态再由渲染层读 toElements()。
 */
export class GraphState {
  /** 每个节点每一侧、每种边最多直接显示的邻居数；多出的折成溢出胶囊。 */
  static readonly MaxVisiblePerSide = 30;

  private nodes = new Map<string, GraphNode>();
  private edges = new Map<string, GraphEdge>();
  private hidden = new Set<string>();
  private history: Breadcrumb[] = [];
  private historyIndex = -1;
  focusNodeId: string | null = null;

  static nodeIdFor(symbol: SymbolDto): string {
    return `sym:${symbol.id}`;
  }

  /** 清空一切（切换仓库时用）。 */
  reset(): void {
    this.nodes = new Map();
    this.edges = new Map();
    this.hidden = new Set();
    this.history = [];
    this.historyIndex = -1;
    this.focusNodeId = null;
  }

  get breadcrumbs(): readonly Breadcrumb[] {
    return this.history;
  }

  get breadcrumbIndex(): number {
    return this.historyIndex;
  }

  getNode(id: string): GraphNode | undefined {
    return this.nodes.get(id);
  }

  getCapsuleMembers(capsuleId: string): NeighborDto[] {
    return this.nodes.get(capsuleId)?.members ?? [];
  }

  /** 新焦点：清空图，放入焦点和它的一层邻居，并推入面包屑（丢弃当前位置之后的历史）。 */
  setFocus(symbol: SymbolDto, neighbors: NeighborDto[], opts?: ExpandOptions): void {
    // 连续聚焦同一个符号（右键两次、切开关后刷新）：原地重建，不追加重复的面包屑
    if (this.focusNodeId === GraphState.nodeIdFor(symbol) && this.historyIndex >= 0) {
      this.replaceFocus(symbol, neighbors, opts);
      return;
    }
    this.saveCurrentSnapshot();
    this.rebuild(symbol, neighbors, opts);
    const id = this.focusNodeId!;

    this.history = this.history.slice(0, this.historyIndex + 1);
    this.history.push({ label: symbol.name, focusNodeId: id, snapshot: this.snapshot() });
    this.historyIndex = this.history.length - 1;
  }

  /** 索引刷新后用新数据重建当前焦点，不新增面包屑。 */
  replaceFocus(symbol: SymbolDto, neighbors: NeighborDto[], opts?: ExpandOptions): void {
    const index = this.historyIndex;
    this.rebuild(symbol, neighbors, opts);
    const crumb = { label: symbol.name, focusNodeId: this.focusNodeId!, snapshot: this.snapshot() };
    if (index >= 0) this.history[index] = crumb;
    else { this.history = [crumb]; this.historyIndex = 0; }
  }

  /** 清空图并放入焦点与一层邻居，不碰面包屑。 */
  private rebuild(symbol: SymbolDto, neighbors: NeighborDto[], opts?: ExpandOptions): void {
    this.nodes = new Map();
    this.edges = new Map();
    this.hidden = new Set();
    const id = GraphState.nodeIdFor(symbol);
    this.focusNodeId = id;
    this.ensureSymbolNode(symbol);
    this.applyNeighbors(id, neighbors, opts);
    this.markExpanded(this.nodes.get(id)!, "both");
  }

  /** 展开一个节点的一层邻居（可只展开一侧）。返回新增节点数。 */
  expand(nodeId: string, neighbors: NeighborDto[], side: Side = "both", opts?: ExpandOptions): number {
    const node = this.nodes.get(nodeId);
    if (!node || node.type !== "symbol" || !node.expandable) return 0;
    const before = this.nodes.size;
    this.applyNeighbors(nodeId, neighbors.filter((n) => side === "both" || n.direction === side), opts);
    this.markExpanded(node, side);
    this.saveCurrentSnapshot();
    return this.nodes.size - before;
  }

  /**
   * 沿调用链再走一跳时的下一批节点：该节点某一侧通过真实的 calls / dispatches 边连到的符号节点。
   * 外部符号、悬空占位、胶囊都不算，因为它们没有下一跳。
   */
  chainTargets(nodeId: string, side: "out" | "in"): GraphNode[] {
    const result: GraphNode[] = [];
    for (const e of this.edges.values()) {
      if (e.kind !== "calls" && e.kind !== "dispatches") continue;
      const otherId = side === "out" ? (e.source === nodeId ? e.target : null) : (e.target === nodeId ? e.source : null);
      if (!otherId || otherId === nodeId) continue;
      const other = this.nodes.get(otherId);
      if (!other || other.type !== "symbol" || !other.expandable || this.hidden.has(otherId)) continue;
      if (!result.includes(other)) result.push(other);
    }
    return result;
  }

  private markExpanded(node: GraphNode, side: Side): void {
    if (side === "both" || side === "out") node.expandedOut = true;
    if (side === "both" || side === "in") node.expandedIn = true;
    node.expanded = node.expandedOut && node.expandedIn;
  }

  /** 把胶囊里选中的成员边实化成真实节点与边。 */
  expandAmbiguous(capsuleId: string, edgeIds: number[]): void {
    const capsule = this.nodes.get(capsuleId);
    if (!capsule || capsule.type !== "ambiguous" || !capsule.members || !capsule.anchor) return;
    const chosen = new Set(edgeIds);
    const remaining: NeighborDto[] = [];
    for (const m of capsule.members) {
      if (chosen.has(m.edgeId)) this.addRealEdge(capsule.anchor.nodeId, m);
      else remaining.push(m);
    }
    if (remaining.length === 0) {
      this.nodes.delete(capsuleId);
      for (const [eid, e] of this.edges) if (e.source === capsuleId || e.target === capsuleId) this.edges.delete(eid);
    } else {
      capsule.members = remaining;
      capsule.label = capsuleLabel(remaining.length, capsule.anchor.direction, capsule.overflow, capsule.externalGroup);
    }
    this.saveCurrentSnapshot();
  }

  /** 隐藏节点（焦点不可隐藏）。边随 toElements 过滤掉。 */
  hide(nodeId: string): void {
    if (nodeId === this.focusNodeId || !this.nodes.has(nodeId)) return;
    this.hidden.add(nodeId);
    this.saveCurrentSnapshot();
  }

  /** 折叠：删除只通过该节点连到图上的邻居，并把它标为未展开。 */
  collapse(nodeId: string): void {
    const node = this.nodes.get(nodeId);
    if (!node) return;
    const neighborIds = new Set<string>();
    for (const e of this.edges.values()) {
      if (e.source === nodeId && e.target !== nodeId) neighborIds.add(e.target);
      if (e.target === nodeId && e.source !== nodeId) neighborIds.add(e.source);
    }
    for (const nid of neighborIds) {
      if (nid === this.focusNodeId) continue;
      const otherLinks = [...this.edges.values()].some(
        (e) => (e.source === nid || e.target === nid) && e.source !== nodeId && e.target !== nodeId,
      );
      if (otherLinks) continue;
      this.nodes.delete(nid);
      this.hidden.delete(nid);
      for (const [eid, e] of this.edges) if (e.source === nid || e.target === nid) this.edges.delete(eid);
    }
    node.expanded = false;
    node.expandedOut = false;
    node.expandedIn = false;
    this.saveCurrentSnapshot();
  }

  /** 回到面包屑的某一项，恢复当时的节点、边与隐藏集合。 */
  goTo(index: number): void {
    const crumb = this.history[index];
    if (!crumb) return;
    this.historyIndex = index;
    this.focusNodeId = crumb.focusNodeId;
    this.restore(crumb.snapshot);
  }

  goBack(): void {
    if (this.historyIndex > 0) this.goTo(this.historyIndex - 1);
  }

  toElements(): Elements {
    // 固定顺序：同样的数据给布局算法同样的输入，图才不会每次摆得不一样
    const nodes = [...this.nodes.values()].filter((n) => !this.hidden.has(n.id)).sort((a, b) => a.id.localeCompare(b.id));
    const visible = new Set(nodes.map((n) => n.id));
    const edges = [...this.edges.values()].filter((e) => visible.has(e.source) && visible.has(e.target)).sort((a, b) => a.id.localeCompare(b.id));
    return { nodes, edges };
  }

  // ---- 内部 ----

  /** 标记一条主干路径（节点 id 序列）；之前的标记清除。 */
  markTrunk(path: string[]): void {
    for (const n of this.nodes.values()) n.trunk = false;
    for (const e of this.edges.values()) e.trunk = false;
    for (let i = 0; i < path.length; i++) {
      const node = this.nodes.get(path[i]!);
      if (node) node.trunk = true;
      if (i === 0) continue;
      const a = path[i - 1]!, b = path[i]!;
      for (const e of this.edges.values())
        if ((e.source === a && e.target === b) || (e.source === b && e.target === a)) e.trunk = true;
    }
  }

  private applyNeighbors(anchorId: string, neighbors: NeighborDto[], opts?: ExpandOptions): void {
    const groups = new Map<string, NeighborDto[]>();
    const plain = new Map<string, NeighborDto[]>();
    const externals = new Map<string, NeighborDto[]>();
    for (const n of neighbors) {
      if (n.other?.kind === "external") {
        const key = `${anchorId}|${n.direction}|${n.kind}`;
        (externals.get(key) ?? externals.set(key, []).get(key)!).push(n);
        continue;
      }
      if (n.ambiguousGroup) {
        // 出边：同一调用点（kind:line）的多候选目标折成一个胶囊；
        // 入边：行号在各调用者文件里各不相同，按 kind 折成"可能的调用者"一个胶囊。
        const groupKey = n.direction === "out" ? n.ambiguousGroup : n.kind;
        const key = `${anchorId}|${n.direction}|${groupKey}`;
        (groups.get(key) ?? groups.set(key, []).get(key)!).push(n);
      } else {
        const key = `${anchorId}|${n.direction}|${n.kind}`;
        (plain.get(key) ?? plain.set(key, []).get(key)!).push(n);
      }
    }
    // 普通边：每侧每种最多 limit 个直接显示（按 rank，缺省按 confidence），其余进溢出胶囊
    const limit = opts?.limit ?? GraphState.MaxVisiblePerSide;
    const rank = opts?.rank ?? ((n: NeighborDto) => n.confidence);
    for (const [key, list] of plain) {
      const already = new Set([...this.edges.keys()]);
      const fresh = list.filter((n) => !already.has(`edge:${n.edgeId}`));
      fresh.sort((a, b) => rank(b) - rank(a) || (a.other?.name ?? a.toName).localeCompare(b.other?.name ?? b.toName));
      const visible = fresh.slice(0, limit);
      const rest = fresh.slice(limit);
      for (const n of visible) this.addRealEdge(anchorId, n);
      if (rest.length > 0) this.addCapsule(`ovf:${key}`, anchorId, rest, true);
    }
    // 外部调用：两个以上折成一个"N 个框架调用"胶囊，单个直接显示
    for (const [key, list] of externals) {
      const fresh = list.filter((n) => !this.edges.has(`edge:${n.edgeId}`));
      if (fresh.length === 1) this.addRealEdge(anchorId, fresh[0]!);
      else if (fresh.length > 1) this.addCapsule(`ext:${key}`, anchorId, fresh, false, true);
    }
    for (const [key, members] of groups) {
      const fresh = members.filter((m) => !this.edges.has(`edge:${m.edgeId}`));
      if (fresh.length > 0) this.addCapsule(`amb:${key}`, anchorId, fresh, false);
    }
  }

  private addCapsule(capsuleId: string, anchorId: string, fresh: NeighborDto[], overflow: boolean, externalGroup = false): void {
    {
      const existing = this.nodes.get(capsuleId);
      const first = fresh[0]!;
      if (existing) {
        const known = new Set(existing.members!.map((m) => m.edgeId));
        existing.members!.push(...fresh.filter((m) => !known.has(m.edgeId)));
        existing.label = capsuleLabel(existing.members!.length, existing.anchor!.direction, existing.overflow, existing.externalGroup);
        return;
      }
      this.nodes.set(capsuleId, {
        id: capsuleId,
        type: "ambiguous",
        label: capsuleLabel(fresh.length, first.direction, overflow, externalGroup),
        kind: first.kind,
        expanded: false,
        expandedOut: false,
        expandedIn: false,
        expandable: false,
        members: fresh,
        anchor: { nodeId: anchorId, direction: first.direction },
        overflow,
        externalGroup,
      });
      const [source, target] = first.direction === "out" ? [anchorId, capsuleId] : [capsuleId, anchorId];
      this.edges.set(`cap:${capsuleId}`, {
        id: `cap:${capsuleId}`,
        source,
        target,
        kind: first.kind,
        confidence: 0.3,
        line: first.line,
        dashed: true,
        label: first.kind,
      });
    }
  }

  private addRealEdge(anchorId: string, n: NeighborDto): void {
    let otherId: string;
    if (n.other) {
      otherId = this.ensureSymbolNode(n.other);
    } else {
      otherId = `dangling:${n.toName}`;
      if (!this.nodes.has(otherId)) {
        this.nodes.set(otherId, {
          id: otherId,
          type: "dangling",
          label: n.toName.split(/[.:/]/).pop() ?? n.toName,
          kind: "dangling",
          expanded: false,
          expandedOut: false,
          expandedIn: false,
          expandable: false,
        });
      }
    }
    const [source, target] = n.direction === "out" ? [anchorId, otherId] : [otherId, anchorId];
    const id = `edge:${n.edgeId}`;
    if (!this.edges.has(id)) {
      this.edges.set(id, { id, source, target, kind: n.kind, confidence: n.confidence, line: n.line, dashed: n.confidence <= 0.3, label: edgeLabel(n) });
    }
  }

  private ensureSymbolNode(symbol: SymbolDto): string {
    const id = GraphState.nodeIdFor(symbol);
    if (!this.nodes.has(id)) {
      this.nodes.set(id, {
        id,
        type: "symbol",
        label: displayLabel(symbol),
        kind: symbol.kind,
        symbol,
        expanded: false,
        expandedOut: false,
        expandedIn: false,
        expandable: symbol.kind !== "external",
      });
    }
    return id;
  }

  private snapshot(): Snapshot {
    return {
      nodes: new Map([...this.nodes].map(([k, v]) => [k, { ...v, members: v.members ? [...v.members] : undefined }])),
      edges: new Map([...this.edges].map(([k, v]) => [k, { ...v }])),
      hidden: new Set(this.hidden),
    };
  }

  private restore(s: Snapshot): void {
    this.nodes = new Map([...s.nodes].map(([k, v]) => [k, { ...v, members: v.members ? [...v.members] : undefined }]));
    this.edges = new Map([...s.edges].map(([k, v]) => [k, { ...v }]));
    this.hidden = new Set(s.hidden);
  }

  /** 把当前图状态存回当前面包屑，这样回退再前进时能看到离开时的样子。 */
  private saveCurrentSnapshot(): void {
    const crumb = this.history[this.historyIndex];
    if (crumb) crumb.snapshot = this.snapshot();
  }
}
