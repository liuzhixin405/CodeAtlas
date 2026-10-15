import { layoutFlow, toggle, type FlowPreset, type FlowRow } from "./flowLayout";
import type { EntryDto, FlowDto, FlowNodeDto } from "./types";

/** 入口种类的分组标题，也决定分组顺序。 */
export const EntryKindLabels: Record<string, string> = {
  http: "HTTP 路由",
  command: "命令行命令",
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
/** 一条入口的身份：同一个符号上可能有几条入口（顶级语句里的多条路由、Main），只按符号比会一起高亮。 */
export function entryKey(e: Pick<EntryDto, "symbolId" | "kind" | "route" | "httpMethod">): string {
  return `${e.symbolId}|${e.kind}|${e.httpMethod ?? ""}|${e.route ?? ""}`;
}

export const MinListWidth = 180;
export const DefaultListWidth = 300;
const ListWidthKey = "codeatlas.flowListWidth";

/** 入口列表宽度：不小于 MinListWidth，不超过面板的 60%（面板宽度未知时只管下限）。 */
export function clampListWidth(px: number, containerWidth: number): number {
  const max = containerWidth > 0 ? Math.max(MinListWidth, Math.floor(containerWidth * 0.6)) : Number.POSITIVE_INFINITY;
  return Math.round(Math.min(Math.max(px, MinListWidth), max));
}

function applyListWidth(container: HTMLElement, px: number): void {
  container.style.setProperty("--flow-list-width", `${px}px`);
}

/** 拖过的宽度记在本机（webview 的 localStorage 可能不可用，读写失败就用默认值）。 */
function savedListWidth(): number {
  try {
    const v = Number(localStorage.getItem(ListWidthKey));
    return Number.isFinite(v) && v >= MinListWidth ? v : DefaultListWidth;
  } catch {
    return DefaultListWidth;
  }
}

function saveListWidth(px: number): void {
  try { localStorage.setItem(ListWidthKey, String(Math.round(px))); } catch { /* 不记也能用 */ }
}

export class FlowView {
  private readonly search: HTMLInputElement;
  private readonly list: HTMLElement;
  private readonly diagram: HTMLElement;
  private entries: EntryDto[] = [];
  /** 选中的那一条入口（不是符号：顶级语句里的几条 Minimal API 路由与 Main 入口落在同一个符号上）。 */
  private selected: string | null = null;
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
    const resizer = document.createElement("div");
    resizer.className = "flow-resizer";
    resizer.title = "拖动调整入口列表宽度";
    left.append(this.search, this.list, resizer);
    this.diagram = document.createElement("div");
    this.diagram.className = "flow-diagram";
    container.append(left, this.diagram);
    applyListWidth(container, savedListWidth());
    resizer.onpointerdown = (down) => {
      down.preventDefault();
      resizer.setPointerCapture(down.pointerId);
      resizer.classList.add("dragging");
      const startX = down.clientX;
      const startWidth = left.getBoundingClientRect().width;
      resizer.onpointermove = (move) => applyListWidth(container, clampListWidth(startWidth + move.clientX - startX, container.clientWidth));
      resizer.onpointerup = () => {
        resizer.onpointermove = resizer.onpointerup = null;
        resizer.classList.remove("dragging");
        saveListWidth(left.getBoundingClientRect().width);
      };
    };
    this.clearFlow();
  }

  renderEntries(entries: EntryDto[], selected: EntryDto | null): void {
    this.entries = entries;
    this.selected = selected ? entryKey(selected) : null;
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
        item.className = "flow-entry" + (entryKey(e) === this.selected ? " active" : "");
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
    el.className = `flow-arrow ${row.direction} kind-${node.kind}` + (row.hasChildren ? " has-children" : "") + (row.collapsed ? " collapsed" : "") + (node.confidence < 0.7 ? " weak" : "") + (row.via ? ` ${row.via.cls}` : "");
    el.style.gridRow = String(index + 1);
    el.style.gridColumn = `${lo + 1} / ${hi + 2}`;
    // 箭头要从起点列的中线画到终点列的中线：列等宽，所以两边各空半列
    if (row.direction === "self") el.style.paddingLeft = node.kind === "entry" ? "0" : "50%";
    else el.style.padding = `0 calc(50% / ${span})`;
    const isHttp = node.kind === "http";
    el.title = [node.qualifiedName, isHttp && node.note ? node.note : "", node.via ? `via ${node.via}` : "", node.confidence < 1 ? `confidence ${node.confidence.toFixed(1)}` : "", row.collapsed ? `${node.children.length} 个孩子已折起` : "", node.hidden > 0 ? `还有 ${node.hidden} 个没下发` : ""].filter(Boolean).join("\n");

    const label = document.createElement("span");
    label.className = "flow-label";
    const text = node.kind === "more" ? `还有 ${node.count} 个` : row.label;
    label.innerHTML = (row.hasChildren ? `<span class="caret">${row.collapsed ? "▸" : "▾"}</span>` : "") +
      // 没对上后端的接口调用：标签前一个「接口」小标记，行尾灰字说明原因
      (isHttp ? `<span class="api-tag">接口</span>` : "") +
      `<span class="text">${esc(text)}</span>` +
      (node.via && node.kind === "hop" ? `<span class="via">${esc(node.via)}</span>` : "") +
      (row.via ? `<span class="via">${esc(row.via.tag)}</span>` : "") +
      (row.collapsed ? `<span class="muted">(${node.children.length})</span>` : "") +
      ((node.kind === "hop" || isHttp) && node.note ? `<span class="note">${esc(node.note)}</span>` : "");
    // 接口调用没有符号，点了也不跳调用图
    if (node.symbolId !== null && !isHttp) {
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
