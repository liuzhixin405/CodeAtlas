import type { FlowPreset } from "./flowLayout";
import { frameworkJumpText, frameworkJumpTitle } from "./frameworkModel";
import { esc, SequenceDiagram } from "./seqDiagram";
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
  /** 点标题里的「经框架流程 ▸」：切到这个流程所在容器的框架流程。 */
  onFrameworkJump?(dto: FlowDto): void;
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

/** 入口流程时序图的标题：入口名、路由、容器、节点数、「经框架流程 ▸」、服务端提示。 */
export function entryFlowHeader(dto: FlowDto, onFramework?: (dto: FlowDto) => void): HTMLElement {
  const head = document.createElement("div");
  head.className = "flow-head";
  const title = dto.route && dto.route !== dto.entryLabel ? `${esc(dto.entryLabel)} <span class="muted">${esc(dto.route)}</span>` : esc(dto.entryLabel);
  const stats = [`${dto.nodes} 个节点`];
  if (dto.truncated) stats.push("已截断");
  head.innerHTML = `<div class="flow-title"><span class="kind">入口</span><span class="label">${title}</span><span class="muted">容器 ${esc(dto.containerKey)} · ${esc(stats.join("，"))}</span></div>`;
  appendFlowHeadExtras(head, dto, onFramework);
  return head;
}

/**
 * 标题下的两行（入口流程、调用图时序模式共用）：
 * - 有行为链时一行「经框架流程 ▸ 行为 ×N」（悬停列出行为），给了回调就可点、点了切到框架流程；
 * - 服务端提示。
 */
export function appendFlowHeadExtras(head: HTMLElement, dto: FlowDto, onFramework?: (dto: FlowDto) => void): void {
  const jump = frameworkJumpText(dto.behaviors);
  if (jump) {
    const el = document.createElement(onFramework ? "a" : "div");
    el.className = "flow-behaviors" + (onFramework ? " flow-fw-jump" : " muted");
    el.textContent = jump;
    el.title = frameworkJumpTitle(dto.behaviors);
    if (onFramework) el.onclick = (e) => { e.stopPropagation(); onFramework(dto); };
    head.appendChild(el);
  }
  if (dto.note) {
    const note = document.createElement("div");
    note.className = "flow-note";
    note.textContent = dto.note;
    head.appendChild(note);
  }
}

/** 入口清单一项的 HTML（入口流程左栏、框架流程的 Handler / 控制器清单共用）；attrs 是额外的 data-* 属性。 */
export function entryItemHtml(e: EntryDto, active: boolean, attrs: Record<string, string> = {}): string {
  const main = e.route ?? e.name;
  const extra = Object.entries(attrs).map(([k, v]) => ` ${k}="${esc(v)}"`).join("");
  return `<div class="flow-entry${active ? " active" : ""}" title="${esc(`${e.qualifiedName}\n${e.file}:${e.line}`)}"${extra}>` +
    (e.httpMethod ? `<span class="method ${esc(e.httpMethod.toLowerCase())}">${esc(e.httpMethod)}</span>` : "") +
    `<span class="route">${esc(main)}</span>` +
    (e.route !== null && e.route !== e.name ? `<span class="name">${esc(e.name)}</span>` : "") + `</div>`;
}

function entryItem(e: EntryDto, active: boolean, onClick: (e: EntryDto) => void): HTMLElement {
  const tmp = document.createElement("div");
  tmp.innerHTML = entryItemHtml(e, active);
  const item = tmp.firstElementChild as HTMLElement;
  item.onclick = () => onClick(e);
  return item;
}

export class FlowView {
  private readonly search: HTMLInputElement;
  private readonly list: HTMLElement;
  private readonly seq: SequenceDiagram;
  private entries: EntryDto[] = [];
  /** 选中的那一条入口（不是符号：顶级语句里的几条 Minimal API 路由与 Main 入口落在同一个符号上）。 */
  private selected: string | null = null;

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
    const diagram = document.createElement("div");
    diagram.className = "flow-diagram";
    container.append(left, diagram);
    // 入口流程的老交互：单击行展开 / 折叠，点名字切到调用图
    const jump = this.cb.onFrameworkJump ? (dto: FlowDto) => this.cb.onFrameworkJump?.(dto) : undefined;
    this.seq = new SequenceDiagram(diagram, { header: (dto) => entryFlowHeader(dto, jump), toggleOnRowClick: true, onNodeLink: (n) => this.cb.onNodeFocus(n) });
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
    this.seq.clear(hint);
  }

  renderFlow(dto: FlowDto): void {
    this.seq.render(dto);
  }

  /** 切换版式预设；已有流程时立刻重画，展开状态保留。 */
  setPreset(preset: FlowPreset | string): void {
    this.seq.setPreset(preset);
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
      for (const e of group) this.list.appendChild(entryItem(e, entryKey(e) === this.selected, (x) => this.cb.onEntrySelect(x)));
    }
  }

}

