import { layoutFlow, toggle, type FlowPreset, type FlowRow } from "./flowLayout";
import type { EntryDto, FlowDto, FlowNodeDto } from "./types";

/** 入口种类的分组标题，也决定分组顺序。 */
export const EntryKindLabels: Record<string, string> = {
  http: "HTTP 路由",
  hosted: "常驻服务",
  job: "定时任务",
  hub: "SignalR",
  grpc: "gRPC 服务",
  handler: "分发处理器",
  page: "前端页面",
  "api-route": "前端 API 路由",
};

export interface FlowViewCallbacks {
  onEntrySelect(entry: EntryDto): void;
  onNodeFocus(node: FlowNodeDto): void;
}

/** 入口显示名：HTTP 用「方法 路由」，其他用路由或名字。 */
export function entryTitle(e: EntryDto): string {
  const main = e.route ?? e.name;
  return e.httpMethod ? `${e.httpMethod} ${main}` : main;
}

/** 客户端过滤：路由 / 名字 / 限定名 任一包含关键词（忽略大小写，多个词都要命中）。 */
export function filterEntries(entries: EntryDto[], q: string): EntryDto[] {
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return entries;
  return entries.filter((e) => {
    const hay = `${e.httpMethod ?? ""} ${e.route ?? ""} ${e.name} ${e.qualifiedName}`.toLowerCase();
    return words.every((w) => hay.includes(w));
  });
}

/** 按 kind 分组，顺序按 EntryKindLabels；没见过的 kind 排最后。 */
export function groupEntries(entries: EntryDto[]): Array<[string, EntryDto[]]> {
  const order = Object.keys(EntryKindLabels);
  const groups = new Map<string, EntryDto[]>();
  for (const e of entries) (groups.get(e.kind) ?? groups.set(e.kind, []).get(e.kind)!).push(e);
  return [...groups.entries()].sort(([a], [b]) => {
    const ia = order.indexOf(a), ib = order.indexOf(b);
    return (ia < 0 ? order.length : ia) - (ib < 0 ? order.length : ib) || (a < b ? -1 : 1);
  });
}

/**
 * 入口流程视图：左边是入口清单（可筛选、按种类分组），右边是一张 CSS Grid 画的时序图。
 * 展开/折叠状态留在视图里，换入口时清掉。
 */
export class FlowView {
  private readonly search: HTMLInputElement;
  private readonly list: HTMLElement;
  private readonly diagram: HTMLElement;
  private entries: EntryDto[] = [];
  private selected: number | null = null;
  private dto: FlowDto | null = null;
  private expanded = new Set<string>();
  /** 时序图版式预设，见 flowLayout.FlowPreset。 */
  private preset: FlowPreset | string = "first-seen";

  constructor(container: HTMLElement, private readonly cb: FlowViewCallbacks) {
    container.innerHTML = "";
    const left = document.createElement("div");
    left.className = "flow-list";
    this.search = document.createElement("input");
    this.search.className = "flow-search";
    this.search.placeholder = "筛选入口（路由 / 名字）";
    this.search.autocomplete = "off";
    this.search.oninput = () => this.renderList();
    this.list = document.createElement("div");
    this.list.className = "flow-entries";
    left.append(this.search, this.list);
    this.diagram = document.createElement("div");
    this.diagram.className = "flow-diagram";
    container.append(left, this.diagram);
    this.clearFlow();
  }

  renderEntries(entries: EntryDto[], selected: number | null): void {
    this.entries = entries;
    this.selected = selected;
    this.renderList();
  }

  /** 右侧回到提示文字（换容器、还没选入口时）。 */
  clearFlow(hint = "在左侧选一个入口，看它一路调到哪里。"): void {
    this.dto = null;
    this.expanded = new Set();
    this.diagram.innerHTML = `<p class="flow-empty">${esc(hint)}</p>`;
  }

  renderFlow(dto: FlowDto): void {
    if (this.dto?.entrySymbolId !== dto.entrySymbolId) this.expanded = new Set();
    this.dto = dto;
    this.renderDiagram();
  }

  /** 切换版式预设；已有流程时立刻重画，展开状态保留。 */
  setPreset(preset: FlowPreset | string): void {
    if (this.preset === preset) return;
    this.preset = preset;
    if (this.dto) this.renderDiagram();
  }

  // ---------- 左栏 ----------

  private renderList(): void {
    this.list.innerHTML = "";
    const shown = filterEntries(this.entries, this.search.value);
    if (shown.length === 0) {
      const p = document.createElement("p");
      p.className = "flow-empty";
      p.textContent = this.entries.length === 0 ? "这个容器里没有发现入口（或服务版本太旧）" : "没有匹配的入口";
      this.list.appendChild(p);
      return;
    }
    for (const [kind, group] of groupEntries(shown)) {
      const head = document.createElement("div");
      head.className = "flow-group";
      head.textContent = `${EntryKindLabels[kind] ?? kind} · ${group.length}`;
      this.list.appendChild(head);
      for (const e of group) {
        const item = document.createElement("div");
        item.className = "flow-entry" + (e.symbolId === this.selected ? " active" : "");
        item.title = `${e.qualifiedName}\n${e.file}:${e.line}`;
        const main = e.route ?? e.name;
        item.innerHTML = (e.httpMethod ? `<span class="method ${esc(e.httpMethod.toLowerCase())}">${esc(e.httpMethod)}</span>` : "") +
          `<span class="route">${esc(main)}</span>` +
          (e.route !== null && e.route !== e.name ? `<span class="name">${esc(e.name)}</span>` : "");
        item.onclick = () => this.cb.onEntrySelect(e);
        this.list.appendChild(item);
      }
    }
  }

  // ---------- 右栏 ----------

  private renderDiagram(): void {
    const dto = this.dto;
    if (!dto) return;
    const layout = layoutFlow(dto, this.expanded, this.preset);
    const n = Math.max(1, layout.columns.length);
    this.diagram.innerHTML = "";
    this.diagram.appendChild(this.buildHeader(dto));

    const grid = document.createElement("div");
    grid.className = "flow-grid";
    grid.style.setProperty("--cols", String(n));

    const actors = document.createElement("div");
    actors.className = "flow-actors";
    for (const a of layout.columns) {
      const cell = document.createElement("div");
      cell.className = `flow-actor ${a.kind}`;
      cell.title = `${a.label}（${a.kind === "container" ? "容器" : a.kind === "external" ? "外部" : "项目"}）`;
      cell.innerHTML = `<span>${esc(a.label)}</span>`;
      actors.appendChild(cell);
    }
    grid.appendChild(actors);

    const body = document.createElement("div");
    body.className = "flow-body";
    layout.rows.forEach((row, i) => body.appendChild(this.buildRow(row, i)));
    grid.appendChild(body);
    this.diagram.appendChild(grid);
  }

  private buildHeader(dto: FlowDto): HTMLElement {
    const head = document.createElement("div");
    head.className = "flow-head";
    const title = dto.route && dto.route !== dto.entryLabel ? `${dto.entryLabel} <span class="muted">${esc(dto.route)}</span>` : esc(dto.entryLabel);
    const stats = [`${dto.nodes} 个节点`];
    if (dto.truncated) stats.push("已截断");
    head.innerHTML = `<div class="flow-title"><span class="kind">入口</span><span class="label">${title}</span><span class="muted">容器 ${esc(dto.containerKey)} · ${esc(stats.join("，"))}</span></div>` +
      (dto.behaviors.length > 0 ? `<div class="flow-behaviors muted">行为链（顺序未知）: ${esc(dto.behaviors.join(" → "))}</div>` : "") +
      (dto.note ? `<div class="flow-note">${esc(dto.note)}</div>` : "");
    return head;
  }

  private buildRow(row: FlowRow, index: number): HTMLElement {
    const node = row.node;
    const el = document.createElement("div");
    const lo = Math.min(row.fromCol, row.toCol);
    const hi = Math.max(row.fromCol, row.toCol);
    const span = hi - lo + 1;
    el.className = `flow-arrow ${row.direction} kind-${node.kind}` + (row.hasChildren ? " has-children" : "") + (row.collapsed ? " collapsed" : "") + (node.confidence < 0.7 ? " weak" : "");
    el.style.gridRow = String(index + 1);
    el.style.gridColumn = `${lo + 1} / ${hi + 2}`;
    // 箭头要从起点列的中线画到终点列的中线：列等宽，所以两边各空半列
    if (row.direction === "self") el.style.paddingLeft = node.kind === "entry" ? "0" : "50%";
    else el.style.padding = `0 calc(50% / ${span})`;
    el.title = [node.qualifiedName, node.via ? `via ${node.via}` : "", node.confidence < 1 ? `confidence ${node.confidence.toFixed(1)}` : "", row.collapsed ? `${node.children.length} 个孩子已折起` : "", node.hidden > 0 ? `还有 ${node.hidden} 个没下发` : ""].filter(Boolean).join("\n");

    const label = document.createElement("span");
    label.className = "flow-label";
    const text = node.kind === "more" ? `还有 ${node.count} 个` : row.label;
    label.innerHTML = (row.hasChildren ? `<span class="caret">${row.collapsed ? "▸" : "▾"}</span>` : "") +
      `<span class="text">${esc(text)}</span>` +
      (node.via && node.kind === "hop" ? `<span class="via">${esc(node.via)}</span>` : "") +
      (row.collapsed ? `<span class="muted">(${node.children.length})</span>` : "") +
      (node.kind === "hop" && node.note ? `<span class="note">${esc(node.note)}</span>` : "");
    if (node.symbolId !== null) {
      label.classList.add("link");
      label.onclick = (e) => { e.stopPropagation(); this.cb.onNodeFocus(node); };
    }

    const line = document.createElement("span");
    line.className = node.kind === "entry" ? "flow-dot" : row.direction === "self" ? "flow-loop" : "flow-line";
    if (row.direction === "self" || node.kind === "entry") el.append(line, label);
    else el.append(label, line);

    if (row.hasChildren) {
      el.onclick = () => { this.expanded = toggle(this.expanded, row.id); this.renderDiagram(); };
    }
    return el;
  }
}

function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}
