import { appendFlowHeadExtras } from "./flowView";
import { FrameworkPanelState, handlerAction, handlerKey, isPlaceholder, stageColumnLabel, type PlaceholderKind } from "./frameworkModel";
import { controllerListHtml, FrameworkHint, frameworkHeaderHtml, handlerEntryKey, handlerListHtml, panelPlaceholder, panelTitle } from "./frameworkHtml";
import { esc, SequenceDiagram } from "./seqDiagram";
import type { ArchNodeDto, EntryDto, FlowNodeDto, FrameworkFlowDto, FrameworkHandlerDto } from "./types";

export interface FrameworkViewCallbacks {
  /** 标题里的容器下拉换了容器。 */
  onContainerChange(key: string): void;
  /** 单击普通行（行为、行为的关键调用、收尾里的后置调用……）：详情条显示它。 */
  onNodeSelect(node: FlowNodeDto): void;
  /** 双击普通行：在调用图（时序模式）里以它为焦点。 */
  onNodeFocus(node: FlowNodeDto): void;
  /** 打开一个入口的入口流程（Handler 清单、控制器清单里点的）。 */
  onOpenEntry(entry: EntryDto): void;
  /** 没有入口分派到的 Handler：在调用图里看它。 */
  onOpenHandler(h: FrameworkHandlerDto): void;
  /** 控制器清单要的入口（容器的入口清单，打开控制器清单时取一次）。 */
  loadEntries(containerKey: string): Promise<EntryDto[]>;
}

/**
 * 框架流程视图：主区是骨架时序图，列 = 阶段；点占位行「N 个 Handler ▸」「N 个控制器 ▸」在右侧展开清单。
 * 时序图用入口流程的同一个组件（SequenceDiagram）：单击行选中看详情，双击在调用图里以它为焦点，点 ▸ 展开行为的关键调用。
 * 清单的 HTML 由 frameworkHtml 拼（纯函数，有单测），这里只按 data-* 分派点击。
 */
export class FrameworkView {
  private readonly seq: SequenceDiagram;
  private readonly diagram: HTMLElement;
  private readonly panel: HTMLElement;
  private readonly panelTitleEl: HTMLElement;
  private readonly search: HTMLInputElement;
  private readonly list: HTMLElement;
  private readonly state = new FrameworkPanelState();
  private dto: FrameworkFlowDto | null = null;
  private containers: ArchNodeDto[] = [];
  private entries: EntryDto[] | null = null;
  private entriesFor: string | null = null;

  constructor(container: HTMLElement, private readonly cb: FrameworkViewCallbacks) {
    container.innerHTML = "";
    const diagram = document.createElement("div");
    diagram.className = "flow-diagram fw-diagram";
    this.diagram = diagram;
    this.panel = document.createElement("aside");
    this.panel.className = "fw-panel";
    this.panel.hidden = true;
    const head = document.createElement("div");
    head.className = "fw-panel-head";
    this.panelTitleEl = document.createElement("span");
    this.panelTitleEl.className = "fw-panel-title";
    const close = document.createElement("button");
    close.className = "secondary fw-close";
    close.textContent = "×";
    close.title = "收起清单";
    close.onclick = () => { this.state.close(); this.renderPanel(); };
    head.append(this.panelTitleEl, close);
    this.search = document.createElement("input");
    this.search.className = "flow-search";
    this.search.autocomplete = "off";
    this.search.oninput = () => {
      if (this.state.open) this.state.setFilter(this.state.open, this.search.value);
      this.renderList();
    };
    this.list = document.createElement("div");
    this.list.className = "flow-entries fw-list";
    this.list.onclick = (e) => this.onListClick(e.target as HTMLElement);
    this.panel.append(head, this.search, this.list);
    container.append(diagram, this.panel);

    this.seq = new SequenceDiagram(diagram, {
      header: () => this.header(),
      toggleOnRowClick: false,
      actorLabel: (a) => stageColumnLabel(a, this.dto?.stages ?? []),
      onRowSelect: (node) => {
        if (isPlaceholder(node)) { this.toggle(node.kind as PlaceholderKind); return; }
        this.cb.onNodeSelect(node);
      },
      onRowDoubleClick: (node) => { if (!isPlaceholder(node)) this.cb.onNodeFocus(node); },
    });
  }

  get current(): FrameworkFlowDto | null {
    return this.dto;
  }

  get openPanel(): PlaceholderKind | null {
    return this.state.open;
  }

  clear(hint: string): void {
    this.dto = null;
    this.state.reset();
    this.entries = null;
    this.entriesFor = null;
    this.seq.clear(hint);
    this.renderPanel();
  }

  /** 拿不到数据（旧服务 404、容器不在了、请求失败）：提示文字上面仍给容器下拉，可以换一个容器。 */
  unavailable(text: string, containers: ArchNodeDto[], currentKey: string, currentName: string): void {
    this.clear(text);
    if (containers.length === 0) return;
    const bar = document.createElement("div");
    bar.className = "flow-head";
    bar.innerHTML = frameworkHeaderHtml({ containerKey: currentKey, containerName: currentName, stages: [], handlers: [], notes: [] }, containers);
    bar.querySelector(".flow-title > .muted")?.remove();
    const sel = bar.querySelector<HTMLSelectElement>("select.fw-container");
    if (sel) sel.onchange = () => this.cb.onContainerChange(sel.value);
    this.diagram.prepend(bar);
  }

  /** 画一个容器的框架流程；同一个容器重画时保留清单的开合与筛选。containers 是标题里下拉的选项。 */
  render(dto: FrameworkFlowDto, containers: ArchNodeDto[]): void {
    if (this.dto?.containerKey !== dto.containerKey) {
      this.state.reset();
      this.entries = null;
      this.entriesFor = null;
      this.seq.clear(""); // 换容器：时序图的展开状态也清掉（不同容器的骨架 entrySymbolId 可能相同）
    }
    this.dto = dto;
    this.containers = containers;
    this.seq.render(dto.flow);
    this.renderPanel();
  }

  /** 打开 / 收起某种清单（点占位行）。 */
  toggle(kind: PlaceholderKind): void {
    this.state.toggle(kind);
    this.renderPanel();
  }

  private header(): HTMLElement {
    const dto = this.dto!;
    const head = document.createElement("div");
    head.className = "flow-head";
    head.innerHTML = frameworkHeaderHtml(dto, this.containers);
    const sel = head.querySelector<HTMLSelectElement>("select.fw-container");
    if (sel) sel.onchange = () => this.cb.onContainerChange(sel.value);
    // 骨架自己不带「经框架流程」（已经在这里了），只留服务端提示
    appendFlowHeadExtras(head, { ...dto.flow, behaviors: [] });
    head.insertAdjacentHTML("beforeend", `<div class="muted flow-hint">${esc(FrameworkHint)}</div>`);
    return head;
  }

  private renderPanel(): void {
    const kind = this.state.open;
    const dto = this.dto;
    this.panel.hidden = kind === null || dto === null;
    if (kind === null || dto === null) return;
    this.search.value = this.state.filter(kind);
    this.search.placeholder = panelPlaceholder(kind, panelTitle(kind, dto, null));
    if (kind === "controllers" && this.entriesFor !== dto.containerKey) {
      const key = dto.containerKey;
      this.entriesFor = key;
      this.entries = null;
      void this.cb.loadEntries(key).then((list) => {
        if (this.entriesFor !== key) return;
        this.entries = list;
        if (this.state.open === "controllers") this.renderList();
      }, () => { if (this.entriesFor === key) { this.entries = []; this.renderList(); } });
    }
    this.renderList();
  }

  private renderList(): void {
    const dto = this.dto;
    const kind = this.state.open;
    if (!dto || !kind) { this.list.innerHTML = ""; return; }
    this.panelTitleEl.textContent = panelTitle(kind, dto, this.entries);
    this.list.innerHTML = kind === "handlers"
      ? handlerListHtml(dto.handlers, this.state.filter("handlers"), (k) => this.state.isExpanded(k))
      : controllerListHtml(this.entries, this.state.filter("controllers"));
  }

  private findHandler(key: string): FrameworkHandlerDto | null {
    for (const g of this.dto?.handlers ?? []) for (const h of g.items) if (handlerKey(h) === key) return h;
    return null;
  }

  private onListClick(target: HTMLElement): void {
    const he = target.closest<HTMLElement>("[data-he]");
    if (he) {
      const key = he.dataset["he"]!;
      const hash = key.lastIndexOf("#");
      const h = this.findHandler(key.slice(0, hash));
      const e = h?.entries[Number(key.slice(hash + 1))];
      if (h && e && handlerEntryKey(h, h.entries.indexOf(e)) === key) this.cb.onOpenEntry(e);
      return;
    }
    const hi = target.closest<HTMLElement>("[data-h]");
    if (hi) {
      const h = this.findHandler(hi.dataset["h"]!);
      if (!h) return;
      const act = handlerAction(h);
      if (act.kind === "entry") this.cb.onOpenEntry(act.entry);
      else if (act.kind === "graph") this.cb.onOpenHandler(h);
      else { this.state.toggleExpanded(handlerKey(h)); this.renderList(); }
      return;
    }
    const ci = target.closest<HTMLElement>("[data-ci]");
    const e = ci && this.entries ? this.entries[Number(ci.dataset["ci"])] : undefined;
    if (e) this.cb.onOpenEntry(e);
  }
}
