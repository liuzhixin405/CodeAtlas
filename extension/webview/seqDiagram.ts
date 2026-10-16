import { isPlaceholder, placeholderText } from "./frameworkModel";
import { layoutFlow, toggle, type FlowPreset, type FlowRow } from "./flowLayout";
import type { FlowActorDto, FlowDto, FlowNodeDto } from "./types";

/**
 * 时序图渲染器：一张 CSS Grid（参与者一列一条生命线，调用一行一个箭头），版式由 flowLayout 算。
 * 入口流程（FlowView 右栏）与调用图的时序模式共用这一个类；两边只在标题和行的点击行为上不同（见 SequenceDiagramOptions）。
 * 展开 / 折叠状态留在这里，换了起点（entrySymbolId 变）就清掉。
 */
export interface SequenceDiagramOptions {
  /** 图上方的标题区，每次重画都调。 */
  header(dto: FlowDto): HTMLElement;
  /**
   * 单击一行时也展开 / 折叠（入口流程的老交互）。false 时只有点行首的 ▸ / ▾ 才展开折叠，
   * 单击行留给 onRowSelect（调用图：单击选中看详情，双击换焦点）。
   */
  toggleOnRowClick: boolean;
  /** 单击行（选中）。 */
  onRowSelect?(node: FlowNodeDto, row: FlowRow): void;
  /** 双击行。 */
  onRowDoubleClick?(node: FlowNodeDto, row: FlowRow): void;
  /** 给了就把有符号的名字画成链接，点名字调它（入口流程：切到调用图）。 */
  onNodeLink?(node: FlowNodeDto): void;
  /** 列标题文字；默认用 actor.label（框架流程：阶段名，顺序未知时带说明）。 */
  actorLabel?(actor: FlowActorDto): string;
}

/** 列标题的悬停说明里 actor 种类的中文。 */
export function actorKindText(kind: string): string {
  return kind === "container" ? "容器" : kind === "external" ? "外部" : kind === "stage" ? "阶段" : "项目";
}

/**
 * 一行的文字：「还有 N 个」、框架流程的占位（「254 个 Handler ▸」「26 个控制器 ▸」，count 是个数不是重复次数），
 * 其余用 flowLayout 算好的 label（重复多次时带 ×N）。
 */
export function rowText(row: Pick<FlowRow, "node" | "label">): string {
  const node = row.node;
  if (node.kind === "more") return `还有 ${node.count} 个`;
  if (isPlaceholder(node)) return placeholderText(node);
  return row.label;
}

/** 能当焦点 / 能跳的节点：有符号；没对上后端的接口调用（http）没有符号。 */
export function isFocusableNode(node: FlowNodeDto): boolean {
  return node.symbolId !== null && node.kind !== "http" && node.kind !== "more" && node.kind !== "external-group" && !isPlaceholder(node);
}

export class SequenceDiagram {
  private current: FlowDto | null = null;
  private expanded = new Set<string>();
  private preset: FlowPreset | string = "first-seen";
  /** 选中行的 id（FlowRow.id）；重画后仍高亮。 */
  private selectedId: string | null = null;

  constructor(private readonly el: HTMLElement, private readonly opts: SequenceDiagramOptions) {}

  get dto(): FlowDto | null {
    return this.current;
  }

  /** 回到提示文字。 */
  clear(hint: string): void {
    this.current = null;
    this.expanded = new Set();
    this.selectedId = null;
    this.el.innerHTML = `<p class="flow-empty">${esc(hint)}</p>`;
  }

  render(dto: FlowDto): void {
    if (this.current?.entrySymbolId !== dto.entrySymbolId) { this.expanded = new Set(); this.selectedId = null; }
    this.current = dto;
    this.draw();
  }

  /** 切换版式预设；已有图时立刻重画，展开状态保留。 */
  setPreset(preset: FlowPreset | string): void {
    if (this.preset === preset) return;
    this.preset = preset;
    if (this.current) this.draw();
  }

  private draw(): void {
    const dto = this.current;
    if (!dto) return;
    const layout = layoutFlow(dto, this.expanded, this.preset);
    const n = Math.max(1, layout.columns.length);
    this.el.innerHTML = "";
    this.el.appendChild(this.opts.header(dto));

    const grid = document.createElement("div");
    grid.className = "flow-grid";
    grid.style.setProperty("--cols", String(n));

    const actors = document.createElement("div");
    actors.className = "flow-actors";
    for (const a of layout.columns) {
      const cell = document.createElement("div");
      cell.className = `flow-actor ${a.kind}`;
      const label = this.opts.actorLabel?.(a) ?? a.label;
      cell.title = `${label}（${actorKindText(a.kind)}）`;
      cell.innerHTML = `<span>${esc(label)}</span>`;
      actors.appendChild(cell);
    }
    grid.appendChild(actors);

    const body = document.createElement("div");
    body.className = "flow-body";
    layout.rows.forEach((row, i) => body.appendChild(this.buildRow(row, i)));
    grid.appendChild(body);
    this.el.appendChild(grid);
  }

  private toggleRow(id: string): void {
    this.expanded = toggle(this.expanded, id);
    this.draw();
  }

  private buildRow(row: FlowRow, index: number): HTMLElement {
    const node = row.node;
    const el = document.createElement("div");
    const lo = Math.min(row.fromCol, row.toCol);
    const hi = Math.max(row.fromCol, row.toCol);
    const span = hi - lo + 1;
    el.className = `flow-arrow ${row.direction} kind-${node.kind}` + (row.hasChildren ? " has-children" : "") + (row.collapsed ? " collapsed" : "") +
      (node.confidence < 0.7 ? " weak" : "") + (row.via ? ` ${row.via.cls}` : "") + (row.id === this.selectedId ? " selected" : "") +
      (this.opts.toggleOnRowClick ? "" : " selectable");
    el.dataset["row"] = row.id;
    el.style.gridRow = String(index + 1);
    el.style.gridColumn = `${lo + 1} / ${hi + 2}`;
    // 箭头要从起点列的中线画到终点列的中线：列等宽，所以两边各空半列
    if (row.direction === "self") el.style.paddingLeft = node.kind === "entry" ? "0" : "50%";
    else el.style.padding = `0 calc(50% / ${span})`;
    const isHttp = node.kind === "http";
    el.title = [node.qualifiedName, isHttp && node.note ? node.note : "", node.via ? `via ${node.via}` : "", node.confidence < 1 ? `confidence ${node.confidence.toFixed(1)}` : "", row.collapsed ? `${node.children.length} 个孩子已折起` : "", node.hidden > 0 ? `还有 ${node.hidden} 个没下发` : ""].filter(Boolean).join("\n");

    const label = document.createElement("span");
    label.className = "flow-label";
    const text = rowText(row);
    label.innerHTML = (row.hasChildren ? `<span class="caret" title="${row.collapsed ? "展开" : "折叠"}">${row.collapsed ? "▸" : "▾"}</span>` : "") +
      // 没对上后端的接口调用：标签前一个「接口」小标记，行尾灰字说明原因
      (isHttp ? `<span class="api-tag">接口</span>` : "") +
      `<span class="text">${esc(text)}</span>` +
      (node.via && node.kind === "hop" ? `<span class="via">${esc(node.via)}</span>` : "") +
      (row.via ? `<span class="via">${esc(row.via.tag)}</span>` : "") +
      (row.collapsed ? `<span class="muted">(${node.children.length})</span>` : "") +
      ((node.kind === "hop" || isHttp) && node.note ? `<span class="note">${esc(node.note)}</span>` : "");
    // 接口调用没有符号，点了也不跳；占位节点点整行打开清单
    const link = this.opts.onNodeLink;
    if (link && node.symbolId !== null && !isHttp && !isPlaceholder(node)) {
      label.classList.add("link");
      label.onclick = (e) => { e.stopPropagation(); link(node); };
    }
    const caret = label.querySelector<HTMLElement>(".caret");
    if (caret && row.hasChildren) caret.onclick = (e) => { e.stopPropagation(); this.toggleRow(row.id); };
    // 双击 ▸ 不算双击行（两次点击已经各自展开折叠过）
    caret?.addEventListener("dblclick", (e) => e.stopPropagation());

    const line = document.createElement("span");
    line.className = node.kind === "entry" ? "flow-dot" : row.direction === "self" ? "flow-loop" : "flow-line";
    if (row.direction === "self" || node.kind === "entry") el.append(line, label);
    else el.append(label, line);

    if (this.opts.toggleOnRowClick) {
      if (row.hasChildren) el.onclick = () => this.toggleRow(row.id);
    } else {
      // 只换高亮，不重画：重画会换掉 DOM，第二下点击落在新元素上，dblclick 就不触发了
      el.onclick = () => {
        this.selectedId = row.id;
        this.el.querySelectorAll(".flow-arrow.selected").forEach((x) => x.classList.remove("selected"));
        el.classList.add("selected");
        this.opts.onRowSelect?.(node, row);
      };
    }
    const dbl = this.opts.onRowDoubleClick;
    if (dbl) el.ondblclick = (e) => { e.preventDefault(); dbl(node, row); };
    return el;
  }
}

export function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}
