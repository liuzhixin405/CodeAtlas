import { Api, DefaultKinds, KindsWithReferences } from "./api";
import { SubKindLabels } from "./archLayout";
import { ArchRenderer } from "./archRenderer";
import { reorderDsm } from "./dsmOrder";
import { DsmView, type DsmCellModel } from "./dsmView";
import { EntryKindLabels, FlowView, entryTitle } from "./flowView";
import { NarrationCard, parseEdgeEvidence, type EvidenceRef } from "./narrationCard";
import { DefaultPreset, LayoutPresets, presetLabel } from "./presets";
import { GraphState, type ExpandOptions, type GraphEdge, type GraphNode } from "./graphState";
import { MapRenderer } from "./mapRenderer";
import { MapState } from "./mapState";
import { host } from "./messaging";
import { collapseImplementations } from "./neighborRules";
import type { CellKind } from "./pipelineLayout";
import { PipelineRenderer } from "./pipelineRenderer";
import { resolveRefreshedSymbol } from "./refresh";
import { Renderer } from "./renderer";
import type { ArchDto, ArchEdgeDto, ArchNodeDto, DsmComponentDto, DsmDto, EntryDto, EvidenceDto, FlowDto, FlowNodeDto, MapEdgeDto, MapGroupDto, NarrativeKind, NeighborDto, PipelineBehaviorDto, PipelineDto, PipelineFlowDto, PipelineItemDto, PipelineRowDto, SymbolDto } from "./types";

// ---------- DOM 骨架 ----------

const app = document.getElementById("app")!;
app.innerHTML = `
  <header>
    <div id="mode"><button id="mode-graph" class="tab active">调用图</button><button id="mode-map" class="tab">项目地图</button></div>
    <input id="search" placeholder="搜索符号（前缀，多个词用空格）" autocomplete="off" />
    <div id="dropdown"></div>
    <nav id="crumbs"></nav>
    <label class="opt" title="聚焦时自动沿调用/分发边向下展开到叶子"><input type="checkbox" id="auto-chain" checked /> 自动向下</label>
    <label class="opt" title="每个节点自动展开几个最像主流程的分支，其余折成「还有 N 个」"><select id="branch-width"><option value="1">主线</option><option value="2" selected>2 分支</option><option value="3">3 分支</option><option value="5">5 分支</option><option value="30">全部</option></select></label>
    <button id="chain-down" class="secondary" title="从焦点沿调用链向下展开到底">↓ 到底</button>
    <button id="chain-up" class="secondary" title="从焦点沿调用者向上追到源头">↑ 到源头</button>
    <label class="opt" title="显示参数/字段等类型引用"><input type="checkbox" id="show-refs" /> 类型引用</label>
    <label class="opt" title="把测试项目里的调用者/被调用者也画出来"><input type="checkbox" id="show-tests" /> 测试代码</label>
    <label class="opt" title="显示分发管道本身：对 QueryAsync/SendAsync 的调用和消息对象的构造"><input type="checkbox" id="show-conduit" /> 分发细节</label>
    <button id="reindex" class="secondary" title="增量索引">⟳ 索引</button>
  </header>
  <main id="cy"><div id="hint">在编辑器里右键"在 CodeAtlas 中查看"，或在上面搜索一个符号。</div></main>
  <main id="map" style="display:none">
    <nav id="map-crumbs"><span id="map-crumb-list"></span><span id="map-view"><button id="view-arch" class="tab" title="容器图：可部署单元、外部系统与它们之间的边">容器</button><button id="view-pipeline" class="tab" title="按管道角色（入口 → 消息 → 行为链 → Handler → 下游）分泳道">管道</button><button id="view-ns" class="tab" title="按命名空间 / 目录分组">命名空间</button></span><label class="opt" id="map-layout-wrap" title="版式预设：AI 的排版建议也只在这些里选" style="display:none"><span>版式</span><select id="map-layout"></select></label></nav>
    <div id="arch-cy" style="display:none"></div><div id="dsm-view" style="display:none"></div><div id="flow-view" style="display:none"></div><div id="pipe-cy" style="display:none"></div><div id="map-cy"></div>
    <aside id="narration"></aside>
  </main>
  <footer id="details"><span class="muted">未选中</span></footer>
  <div id="overlay"><div class="card">
    <h2 id="ov-title">正在连接服务…</h2>
    <p id="ov-text"></p>
    <div id="progress"><div></div></div>
    <button id="ov-index" style="display:none">建立索引</button>
  </div></div>
  <div id="chooser"><div class="dialog">
    <header id="ch-title">选择要加入图的目标</header>
    <div class="list" id="ch-list"></div>
    <div class="actions">
      <button class="secondary" id="ch-all">全选</button>
      <button class="secondary" id="ch-cancel">取消</button>
      <button id="ch-ok">加入</button>
    </div>
  </div></div>
  <div id="ctxmenu"></div>
  <div id="toast"></div>
`;

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const searchEl = $<HTMLInputElement>("search");
const dropdown = $("dropdown");
const crumbsEl = $("crumbs");
const detailsEl = $("details");
const overlay = $("overlay");
const chooser = $("chooser");
const ctxmenu = $("ctxmenu");
const toastEl = $("toast");
const hintEl = $("hint");

// ---------- 状态 ----------

const state = new GraphState();
const mapState = new MapState();
const pipeState = new MapState();
let api: Api | null = null;
let renderer: Renderer | null = null;
let mapRenderer: MapRenderer | null = null;
let pipelineRenderer: PipelineRenderer | null = null;
let archRenderer: ArchRenderer | null = null;
let archDto: ArchDto | null = null;
let dsmView: DsmView | null = null;
/** 当前 DSM 的容器与钻取路径（root 键列表，"" 是项目层）。 */
let dsmContainer: ArchNodeDto | null = null;
let dsmPath: string[] = [];
let dsmDto: DsmDto | null = null;
/** 入口流程视图（L3）：与 DSM 共用 dsmContainer；入口清单、选中的入口和它的流程。 */
let flowView: FlowView | null = null;
let flowContainerKey: string | null = null;
let flowEntries: EntryDto[] = [];
let flowEntry: EntryDto | null = null;
let flowDto: FlowDto | null = null;
/** 实际画出来的 DSM（按预设重排过的）；证据定位按它的下标找行列。 */
let dsmShown: DsmDto | null = null;
/** 右侧叙述卡（M5d）与三类对象当前用的版式预设。 */
let card: NarrationCard | null = null;
const presets: Record<NarrativeKind, string> = { ...DefaultPreset };
let mode: "graph" | "map" = "graph";
type MapViewKind = "arch" | "dsm" | "flow" | "pipeline" | "ns";
/** 地图模式下看哪个视图；null = 还没问过服务。有容器就从容器图开始，否则管道，再否则命名空间。 */
let mapView: MapViewKind | null = null;
let selectedNode: GraphNode | null = null;

/** 链展开的安全阀：深度与每个节点的分支数。超过分支数就停在那个节点，让用户手动展开。 */
const ChainMaxDepth = 12;
const ChainMaxFanout = 6;

function branchWidth(): number {
  return Number($<HTMLSelectElement>("branch-width").value) || 2;
}

/** 名字一看就是校验/日志/判断类的辅助方法，不太可能是主流程。 */
const UtilityName = /^(Validate|Ensure|Check|Format|Log[A-Z]|Is[A-Z]|Has[A-Z]|Can[A-Z]|Try[A-Z]|ThrowIf|Assert|Guard|Normalize|Sanitize)/;

let focusModule = "";
function moduleOf(qname: string): string {
  const parts = qname.split(/[.:/]/);
  return parts.slice(0, 2).join(".");
}

/**
 * 一条边像不像主流程：分发边最像，仓库内普通调用其次；被几十处调用的工具方法、校验/日志类名字、测试代码降权。
 * 外部调用在 GraphState 里单独折叠，这里不用管。
 */
function rankNeighbor(n: NeighborDto): number {
  let s = 0;
  if (n.kind === "dispatches") s += 100;
  else if (n.kind === "calls") s += 30;
  else if (n.kind === "implements" || n.kind === "inherits") s += 10;
  s += n.confidence * 20;
  const o = n.other;
  if (o) {
    if (isTestSymbol(o)) s -= 200;
    if (UtilityName.test(o.name)) s -= 50;
    const callers = o.callers ?? 0;
    if (callers > 40) s -= 60;
    else if (callers > 15) s -= 25;
    if (focusModule && moduleOf(o.qualifiedName) === focusModule) s += 15;
  }
  return s;
}

function expandOptions(): ExpandOptions {
  return { limit: branchWidth(), rank: rankNeighbor };
}

/** 展开时记住每个节点是经哪条边进来的，用来选主干。 */
const arrivedBy = new Map<string, NeighborDto>();
function remember(neighbors: NeighborDto[]): void {
  for (const n of neighbors) if (n.other) arrivedBy.set(GraphState.nodeIdFor(n.other), n);
}

function currentKinds(): string {
  return $<HTMLInputElement>("show-refs").checked ? KindsWithReferences : DefaultKinds;
}
function autoChainEnabled(): boolean {
  return $<HTMLInputElement>("auto-chain").checked;
}

/** 测试代码判定：路径或限定名里带 Tests/Test/Spec 段。追源头时测试方法是最大的噪声，默认不画。 */
const TestPathPattern = /(^|[\/.])(tests?|__tests__|spec|specs)([\/.]|$)|\.(test|spec)\.[tj]sx?$|Tests?\.cs$/i;
function isTestSymbol(s: SymbolDto): boolean {
  return TestPathPattern.test(s.path) || /\.Tests?\./.test(s.qualifiedName);
}
function filterNeighbors(neighbors: NeighborDto[]): NeighborDto[] {
  const showTests = $<HTMLInputElement>("show-tests").checked;
  const showConduit = $<HTMLInputElement>("show-conduit").checked;
  return collapseImplementations(neighbors.filter((n) => {
    // 管道细节：dispatcher.QueryAsync(...) 这类调用和 new 消息对象，默认用一条 dispatch 边代替
    if (!showConduit && n.via === "conduit") return false;
    if (!showTests && n.other && isTestSymbol(n.other)) return false;
    return true;
  }));
}

function toast(msg: string, ms = 2500): void {
  toastEl.textContent = msg;
  toastEl.classList.add("open");
  window.setTimeout(() => toastEl.classList.remove("open"), ms);
}

function render(): void {
  renderer?.render(state.toElements(), state.focusNodeId);
  renderCrumbs();
  hintEl.style.display = state.focusNodeId ? "none" : "block";
}

function renderCrumbs(): void {
  crumbsEl.innerHTML = "";
  state.breadcrumbs.forEach((b, i) => {
    if (i > 0) crumbsEl.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (i === state.breadcrumbIndex ? " current" : "");
    el.textContent = b.label;
    el.title = b.focusNodeId;
    el.onclick = () => { state.goTo(i); render(); };
    crumbsEl.appendChild(el);
  });
}

function edgeKindText(e: GraphEdge): string {
  if (e.kind === "dispatches") return e.label === "handles" ? "handles（Handler 处理该类型）" : `分发：${e.label} 交给它的 Handler 处理`;
  if (e.label === "conduit") return "calls（分发管道）";
  if (e.label === "impl") return "calls（接口→唯一实现）";
  return e.kind;
}

function confidenceText(c: number): string {
  if (c >= 1) return "确定";
  if (c >= 0.9) return "同文件唯一";
  if (c >= 0.7) return "import 范围唯一";
  if (c >= 0.5) return "全仓库唯一或外部";
  return "多候选之一";
}

function showNodeDetails(n: GraphNode | null): void {
  selectedNode = n;
  if (!n) { detailsEl.innerHTML = '<span class="muted">未选中</span>'; return; }
  if (n.type === "symbol" && n.symbol) {
    const s = n.symbol;
    detailsEl.innerHTML = `<span class="kind">${s.kind}</span><span class="mono">${esc(s.qualifiedName)}</span>` +
      `<span class="muted">${esc(s.path)}:${s.startLine}</span>` +
      (s.kind !== "external" ? `<a id="open-link">在编辑器中打开</a>` : "") +
      (s.signature ? `<span class="muted mono" style="margin-left:auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(s.signature)}</span>` : "");
    detailsEl.querySelector<HTMLAnchorElement>("#open-link")?.addEventListener("click", () => openInEditor(s));
    if (n.expandedIn && state.chainTargets(n.id, "in").length === 0) detailsEl.insertAdjacentHTML("beforeend", '<span class="kind">入口?</span><span class="muted">没有找到调用者</span>');
  } else if (n.type === "ambiguous") {
    detailsEl.innerHTML = `<span class="kind">${n.externalGroup ? "框架调用" : n.overflow ? "已折叠" : "多候选"}</span><span>${n.label}，双击节点选择要加入的</span>`;
  } else {
    detailsEl.innerHTML = `<span class="kind">悬空</span><span class="mono">${esc(n.id.replace(/^dangling:/, ""))}</span><span class="muted">目标已不在索引中</span>`;
  }
}

function showEdgeDetails(e: GraphEdge | null): void {
  if (!e) return;
  const src = state.getNode(e.source)?.label ?? e.source;
  const dst = state.getNode(e.target)?.label ?? e.target;
  detailsEl.innerHTML = `<span class="kind">${esc(edgeKindText(e))}</span><span class="mono">${esc(src)} → ${esc(dst)}</span>` +
    `<span class="muted">confidence ${e.confidence.toFixed(1)}（${confidenceText(e.confidence)}）· 第 ${e.line} 行</span>`;
}

function esc(s: string): string {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
}

function openInEditor(s: SymbolDto): void {
  host.post({ type: "open", path: s.path, line: s.startLine, col: s.startCol });
  if (!host.isVsCode) toast(`（浏览器模式）打开 ${s.path}:${s.startLine}`);
}

// ---------- 核心动作 ----------

async function focusSymbol(symbol: SymbolDto, replace = false): Promise<void> {
  if (!api) return;
  const neighbors = filterNeighbors(await api.neighbors(symbol.id, "both", currentKinds()));
  focusModule = moduleOf(symbol.qualifiedName);
  arrivedBy.clear();
  remember(neighbors);
  if (replace) state.replaceFocus(symbol, neighbors, expandOptions());
  else state.setFocus(symbol, neighbors, expandOptions());
  render();
  showNodeDetails(state.getNode(GraphState.nodeIdFor(symbol)) ?? null);
  if (autoChainEnabled()) await expandChain(GraphState.nodeIdFor(symbol), "out");
}

/** 从焦点出发，每一步选分最高的孩子，直到叶子：这就是主干。 */
function highlightTrunk(): void {
  if (!state.focusNodeId) return;
  const path = [state.focusNodeId];
  const visited = new Set(path);
  for (;;) {
    const targets = state.chainTargets(path[path.length - 1]!, "out").filter((t) => !visited.has(t.id));
    if (targets.length === 0) break;
    targets.sort((a, b) => (arrivedBy.has(b.id) ? rankNeighbor(arrivedBy.get(b.id)!) : 0) - (arrivedBy.has(a.id) ? rankNeighbor(arrivedBy.get(a.id)!) : 0));
    const next = targets[0]!;
    visited.add(next.id);
    path.push(next.id);
  }
  state.markTrunk(path.length > 1 ? path : []);
}

/**
 * 沿一侧一路展开：向下走到叶子（外部库 / 没有下一跳），向上追到源头（没有调用者）。
 * 分支数超过 ChainMaxFanout 的节点停下不再自动继续，避免图爆炸。
 */
async function expandChain(startId: string, side: "out" | "in"): Promise<void> {
  if (!api) return;
  const visited = new Set<string>([startId]);
  let frontier = state.chainTargets(startId, side);
  const stoppedAt: string[] = [];
  for (let depth = 0; depth < ChainMaxDepth && frontier.length > 0; depth++) {
    const next: GraphNode[] = [];
    for (const node of frontier) {
      if (visited.has(node.id) || !node.symbol) continue;
      visited.add(node.id);
      const already = side === "out" ? node.expandedOut : node.expandedIn;
      if (!already) {
        const neighbors = filterNeighbors(await api.neighbors(node.symbol.id, side, currentKinds()));
        remember(neighbors);
        state.expand(node.id, neighbors, side, expandOptions());
      }
      const targets = state.chainTargets(node.id, side);
      if (targets.length > ChainMaxFanout) { stoppedAt.push(node.label); continue; }
      next.push(...targets.filter((t) => !visited.has(t.id)));
    }
    frontier = next;
    render();
  }
  if (side === "out") { highlightTrunk(); render(); }
  if (stoppedAt.length > 0) toast(`${stoppedAt.slice(0, 3).join("、")}${stoppedAt.length > 3 ? " 等" : ""} 分支超过 ${ChainMaxFanout} 个，已停止自动展开，双击可继续`, 5000);
}

async function expandNode(node: GraphNode): Promise<void> {
  if (!api || node.type !== "symbol" || !node.symbol || !node.expandable) return;
  const neighbors = filterNeighbors(await api.neighbors(node.symbol.id, "both", currentKinds()));
  remember(neighbors);
  // 超过每侧上限的邻居由 GraphState 自动折成"还有 N 个"胶囊，点开再选；没有邻居也算展开过，再双击就跳代码
  state.expand(node.id, neighbors, "both", expandOptions());
  if (neighbors.length === 0) toast("没有更多邻居");
  render();
}

async function expandCapsule(node: GraphNode): Promise<void> {
  const members = state.getCapsuleMembers(node.id);
  const title = node.overflow ? `${node.label}，选择要加入图的` : `${node.label}：这些同名符号都可能是真正的目标`;
  const chosen = await chooseNeighbors(title, members);
  if (!chosen || chosen.length === 0) return;
  state.expandAmbiguous(node.id, chosen.map((m) => m.edgeId));
  render();
  showNodeDetails(state.getNode(node.id) ?? null);
}

/** 清单弹层：按 kind 分组，带复选框。取消返回 null。 */
function chooseNeighbors(title: string, items: NeighborDto[]): Promise<NeighborDto[] | null> {
  return new Promise((resolve) => {
    $("ch-title").textContent = title;
    const list = $("ch-list");
    list.innerHTML = "";
    const byKind = new Map<string, NeighborDto[]>();
    for (const n of items) {
      const k = `${n.direction === "out" ? "→" : "←"} ${n.kind}`;
      (byKind.get(k) ?? byKind.set(k, []).get(k)!).push(n);
    }
    for (const [k, group] of byKind) {
      list.insertAdjacentHTML("beforeend", `<div class="group">${esc(k)} · ${group.length}</div>`);
      for (const n of group) {
        const label = document.createElement("label");
        const name = n.other?.qualifiedName ?? n.toName;
        label.innerHTML = `<input type="checkbox" data-edge="${n.edgeId}" /><span class="qname" title="${esc(name)}">${esc(name)}</span><span class="path">${n.confidence.toFixed(1)} · L${n.line}</span>`;
        list.appendChild(label);
      }
    }
    const boxes = () => [...list.querySelectorAll<HTMLInputElement>("input[type=checkbox]")];
    const done = (result: NeighborDto[] | null) => { chooser.classList.remove("open"); resolve(result); };
    $("ch-all").onclick = () => boxes().forEach((b) => (b.checked = true));
    $("ch-cancel").onclick = () => done(null);
    $("ch-ok").onclick = () => {
      const ids = new Set(boxes().filter((b) => b.checked).map((b) => Number(b.dataset["edge"])));
      done(items.filter((i) => ids.has(i.edgeId)));
    };
    chooser.classList.add("open");
  });
}

function showContextMenu(node: GraphNode, x: number, y: number): void {
  const items: Array<{ label: string; run?: () => void }> = [];
  if (node.type === "symbol" && node.symbol) {
    const s = node.symbol;
    items.push({ label: "设为焦点", run: () => void focusSymbol(s) });
    if (node.expandable) items.push({ label: node.expanded ? "折叠邻居" : "展开一层", run: () => (node.expanded ? (state.collapse(node.id), render()) : void expandNode(node)) });
    if (node.id !== state.focusNodeId) items.push({ label: "从图中隐藏", run: () => { state.hide(node.id); render(); } });
    if (s.kind !== "external") items.push({ label: "在编辑器中打开", run: () => openInEditor(s) });
  } else if (node.type === "ambiguous") {
    items.push({ label: "选择要加入的目标…", run: () => void expandCapsule(node) });
    items.push({ label: "从图中隐藏", run: () => { state.hide(node.id); render(); } });
  } else {
    items.push({ label: "从图中隐藏", run: () => { state.hide(node.id); render(); } });
  }
  ctxmenu.innerHTML = items.map((i, idx) => `<div class="mi${i.run ? "" : " disabled"}" data-i="${idx}">${esc(i.label)}</div>`).join("");
  ctxmenu.querySelectorAll<HTMLElement>(".mi").forEach((el) => {
    el.onclick = () => { ctxmenu.classList.remove("open"); items[Number(el.dataset["i"])]?.run?.(); };
  });
  ctxmenu.style.left = `${Math.min(x, window.innerWidth - 180)}px`;
  ctxmenu.style.top = `${Math.min(y, window.innerHeight - items.length * 28 - 10)}px`;
  ctxmenu.classList.add("open");
}
document.addEventListener("click", () => ctxmenu.classList.remove("open"));
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { ctxmenu.classList.remove("open"); chooser.classList.remove("open"); dropdown.classList.remove("open"); }
  if (e.key === "Enter" && selectedNode?.type === "symbol" && selectedNode.symbol && document.activeElement !== searchEl) openInEditor(selectedNode.symbol);
});

// ---------- 搜索 ----------

let searchTimer = 0;
let activeIndex = -1;
let lastHits: SymbolDto[] = [];
searchEl.addEventListener("input", () => {
  window.clearTimeout(searchTimer);
  const q = searchEl.value.trim();
  if (!q) { dropdown.classList.remove("open"); return; }
  searchTimer = window.setTimeout(async () => {
    if (!api) return;
    lastHits = await api.search(q, 20);
    activeIndex = -1;
    dropdown.innerHTML = lastHits.length === 0
      ? '<div class="item"><span class="muted">没有匹配</span></div>'
      : lastHits.map((s, i) => `<div class="item" data-i="${i}"><span class="kind">${s.kind}</span><span class="qname" title="${esc(s.qualifiedName)}">${esc(s.qualifiedName)}</span><span class="path">${esc(s.path)}:${s.startLine}</span></div>`).join("");
    dropdown.querySelectorAll<HTMLElement>(".item[data-i]").forEach((el) => {
      el.onclick = () => pick(Number(el.dataset["i"]));
    });
    dropdown.classList.add("open");
  }, 300);
});
searchEl.addEventListener("keydown", (e) => {
  if (!dropdown.classList.contains("open")) return;
  if (e.key === "ArrowDown") { activeIndex = Math.min(activeIndex + 1, lastHits.length - 1); highlight(); e.preventDefault(); }
  if (e.key === "ArrowUp") { activeIndex = Math.max(activeIndex - 1, 0); highlight(); e.preventDefault(); }
  if (e.key === "Enter") { pick(activeIndex >= 0 ? activeIndex : 0); e.preventDefault(); }
});
function highlight(): void {
  dropdown.querySelectorAll<HTMLElement>(".item[data-i]").forEach((el, i) => el.classList.toggle("active", i === activeIndex));
}
function pick(i: number): void {
  const s = lastHits[i];
  dropdown.classList.remove("open");
  if (s) { searchEl.value = ""; void focusSymbol(s); }
}

// ---------- 项目地图 ----------

function setMode(next: "graph" | "map"): void {
  mode = next;
  $("cy").style.display = next === "graph" ? "" : "none";
  $("map").style.display = next === "map" ? "" : "none";
  $("mode-graph").classList.toggle("active", next === "graph");
  $("mode-map").classList.toggle("active", next === "map");
  for (const id of ["auto-chain", "chain-down", "chain-up", "show-refs", "show-tests", "show-conduit", "branch-width"]) {
    const el = $(id);
    (el.closest("label") ?? el).style.display = next === "graph" ? "" : "none";
  }
  if (next === "map") { mapRenderer?.resize(); pipelineRenderer?.resize(); archRenderer?.resize(); void loadMapView(); } else renderer?.resize();
}

/** 第一次进地图模式先问服务：有容器就从容器图开始；没有再看有没有分发数据（管道）；都没有就命名空间视图。 */
async function loadMapView(): Promise<void> {
  if (!api) return;
  if (mapView === null) {
    const a = await api.arch();
    archDto = a;
    const archAvailable = a !== null && a.nodes.length > 0;
    $<HTMLButtonElement>("view-arch").disabled = !archAvailable;
    $("view-arch").title = a === null ? "服务版本太旧，没有 /api/arch/containers；重启服务或更新扩展" : archAvailable ? "容器图：可部署单元、外部系统与它们之间的边" : (a.note ?? "没有发现可部署单元");
    const p = await api.pipeline("");
    const pipelineAvailable = p !== null && p.rows.length > 0;
    $<HTMLButtonElement>("view-pipeline").disabled = !pipelineAvailable;
    $("view-pipeline").title = p === null ? "服务版本太旧，没有 /api/pipeline；重启服务或更新扩展"
      : pipelineAvailable ? "按管道角色（入口 → 消息 → 行为链 → Handler → 下游）分泳道" : "索引里没有分发（dispatches）数据，看不出管道";
    if (archAvailable) { applyMapView("arch"); showArch(a!); return; }
    applyMapView(pipelineAvailable ? "pipeline" : "ns");
    if (p !== null && pipelineAvailable) { showPipeline(p); return; }
  }
  if (mapView === "arch") await loadArch();
  else if (mapView === "dsm") await loadDsm();
  else if (mapView === "flow") await loadFlowEntries();
  else if (mapView === "pipeline") await loadPipeline();
  else await loadNamespaceMap();
}

function applyMapView(view: MapViewKind): void {
  mapView = view;
  // DSM / 入口流程都是容器图往下钻的一层，切换按钮上仍算"容器"
  $("view-arch").classList.toggle("active", view === "arch" || view === "dsm" || view === "flow");
  $("view-pipeline").classList.toggle("active", view === "pipeline");
  $("view-ns").classList.toggle("active", view === "ns");
  $("arch-cy").style.display = view === "arch" ? "" : "none";
  $("dsm-view").style.display = view === "dsm" ? "" : "none";
  $("flow-view").style.display = view === "flow" ? "" : "none";
  $("pipe-cy").style.display = view === "pipeline" ? "" : "none";
  $("map-cy").style.display = view === "ns" ? "" : "none";
  // 叙述卡只对容器 / 组件 / 入口流程有意义；管道与命名空间视图不占右侧
  $("narration").style.display = layoutKindFor(view) ? "" : "none";
  renderLayoutSelect();
}

// ---------- 版式预设（AI 只在这里面选）----------

function layoutKindFor(view: MapViewKind | null): NarrativeKind | null {
  return view === "arch" ? "container" : view === "dsm" ? "component" : view === "flow" ? "flow" : null;
}

/** 面包屑栏右侧的「版式」下拉：只在容器 / 组件矩阵 / 入口流程三个视图显示，选项随视图换。 */
function renderLayoutSelect(): void {
  const kind = layoutKindFor(mapView);
  const wrap = $("map-layout-wrap");
  wrap.style.display = kind ? "" : "none";
  if (!kind) return;
  const sel = $<HTMLSelectElement>("map-layout");
  sel.innerHTML = LayoutPresets[kind].map((p) => `<option value="${esc(p.id)}" title="${esc(p.title)}">${esc(p.label)}</option>`).join("");
  sel.value = presets[kind];
}
$("map-layout").onchange = () => {
  const kind = layoutKindFor(mapView);
  if (kind) applyPreset(kind, $<HTMLSelectElement>("map-layout").value);
};

/** 切预设：手动下拉和叙述卡的「按 AI 建议排版」都走这里。不认识的 id 忽略。 */
function applyPreset(kind: NarrativeKind, id: string): void {
  if (!LayoutPresets[kind].some((p) => p.id === id)) { toast(`不认识的版式 ${id}`); return; }
  const changed = presets[kind] !== id;
  presets[kind] = id;
  if (kind === "container") { if (archDto && archRenderer) archRenderer.render(archDto, id); }
  else if (kind === "component") { if (dsmDto && dsmView) { dsmShown = reorderDsm(dsmDto, id); dsmView.render(dsmShown); } }
  else flowView?.setPreset(id);
  renderLayoutSelect();
  card?.refresh();
  if (changed) toast(`版式：${presetLabel(kind, id)}`);
}

// ---------- 叙述卡（M5d）----------

function showContainerCard(c: ArchNodeDto): void {
  card?.show("container", c.key, c.name);
}

/** 点证据标签：node → 容器图选中；edge → 容器图选中边；entry / sym → 调用图聚焦；comp / cell → 组件矩阵里闪一下。 */
async function onEvidence(ref: EvidenceRef, raw: string): Promise<void> {
  const ensureArch = async () => {
    if (mapView !== "arch") { applyMapView("arch"); archRenderer?.resize(); await loadArch(); }
  };
  switch (ref.kind) {
    case "node": {
      await ensureArch();
      if (archRenderer?.selectNode(ref.ref)) {
        const n = archDto?.nodes.find((x) => x.key === ref.ref);
        if (n) showArchNodeDetails(n, false);
      } else toast(`容器图里没有 ${ref.ref}`);
      return;
    }
    case "edge": {
      const e = parseEdgeEvidence(ref.ref);
      if (!e) { toast(raw); return; }
      await ensureArch();
      if (archRenderer?.selectEdge(e.from, e.to, e.protocol)) {
        const edge = archDto?.edges.find((x) => x.from === e.from && x.to === e.to && (e.protocol === null || x.protocol === e.protocol));
        if (edge) showArchEdgeDetails(edge);
      } else toast(`容器图里没有这条边：${e.from} → ${e.to}`);
      return;
    }
    case "entry":
    case "sym": {
      const id = Number(ref.ref);
      if (Number.isInteger(id) && api) await focusSymbolById(id);
      else toast(raw);
      return;
    }
    case "comp": {
      if (mapView === "dsm" && highlightDsm(ref.ref, null)) return;
      toast(`组件 ${ref.ref}（在组件矩阵视图里可定位）`);
      return;
    }
    case "cell": {
      const arrow = ref.ref.indexOf("→");
      if (arrow > 0 && mapView === "dsm" && highlightDsm(ref.ref.slice(0, arrow), ref.ref.slice(arrow + 1))) return;
      toast(`依赖格 ${ref.ref}（在组件矩阵视图里可定位）`);
      return;
    }
    default:
      toast(raw);
  }
}

/**
 * 在当前 DSM 表格里让一个行头（colKey 为 null）或一个格子闪一下。
 * 依赖 dsmView 的表结构：tbody > tr（每行先一个 th 再 N 个 td），下标按 dsmShown 的组件顺序。
 */
function highlightDsm(rowKey: string, colKey: string | null): boolean {
  const comps = dsmShown?.components ?? [];
  const r = comps.findIndex((c) => c.key === rowKey);
  if (r < 0) return false;
  const tr = $("dsm-view").querySelectorAll<HTMLElement>("tbody tr")[r];
  if (!tr) return false;
  let target: HTMLElement | null;
  if (colKey === null) target = tr.querySelector("th");
  else {
    const c = comps.findIndex((x) => x.key === colKey);
    if (c < 0) return false;
    target = (tr.children[c + 1] as HTMLElement | undefined) ?? null;
  }
  if (!target) return false;
  target.scrollIntoView({ block: "nearest", inline: "nearest" });
  target.classList.remove("ca-flash");
  void target.offsetWidth; // 重新触发动画
  target.classList.add("ca-flash");
  const el = target;
  window.setTimeout(() => el.classList.remove("ca-flash"), 1700);
  return true;
}

function setMapView(view: MapViewKind): void {
  if (mapView === view) return;
  // 用户回到容器图时，组件 / 入口的叙述已不在画面上（证据定位走 applyMapView，不清卡片）
  if (view === "arch" && card && card.currentKind !== null && card.currentKind !== "container") card.clear();
  applyMapView(view);
  if (view === "arch") archRenderer?.resize(); else if (view === "pipeline") pipelineRenderer?.resize(); else if (view === "ns") mapRenderer?.resize();
  void loadMapView();
}

// ---------- 组件 DSM（L2）----------

function openDsm(container: ArchNodeDto): void {
  dsmContainer = container;
  dsmPath = [];
  applyMapView("dsm");
  showContainerCard(container);
  void loadDsm();
}

async function loadDsm(): Promise<void> {
  if (!api || !dsmContainer) { setMapView("arch"); return; }
  const root = dsmPath[dsmPath.length - 1] ?? "";
  const d = await api.dsm(dsmContainer.key, root);
  if (d === null) { toast("服务没有 /api/arch/dsm，或容器已不存在"); setMapView("arch"); return; }
  dsmDto = d;
  dsmView ??= new DsmView($("dsm-view"), {
    onCellSelect: showDsmCellDetails,
    onComponentSelect: showDsmComponentDetails,
    onComponentDoubleClick: (c) => {
      if (!c.hasChildren) { toast(`${c.label} 下面没有更深的层级`); return; }
      dsmPath.push(c.key);
      void loadDsm();
    },
  });
  // 版式预设只在页面这层套：服务端给的是依赖序，字母序由 reorderDsm 重排并同步 cells / coupling 下标
  dsmShown = reorderDsm(d, presets.component);
  dsmView.render(dsmShown);
  renderContainerCrumbs("dsm");
  const cyc = d.cycles.length > 0 ? `，${d.cycles.length} 个环（红框）` : "，没有环";
  const cpl = d.coupling.length > 0 ? `，${d.coupling.length} 对隐性耦合（虚框 ~）` : "";
  const git = d.gitAvailable ? "" : "；没有 git 统计";
  detailsEl.innerHTML = `<span class="kind">DSM</span><span>${esc(dsmContainer.name)}${root ? " › " + esc(root) : ""}：${d.components.length} 个组件，${d.cells.length} 个依赖格${cyc}${cpl}${git}${d.note ? "；" + esc(d.note) : ""}</span><span class="muted">行依赖列；双击带下划线的行头钻入</span>`;
}

/**
 * 容器层的面包屑：项目 › 容器 [组件矩阵 | 入口流程] › …。
 * DSM 后面跟钻取路径；入口流程后面跟「入口流程 › 入口」。
 */
function renderContainerCrumbs(sub: "dsm" | "flow"): void {
  const crumbs = $("map-crumb-list");
  crumbs.innerHTML = "";
  const add = (label: string, current: boolean, onClick: () => void) => {
    if (crumbs.childElementCount > 0) crumbs.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (current ? " current" : "");
    el.textContent = label;
    el.onclick = onClick;
    crumbs.appendChild(el);
  };
  add("项目", false, () => setMapView("arch"));
  add(dsmContainer?.name ?? "容器", sub === "dsm" && dsmPath.length === 0, () => { if (dsmContainer) openDsm(dsmContainer); });
  const tabs = document.createElement("span");
  tabs.className = "subtabs";
  const tab = (label: string, title: string, active: boolean, onClick: () => void) => {
    const b = document.createElement("button");
    b.className = "tab" + (active ? " active" : "");
    b.textContent = label;
    b.title = title;
    b.onclick = onClick;
    tabs.appendChild(b);
  };
  tab("组件矩阵", "组件依赖矩阵（DSM）：行依赖列，带环与 git 耦合", sub === "dsm", () => { if (dsmContainer && mapView !== "dsm") openDsm(dsmContainer); });
  tab("入口流程", "从一个入口（路由 / 任务 / Handler）出发的调用时序", sub === "flow", () => { if (dsmContainer && mapView !== "flow") openFlow(dsmContainer); });
  crumbs.appendChild(tabs);
  if (sub === "dsm") {
    dsmPath.forEach((key, i) => add(key.slice(Math.max(key.lastIndexOf("."), key.lastIndexOf("/")) + 1), i === dsmPath.length - 1, () => { dsmPath = dsmPath.slice(0, i + 1); void loadDsm(); }));
  } else {
    add("入口流程", flowEntry === null, () => { flowEntry = null; flowDto = null; if (dsmContainer) showContainerCard(dsmContainer); void loadFlowEntries(); });
    if (flowEntry) add(entryTitle(flowEntry), true, () => {});
  }
}

// ---------- 入口流程（L3）----------

function openFlow(container: ArchNodeDto): void {
  if (flowContainerKey !== container.key) { flowContainerKey = container.key; flowEntries = []; flowEntry = null; flowDto = null; }
  dsmContainer = container;
  applyMapView("flow");
  if (flowEntry) card?.show("flow", String(flowEntry.symbolId), entryTitle(flowEntry));
  else showContainerCard(container);
  void loadFlowEntries();
}

async function loadFlowEntries(): Promise<void> {
  if (!api || !dsmContainer) { setMapView("arch"); return; }
  flowContainerKey = dsmContainer.key;
  flowEntries = await api.entries(dsmContainer.key);
  flowView ??= new FlowView($("flow-view"), {
    onEntrySelect: (e) => void selectFlowEntry(e),
    onNodeFocus: showFlowNodeDetails,
  });
  flowView.renderEntries(flowEntries, flowEntry?.symbolId ?? null);
  if (flowEntry && flowDto) flowView.renderFlow(flowDto);
  else flowView.clearFlow();
  renderContainerCrumbs("flow");
  if (flowEntry && flowDto) { showFlowEntryDetails(flowEntry, flowDto); return; }
  const counts = new Map<string, number>();
  for (const e of flowEntries) counts.set(e.kind, (counts.get(e.kind) ?? 0) + 1);
  const parts = [...counts.entries()].map(([k, n]) => `${EntryKindLabels[k] ?? k} ${n}`).join("，");
  detailsEl.innerHTML = `<span class="kind">入口</span><span>${esc(dsmContainer.name)}：${flowEntries.length} 个入口${parts ? "（" + esc(parts) + "）" : ""}</span><span class="muted">${flowEntries.length ? "点一个入口看时序；点行展开 / 折叠，点名字切到调用图" : "没有发现入口，或服务没有 /api/arch/entries"}</span>`;
}

async function selectFlowEntry(e: EntryDto): Promise<void> {
  if (!api) return;
  flowEntry = e;
  flowDto = null;
  flowView?.renderEntries(flowEntries, e.symbolId);
  flowView?.clearFlow("正在加载…");
  renderContainerCrumbs("flow");
  // 流程叙述的 key 是入口 symbolId 的十进制字符串
  card?.show("flow", String(e.symbolId), entryTitle(e));
  const f = await api.flow(e.symbolId);
  if (flowEntry !== e) return; // 等待期间用户又点了别的
  if (f === null) { toast("服务没有 /api/arch/flow，或入口已不在索引里"); flowView?.clearFlow("拿不到这个入口的流程"); return; }
  flowDto = f;
  flowView?.renderFlow(f);
  showFlowEntryDetails(e, f);
}

function showFlowEntryDetails(e: EntryDto, f: FlowDto | null): void {
  const kind = EntryKindLabels[e.kind] ?? e.kind;
  const where = `${e.file}:${e.line}`;
  const stats = f ? `<span class="muted">${f.actors.length} 个参与者 · ${f.nodes} 个节点${f.truncated ? " · 已截断" : ""}</span>` : "";
  detailsEl.innerHTML = `<span class="kind">${esc(kind)}</span><span class="mono" title="${esc(e.qualifiedName)}">${esc(entryTitle(e))}</span><span class="muted">${esc(where)}</span><a id="open-link">在编辑器中打开</a>${stats}`;
  detailsEl.querySelector<HTMLAnchorElement>("#open-link")?.addEventListener("click", () => {
    host.post({ type: "open", path: e.file, line: e.line, col: 1 });
    if (!host.isVsCode) toast(`（浏览器模式）打开 ${where}`);
  });
}

function showFlowNodeDetails(node: FlowNodeDto): void {
  if (node.symbolId !== null) void focusSymbolById(node.symbolId);
}

function showDsmComponentDetails(c: DsmComponentDto): void {
  // 组件叙述的 key = <容器键>|<组件键>，钻入后的命名空间层也一样
  if (dsmContainer) card?.show("component", `${dsmContainer.key}|${c.key}`, c.label);
  const hot = c.hot ? `<span class="muted">git：修订 ${c.hot.revisions} 次，+${c.hot.added}/−${c.hot.deleted} 行，最近 ${c.hot.lastCommit}（${c.hot.ageMonths} 个月前）</span>` : "";
  const cycle = dsmDto?.cycles.find((cy) => cy.includes(c.key));
  const cyc = cycle ? `<span class="warn">环：${esc(cycle.map((k) => k.slice(Math.max(k.lastIndexOf("."), k.lastIndexOf("/")) + 1)).join(" ↔ "))}</span>` : "";
  detailsEl.innerHTML = `<span class="kind">组件</span><span class="mono">${esc(c.key)}</span><span class="muted">${c.symbols} 个符号 · ${c.files} 个文件${c.hasChildren ? " · 可钻入" : ""}</span>${hot}${cyc}`;
}

function showDsmCellDetails(cell: DsmCellModel, from: DsmComponentDto, to: DsmComponentDto): void {
  const kinds = cell.cell ? Object.entries(cell.cell.kinds).map(([k, n]) => `${k} ${n}`).join("，") : "没有静态依赖";
  const coupled = cell.coupled ? `<span class="warn">常一起改：共同修订 ${cell.coupled.shared} 次，耦合度 ${Math.round(cell.coupled.degree * 100)}%${cell.cell ? "" : "，却没有静态边"}</span>` : "";
  const cyc = cell.cycle ? `<span class="warn">环依赖</span>` : "";
  detailsEl.innerHTML = `<span class="kind">依赖</span><span class="mono">${esc(from.label)} → ${esc(to.label)}</span><span class="muted">${esc(kinds)}</span>${coupled}${cyc}`;
}

// ---------- 容器图（L1）----------

async function loadArch(): Promise<void> {
  if (!api) return;
  const a = await api.arch();
  archDto = a;
  if (a === null) { toast("服务没有 /api/arch/containers，切回命名空间视图"); setMapView("ns"); return; }
  showArch(a);
}

function showArch(a: ArchDto): void {
  archRenderer ??= new ArchRenderer($("arch-cy"), {
    onNodeSelect: showArchNodeDetails,
    onEdgeSelect: showArchEdgeDetails,
    onContainerDoubleClick: (n) => openDsm(n),
  });
  archRenderer.render(a, presets.container);
  const crumbs = $("map-crumb-list");
  crumbs.innerHTML = '<span class="crumb current">项目</span>';
  const containers = a.nodes.filter((n) => n.kind === "container").length;
  const externals = a.nodes.filter((n) => n.kind === "external").length;
  const firm = a.edges.filter((e) => e.confidence >= 0.7).length;
  const parts = [`${containers} 个容器`, `${externals} 个外部系统`, `${a.edges.length} 条边（其中 ${firm} 条按配置或路由对上）`];
  if (a.conflicts.length > 0) parts.push(`声明与发现冲突 ${a.conflicts.length} 处`);
  detailsEl.innerHTML = `<span class="kind">容器图</span><span>${esc(parts.join("，"))}</span><span class="muted">双击容器钻入；点边看证据</span>`;
}

function evidenceLinks(list: EvidenceDto[], max = 6): string {
  const items = list.slice(0, max).map((e) => `<span class="mono" title="${esc(e.note ?? "")}">${esc(e.file)}${e.line !== null ? ":" + e.line : ""}</span>`);
  if (list.length > max) items.push(`<span class="muted">…还有 ${list.length - max} 处</span>`);
  return items.join("，");
}

/** 单击容器：详情条照旧 + 叙述卡切到该容器（只 GET，不自动排队）。updateCard=false 用于证据定位，不动叙述卡。 */
function showArchNodeDetails(n: ArchNodeDto | null, updateCard = true): void {
  if (!n) return;
  if (updateCard) {
    if (n.kind === "container") showContainerCard(n);
    else card?.clear("外部系统 / 使用者没有叙述；单击一个容器看它的。");
  }
  const sub = SubKindLabels[n.subKind] ?? n.subKind;
  const extra = n.kind === "container"
    ? `<span class="muted">成员 ${n.members.length} 个项目${n.ports.length ? " · 端口 " + n.ports.join("/") : ""}${n.outbound ? " · " + n.outbound + " 处出口" : ""}</span>`
    : `<span class="muted">${n.source === "declared" ? "来自 architecture.yaml 声明" : "静态发现"}</span>`;
  detailsEl.innerHTML = `<span class="kind">${esc(sub)}</span><span class="mono">${esc(n.name)}</span>${extra}<span>证据：${evidenceLinks(n.evidence)}</span>`;
}

function showArchEdgeDetails(e: ArchEdgeDto | null): void {
  if (!e || !archDto) return;
  const name = (k: string) => archDto!.nodes.find((n) => n.key === k)?.name ?? k;
  const tier = e.source === "declared" ? "声明（1.0）" : e.confidence >= 0.9 ? "按路由对上（0.9）" : e.confidence >= 0.7 ? "按配置地址对上（0.7）" : "只知道客户端（0.5）";
  const addr = [e.configKey ? `配置 ${e.configKey}` : "", e.url ?? ""].filter(Boolean).join(" → ");
  detailsEl.innerHTML = `<span class="kind">${esc(e.protocol)}</span><span class="mono">${esc(name(e.from))} → ${esc(name(e.to))}</span><span class="muted">${esc(tier)}${addr ? " · " + esc(addr) : ""}</span><span>证据：${evidenceLinks(e.evidence)}</span>`;
}

$("view-arch").onclick = () => setMapView("arch");

function renderMapCrumbs(s: MapState, reload: () => void): void {
  const crumbs = $("map-crumb-list");
  crumbs.innerHTML = "";
  s.crumbs.forEach((c, i) => {
    if (i > 0) crumbs.insertAdjacentHTML("beforeend", '<span class="sep">›</span>');
    const el = document.createElement("span");
    el.className = "crumb" + (c.root === s.root ? " current" : "");
    el.textContent = c.label;
    el.onclick = () => { s.goTo(c.root); reload(); };
    crumbs.appendChild(el);
  });
}

async function loadNamespaceMap(): Promise<void> {
  if (!api) return;
  mapRenderer ??= new MapRenderer($("map-cy"), {
    onGroupDoubleClick: (g) => {
      if (g.symbolId !== null) { void focusSymbolById(g.symbolId); return; }
      mapState.drill(g.key);
      void loadNamespaceMap();
    },
    onGroupSelect: showMapGroupDetails,
    onEdgeSelect: showMapEdgeDetails,
  });
  const map = await api.map(mapState.root);
  mapRenderer.render(map);
  // 面包屑回到"项目"时，有容器图就回容器图，而不是整库的命名空间顶层
  renderMapCrumbs(mapState, () => { if (mapState.root === "" && archDto && archDto.nodes.length > 0) setMapView("arch"); else void loadNamespaceMap(); });
  const total = map.groups.reduce((n, g) => n + g.symbols, 0);
  detailsEl.innerHTML = `<span class="kind">地图</span><span>${esc(mapState.root || "整个项目")}：${map.groups.length} 个${map.groups.some((g) => g.symbolId !== null) ? "节点" : "模块"}，${total} 个符号，${map.edges.length} 条依赖</span><span class="muted">双击模块钻入，双击类型切到调用图</span>`;
}

// ---------- 管道视图 ----------

function focusSymbolById(id: number): Promise<void> {
  return api!.symbol(id).then((s) => { if (s) { setMode("graph"); void focusSymbol(s); } else toast("符号在索引里找不到了"); });
}

async function loadPipeline(): Promise<void> {
  if (!api) return;
  const p = await api.pipeline(pipeState.root);
  if (p === null) { toast("服务没有 /api/pipeline，切回命名空间视图"); setMapView("ns"); return; }
  showPipeline(p);
}

function showPipeline(p: PipelineDto): void {
  pipelineRenderer ??= new PipelineRenderer($("pipe-cy"), {
    onCellSelect: showPipelineCellDetails,
    onBehaviorsSelect: showPipelineBehaviors,
    onFlowSelect: showPipelineFlowDetails,
    onCellDoubleClick: (row, kind) => {
      if (row.hasChildren) { pipeState.drill(row.key); void loadPipeline(); return; }
      // 叶子行：消息格子聚焦消息类型；入口 / Handler 格子聚焦第一个代表项，其余在详情条里点
      const target = kind === "message" || kind === "label" ? row.symbolId
        : kind === "entry" ? row.entries.items[0]?.symbolId ?? null
        : kind === "handler" ? row.handlers.items[0]?.symbolId ?? null
        : null;
      if (target !== null) void focusSymbolById(target);
    },
  });
  // root 传空时服务端会自动落到业务模块层（defaultRoot）；面包屑以它为底，上面的项目 / 类库层不显示
  if (pipeState.root !== p.root) pipeState.goToPathFrom(p.defaultRoot, p.root);
  pipelineRenderer.render(p);
  renderMapCrumbs(pipeState, () => void loadPipeline());
  // items 最多只带 20 个代表项，总数要用 count 累加（同一个 Handler / 入口跨行会重复计，所以叫"按行累计"）
  const messages = p.rows.reduce((n, r) => n + r.messages.count, 0);
  const handlers = p.rows.reduce((n, r) => n + r.handlers.count, 0);
  const entries = p.rows.reduce((n, r) => n + r.entries.count, 0);
  const nested = p.rows.reduce((n, r) => n + r.nested, 0);
  const bypass = p.rows.reduce((n, r) => n + r.bypass, 0);
  const parts = [
    `${p.rows.length} 条泳道`, `${messages} 个消息`, `${handlers} 个 Handler`, `${entries} 个入口（按行累计）`,
    `行为链 ${p.behaviors.length}（${p.behaviorsOrdered ? "按注册顺序" : "顺序未知"}）`,
  ];
  if (nested > 0) parts.push(`嵌套分发 ${nested}`);
  if (bypass > 0) parts.push(`绕过管道 ${bypass}`);
  if (p.testEntriesSkipped > 0 || p.testMessagesSkipped > 0) parts.push(`已排除测试代码：入口 ${p.testEntriesSkipped} 个类型、消息 ${p.testMessagesSkipped} 个`);
  if (p.messagesOutsideRoot > 0) parts.push(`本级之外还有 ${p.messagesOutsideRoot} 个消息`);
  detailsEl.innerHTML = `<span class="kind">管道</span><span>${esc(p.root || "整个项目")}：${esc(parts.join("，"))}</span><span class="muted">${p.leaf ? "双击消息切到调用图" : "双击一行钻入"}</span>`;
}

function itemLinks(items: PipelineItemDto[], count: number): string {
  const links = items.map((i) => `<a data-sym="${i.symbolId}" title="${esc(i.qualifiedName)}">${esc(i.name)}</a>${i.weight > 1 ? `<span class="muted">×${i.weight}</span>` : ""}`).join("，");
  return count > items.length ? `${links}<span class="muted">…还有 ${count - items.length} 个</span>` : links;
}

function showPipelineCellDetails(row: PipelineRowDto | null, kind: CellKind | null): void {
  if (!row || !kind) return;
  const m = row.messages;
  const body = kind === "entry" ? `<span class="kind">入口</span><span>${row.entries.count} 个类型向 ${esc(row.label)} 分发过</span><span>${itemLinks(row.entries.items, row.entries.count)}</span>`
    : kind === "message" ? `<span class="kind">消息</span><span>${esc(row.label)}：${m.count} 个（命令 ${m.commands}、查询 ${m.queries}、事件 ${m.events}）</span>${m.orphans > 0 ? `<span class="warn">${m.orphans} 个没有任何非测试入口分发过</span>` : ""}`
    : kind === "handler" ? `<span class="kind">Handler</span><span>${row.handlers.count} 个</span><span>${itemLinks(row.handlers.items, row.handlers.count)}</span>${row.nested > 0 ? `<span class="muted">嵌套分发 ${row.nested}</span>` : ""}${row.bypass > 0 ? `<span class="warn">被直接调用 ${row.bypass} 次（绕过管道）</span>` : ""}`
    : kind === "downstream" ? `<span class="kind">下游</span><span>${row.downstream.map((g) => `<span class="mono" title="${esc(g.key)}">${esc(g.label)}</span> ${g.weight}`).join("，") || "无仓库内下游"}</span>${row.externalCalls > 0 ? `<span class="muted">框架调用 ${row.externalCalls}</span>` : ""}`
    : `<span class="kind">${row.symbolId !== null ? "消息" : "模块"}</span><span class="mono">${esc(row.key)}</span><span class="muted">${row.hasChildren ? "双击钻入" : "双击切到调用图"}</span>`;
  detailsEl.innerHTML = body;
}

function showPipelineBehaviors(behaviors: PipelineBehaviorDto[]): void {
  const chain = behaviors.map((b) => `<a data-sym="${b.symbolId}" title="${esc(b.qualifiedName)}">${esc(b.name)}</a>`).join(" → ");
  const ordered = pipelineOrderedNote();
  detailsEl.innerHTML = `<span class="kind">行为链</span><span>${chain || "没有发现实现 *Behavior 接口的类型"}</span>${ordered}`;
}
let lastPipelineOrdered: boolean | null = null;
function pipelineOrderedNote(): string {
  if (lastPipelineOrdered === null) return "";
  return lastPipelineOrdered ? `<span class="muted">顺序来自 DI 注册</span>` : `<span class="warn">没有 DI 注册记录（需要 Roslyn 索引），顺序按名字，不可信</span>`;
}

function showPipelineFlowDetails(f: PipelineFlowDto | null): void {
  if (!f) return;
  const text = f.kind === "nested"
    ? `${esc(f.from)} 的 Handler 里又分发了 ${esc(f.to)} 的消息，${f.weight} 处`
    : `${esc(f.from)} 的入口直接调用了 ${esc(f.to)} 的 Handler 方法（绕过管道），${f.weight} 处`;
  detailsEl.innerHTML = `<span class="kind">${f.kind === "nested" ? "嵌套分发" : "绕过"}</span><span>${text}</span>`;
}

detailsEl.addEventListener("click", (e) => {
  const a = (e.target as HTMLElement).closest("a[data-sym]") as HTMLElement | null;
  if (a && api) void focusSymbolById(Number(a.dataset["sym"]));
});
$("view-pipeline").onclick = () => setMapView("pipeline");
$("view-ns").onclick = () => setMapView("ns");

function showMapGroupDetails(g: MapGroupDto | null): void {
  if (!g) return;
  detailsEl.innerHTML = `<span class="kind">${g.symbolId !== null ? "类型" : "模块"}</span><span class="mono">${esc(g.key)}</span>` +
    `<span class="muted">${g.symbols} 个符号 · ${g.files} 个文件${g.hasChildren ? " · 可钻入" : ""}</span>`;
}

function showMapEdgeDetails(e: MapEdgeDto | null): void {
  if (!e) return;
  const kinds = Object.entries(e.kinds).map(([k, n]) => `${k} ${n}`).join("，");
  detailsEl.innerHTML = `<span class="kind">依赖</span><span class="mono">${esc(e.from)} → ${esc(e.to)}</span><span class="muted">${e.weight} 条：${esc(kinds)}</span>`;
}

$("mode-graph").onclick = () => setMode("graph");
$("mode-map").onclick = () => setMode("map");

// ---------- 索引与连接 ----------

let statusTimer = 0;
let sawRunning = false;
async function checkHealth(): Promise<void> {
  if (!api) return;
  try {
    const h = await api.health();
    if (h.files === 0) {
      showOverlay("这个仓库还没有索引", `${h.repo}`, true);
    } else {
      overlay.classList.remove("open");
    }
    const st = await api.status();
    if (st.running) pollStatus();
  } catch (e) {
    showOverlay("连不上 CodeAtlas 服务", String(e), false);
  }
}
function showOverlay(title: string, text: string, showIndexButton: boolean): void {
  $("ov-title").textContent = title;
  $("ov-text").textContent = text;
  $<HTMLButtonElement>("ov-index").style.display = showIndexButton ? "inline-block" : "none";
  $("progress").style.display = "none";
  overlay.classList.add("open");
}
async function startIndex(full: boolean): Promise<void> {
  if (!api) return;
  // VS Code 里由扩展发起（它要更新状态栏）；浏览器调试模式下页面自己调服务
  if (host.isVsCode) host.post({ type: "requestIndex", full });
  else if ((await api.startIndex(full)) === "busy") toast("索引正在进行中");
  sawRunning = true;
  pollStatus();
}
function pollStatus(): void {
  window.clearTimeout(statusTimer);
  statusTimer = window.setTimeout(async () => {
    if (!api) return;
    const s = await api.status();
    if (s.running) {
      sawRunning = true;
      $("ov-title").textContent = "正在建立索引";
      $("ov-text").textContent = `${s.phase ?? ""} ${s.done}/${s.total}`;
      $("progress").style.display = "block";
      ($("progress").firstElementChild as HTMLElement).style.width = s.total ? `${Math.round((s.done / s.total) * 100)}%` : "10%";
      $<HTMLButtonElement>("ov-index").style.display = "none";
      overlay.classList.add("open");
      pollStatus();
    } else if (sawRunning) {
      // 只有真正看到过一次"进行中"才算一次索引结束，避免 init 时误关覆盖层、误弹"完成"
      sawRunning = false;
      overlay.classList.remove("open");
      if (s.error) toast(`索引失败：${s.error}`, 6000);
      else if (s.lastReport) toast(`索引完成：${s.lastReport.totalSymbols} 个符号，${s.lastReport.elapsedSeconds.toFixed(1)} 秒`);
      await refreshFocus();
    }
  }, 500);
}
async function refreshFocus(): Promise<void> {
  if (!api || !state.focusNodeId) return;
  const old = state.getNode(state.focusNodeId)?.symbol;
  if (!old) return;
  // 增量索引会重建被改文件的符号，id 会变：按 id → 原位置 → 限定名 依次找回
  const s = await resolveRefreshedSymbol(api, old);
  if (s) await focusSymbol(s, true);
  else toast("焦点符号在新索引里找不到了");
}
$("ov-index").onclick = () => void startIndex(false);
$("reindex").onclick = () => void startIndex(false);
$("chain-down").onclick = () => { if (state.focusNodeId) void expandChain(state.focusNodeId, "out"); };
$("chain-up").onclick = () => { if (state.focusNodeId) void expandChain(state.focusNodeId, "in"); };
$("show-refs").onchange = () => { void refreshFocus(); };
$("show-tests").onchange = () => { void refreshFocus(); };
$("show-conduit").onchange = () => { void refreshFocus(); };
$("branch-width").onchange = () => { void refreshFocus(); };

// ---------- 启动 ----------

let currentRepo: string | null = null;
function init(apiBase: string, token: string | null, repoRoot: string | null): void {
  api = new Api(apiBase.replace(/\/$/, ""), token);
  if (repoRoot !== currentRepo) {
    // 切到另一个仓库：旧图里的符号 id 对新服务无意义
    currentRepo = repoRoot;
    state.reset();
    mapState.reset();
    pipeState.reset();
    mapView = null;
    archDto = null;
    dsmContainer = null; dsmPath = []; dsmDto = null; dsmShown = null;
    flowContainerKey = null; flowEntries = []; flowEntry = null; flowDto = null;
    flowView?.renderEntries([], null); flowView?.clearFlow();
    card?.clear();
    render();
    showNodeDetails(null);
  }
  // 叙述卡：数据与跳转都经回调回到这里；api 为空时一律当 404（服务版本太旧）
  card ??= new NarrationCard($("narration"), {
    fetch: (kind, key) => (api ? api.narrative(kind, key) : Promise.resolve(null)),
    generate: (kind, key, force) => (api ? api.narrate(kind, key, force) : Promise.resolve(null)),
    status: () => (api ? api.narrateStatus() : Promise.resolve(null)),
    control: (action) => (api ? api.narrateControl(action) : Promise.resolve(null)),
    onEvidence: (ref, raw) => void onEvidence(ref, raw),
    onApplyLayout: (kind, layout) => applyPreset(kind, layout),
    currentPreset: (kind) => presets[kind],
    onToggle: () => { archRenderer?.resize(); pipelineRenderer?.resize(); mapRenderer?.resize(); },
    runCommand: (command) => {
      if (host.isVsCode) host.post({ type: "command", command });
      else toast(`（浏览器模式）请在 VS Code 命令面板运行 ${command}`);
    },
  });
  renderer ??= new Renderer($("cy"), {
    onNodeDoubleClick: (n) => {
      if (n.type === "ambiguous") void expandCapsule(n);
      else if (n.type === "symbol" && n.symbol && (n.expanded || !n.expandable)) { if (n.symbol.kind !== "external") openInEditor(n.symbol); }
      else void expandNode(n);
    },
    onNodeContext: showContextMenu,
    onNodeSelect: showNodeDetails,
    onEdgeSelect: showEdgeDetails,
  });
  void checkHealth();
}

host.onMessage((msg) => {
  if (msg.type === "init") init(msg.apiBase, msg.token ?? null, msg.repoRoot ?? null);
  else if (msg.type === "focus") { if (mode !== "graph") setMode("graph"); void api?.symbol(msg.symbolId).then((s) => s && focusSymbol(s)); }
  else if (msg.type === "indexChanged") { sawRunning = false; overlay.classList.remove("open"); void refreshFocus(); }
});
window.addEventListener("resize", () => { renderer?.resize(); mapRenderer?.resize(); pipelineRenderer?.resize(); archRenderer?.resize(); });

const params = new URLSearchParams(location.search);
const apiParam = params.get("api");
if (apiParam) init(apiParam, params.get("token"), "browser");
host.post({ type: "ready" });
